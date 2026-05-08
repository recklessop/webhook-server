using System.Diagnostics;
using System.Text;
using WebhookServer.Core.Models;

namespace WebhookServer.Core.Execution;

public sealed class ProcessExecutor : IExecutor
{
    /// <summary>Per-stream cap on captured output (excess is dropped and StdoutTruncated set).</summary>
    public const int MaxOutputBytes = 1 * 1024 * 1024;

    public async Task<ExecutionResult> RunAsync(EndpointConfig endpoint, ExecutionContext ctx, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var psi = BuildStartInfo(endpoint, ctx);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        try
        {
            if (!process.Start())
            {
                return Failed(ctx.RunId, startedAt, "process failed to start");
            }
        }
        catch (Exception ex)
        {
            return Failed(ctx.RunId, startedAt, $"launch error: {ex.Message}");
        }

        // stdin
        if (endpoint.DataPassing.StdinJson)
        {
            try
            {
                if (ctx.BodyBytes.Length > 0)
                    await process.StandardInput.BaseStream.WriteAsync(ctx.BodyBytes, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Failed(ctx.RunId, startedAt, $"stdin write failed: {ex.Message}");
            }
            finally
            {
                try { process.StandardInput.Close(); } catch { /* swallow */ }
            }
        }
        else
        {
            try { process.StandardInput.Close(); } catch { /* swallow */ }
        }

        // Capture stdout/stderr in parallel, with per-stream cap.
        var stdoutTask = ReadCappedAsync(process.StandardOutput, ct);
        var stderrTask = ReadCappedAsync(process.StandardError, ct);

        var timeout = TimeSpan.FromSeconds(Math.Max(1, endpoint.TimeoutSeconds));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { /* swallow */ }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* swallow */ }
        }

        var (stdout, stdoutTrunc) = await stdoutTask.ConfigureAwait(false);
        var (stderr, stderrTrunc) = await stderrTask.ConfigureAwait(false);

        return new ExecutionResult
        {
            RunId = ctx.RunId,
            ExitCode = timedOut ? -1 : process.ExitCode,
            Stdout = stdout,
            Stderr = stderr,
            StdoutTruncated = stdoutTrunc,
            StderrTruncated = stderrTrunc,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            TimedOut = timedOut,
        };
    }

    private static ProcessStartInfo BuildStartInfo(EndpointConfig endpoint, ExecutionContext ctx)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = string.IsNullOrEmpty(endpoint.WorkingDirectory)
                ? Environment.CurrentDirectory
                : endpoint.WorkingDirectory!,
        };

        switch (endpoint.ExecutorType)
        {
            case ExecutorType.WindowsPowerShell:
                psi.FileName = "powershell.exe";
                AddPwshArgs(psi, endpoint);
                break;
            case ExecutorType.PwshCore:
                psi.FileName = "pwsh.exe";
                AddPwshArgs(psi, endpoint);
                break;
            case ExecutorType.Cmd:
                psi.FileName = "cmd.exe";
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(ResolveCmdInvocation(endpoint));
                break;
            case ExecutorType.Executable:
                psi.FileName = endpoint.ExecutablePath ?? "";
                foreach (var staticArg in endpoint.ExecutableArgs)
                    psi.ArgumentList.Add(staticArg);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(endpoint.ExecutorType));
        }

        if (endpoint.DataPassing.ArgTemplate)
        {
            foreach (var arg in ArgTemplateRenderer.Render(endpoint.DataPassing.ArgTemplateString, ctx))
                psi.ArgumentList.Add(arg);
        }

        if (endpoint.DataPassing.EnvVars)
        {
            foreach (var (k, v) in ctx.Headers)
                psi.Environment[$"WEBHOOK_HEADER_{Sanitize(k)}"] = v;
            foreach (var (k, v) in ctx.Query)
                psi.Environment[$"WEBHOOK_QUERY_{Sanitize(k)}"] = v;
        }

        psi.Environment["WEBHOOK_RUN_ID"] = ctx.RunId;
        psi.Environment["WEBHOOK_SLUG"] = ctx.Slug;

        return psi;
    }

    private static void AddPwshArgs(ProcessStartInfo psi, EndpointConfig endpoint)
    {
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");

        if (!string.IsNullOrEmpty(endpoint.ScriptPath))
        {
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(endpoint.ScriptPath);
        }
        else
        {
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(endpoint.InlineCommand ?? "");
        }
    }

    private static string ResolveCmdInvocation(EndpointConfig endpoint)
    {
        if (!string.IsNullOrEmpty(endpoint.ScriptPath))
            return endpoint.ScriptPath!;
        return endpoint.InlineCommand ?? "";
    }

    private static string Sanitize(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (var ch in key)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
                sb.Append(char.ToUpperInvariant(ch));
            else
                sb.Append('_');
        }
        return sb.ToString();
    }

    private static async Task<(string Text, bool Truncated)> ReadCappedAsync(StreamReader reader, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new char[4096];
        bool truncated = false;
        var byteEstimate = 0;

        while (true)
        {
            int n;
            try { n = await reader.ReadAsync(buffer, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (IOException) { break; }
            if (n == 0) break;

            // Cheap byte estimate (ASCII-ish); good enough as a guard rail.
            if (!truncated)
            {
                if (byteEstimate + n > MaxOutputBytes)
                {
                    var allowed = MaxOutputBytes - byteEstimate;
                    if (allowed > 0) sb.Append(buffer, 0, allowed);
                    truncated = true;
                }
                else
                {
                    sb.Append(buffer, 0, n);
                    byteEstimate += n;
                }
            }
            // Else keep draining without storing to keep the pipe from blocking.
        }

        return (sb.ToString(), truncated);
    }

    private static ExecutionResult Failed(string runId, DateTimeOffset startedAt, string reason) => new()
    {
        RunId = runId,
        ExitCode = -1,
        Stdout = "",
        Stderr = "",
        StartedAt = startedAt,
        CompletedAt = DateTimeOffset.UtcNow,
        TimedOut = false,
        LaunchError = reason,
    };
}
