using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using WebhookServer.Core.Execution.Native;
using WebhookServer.Core.Models;

namespace WebhookServer.Core.Execution;

[SupportedOSPlatform("windows")]
public sealed class ProcessExecutor : IExecutor
{
    /// <summary>Per-stream cap on captured output (excess is dropped and StdoutTruncated set).</summary>
    public const int MaxOutputBytes = 1 * 1024 * 1024;

    public async Task<ExecutionResult> RunAsync(EndpointConfig endpoint, ExecutionContext ctx, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var mode = endpoint.RunAs?.Mode ?? RunAsMode.Service;
        return mode switch
        {
            RunAsMode.InteractiveUser => await RunInteractiveAsync(endpoint, ctx, startedAt, ct).ConfigureAwait(false),
            _ => await RunWithProcessAsync(endpoint, ctx, startedAt, ct).ConfigureAwait(false),
        };
    }

    // ---------------- Process path: handles Service (default) and SpecificUser. ----------------

    private async Task<ExecutionResult> RunWithProcessAsync(EndpointConfig endpoint, ExecutionContext ctx, DateTimeOffset startedAt, CancellationToken ct)
    {
        var (psi, envVars) = BuildStartInfo(endpoint, ctx);
        foreach (var (k, v) in envVars)
            psi.Environment[k] = v;

        if (endpoint.RunAs?.Mode == RunAsMode.SpecificUser)
        {
            try { ApplySpecificUser(psi, endpoint.RunAs); }
            catch (Exception ex) { return Failed(ctx.RunId, startedAt, ex.Message); }
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        try
        {
            if (!process.Start())
                return Failed(ctx.RunId, startedAt, "process failed to start");
        }
        catch (Exception ex)
        {
            return Failed(ctx.RunId, startedAt, $"launch error: {ex.Message}");
        }

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
                try { process.StandardInput.Close(); } catch { }
            }
        }
        else
        {
            try { process.StandardInput.Close(); } catch { }
        }

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
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
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

    // ---------------- Interactive path: launches into the active console session. ----------------

    private static async Task<ExecutionResult> RunInteractiveAsync(EndpointConfig endpoint, ExecutionContext ctx, DateTimeOffset startedAt, CancellationToken ct)
    {
        var (psi, envVars) = BuildStartInfo(endpoint, ctx);

        InteractiveProcessLauncher.LaunchResult launch;
        try
        {
            launch = InteractiveProcessLauncher.Launch(new InteractiveProcessLauncher.LaunchOptions
            {
                FileName = psi.FileName,
                Arguments = psi.ArgumentList.ToList(),
                WorkingDirectory = string.IsNullOrEmpty(psi.WorkingDirectory) ? null : psi.WorkingDirectory,
                ExtraEnvVars = envVars,
                StdinBytes = endpoint.DataPassing.StdinJson ? ctx.BodyBytes : null,
            });
        }
        catch (Exception ex)
        {
            return Failed(ctx.RunId, startedAt, $"interactive launch error: {ex.Message}");
        }

        try
        {
            var stdoutTask = ReadCappedAsync(launch.Stdout, ct);
            var stderrTask = ReadCappedAsync(launch.Stderr, ct);

            var timeout = TimeSpan.FromSeconds(Math.Max(1, endpoint.TimeoutSeconds));
            bool timedOut = false;
            int exitCode = -1;
            try
            {
                exitCode = await InteractiveProcessLauncher.WaitAsync(launch.ProcessHandle, timeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                timedOut = true;
                InteractiveProcessLauncher.Kill(launch.ProcessHandle);
            }

            var (stdout, stdoutTrunc) = await stdoutTask.ConfigureAwait(false);
            var (stderr, stderrTrunc) = await stderrTask.ConfigureAwait(false);

            return new ExecutionResult
            {
                RunId = ctx.RunId,
                ExitCode = timedOut ? -1 : exitCode,
                Stdout = stdout,
                Stderr = stderr,
                StdoutTruncated = stdoutTrunc,
                StderrTruncated = stderrTrunc,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                TimedOut = timedOut,
            };
        }
        finally
        {
            launch.Dispose();
        }
    }

    // ---------------- Shared psi construction. ----------------

    private static (ProcessStartInfo psi, Dictionary<string, string> envVars) BuildStartInfo(EndpointConfig endpoint, ExecutionContext ctx)
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

        var envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WEBHOOK_RUN_ID"] = ctx.RunId,
            ["WEBHOOK_SLUG"] = ctx.Slug,
        };

        if (endpoint.DataPassing.EnvVars)
        {
            foreach (var (k, v) in ctx.Headers) envVars[$"WEBHOOK_HEADER_{Sanitize(k)}"] = v;
            foreach (var (k, v) in ctx.Query) envVars[$"WEBHOOK_QUERY_{Sanitize(k)}"] = v;
        }

        return (psi, envVars);
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
            // Pipe stdin into a scriptblock so trailing argv entries bind via @args
            // and the script can still consume the request body via $input.
            // Without the wrapper, PowerShell concatenates all trailing args into the
            // -Command string and fails to parse them.
            psi.ArgumentList.Add("$input | & { " + (endpoint.InlineCommand ?? "") + " } @args");
        }
    }

    private static string ResolveCmdInvocation(EndpointConfig endpoint)
    {
        if (!string.IsNullOrEmpty(endpoint.ScriptPath))
            return endpoint.ScriptPath!;
        return endpoint.InlineCommand ?? "";
    }

    private static void ApplySpecificUser(ProcessStartInfo psi, RunAsConfig runAs)
    {
        if (string.IsNullOrEmpty(runAs.Username))
            throw new InvalidOperationException("RunAs.Username is required when Mode = SpecificUser");
        if (runAs.Password?.Plaintext is not { Length: > 0 } password)
            throw new InvalidOperationException("RunAs.Password is required when Mode = SpecificUser");

        var (domain, user) = ParseUserSpec(runAs.Username);
        psi.UserName = user;
        if (!string.IsNullOrEmpty(domain)) psi.Domain = domain;

        var ss = new SecureString();
        foreach (var ch in password) ss.AppendChar(ch);
        ss.MakeReadOnly();
        psi.Password = ss;

        psi.LoadUserProfile = runAs.LoadProfile;
    }

    private static (string Domain, string User) ParseUserSpec(string spec)
    {
        var bs = spec.IndexOf('\\');
        if (bs > 0) return (spec.Substring(0, bs), spec.Substring(bs + 1));
        return ("", spec);
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
