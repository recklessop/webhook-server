using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static WebhookServer.Core.Execution.Native.NativeMethods;

namespace WebhookServer.Core.Execution.Native;

/// <summary>
/// Launches a child process inside the active console session under whoever is
/// logged in at the keyboard. Required when running as SYSTEM (the service account)
/// because <c>Process.Start</c> would land the child in Session 0 where it can't
/// show UI on the user's desktop.
///
/// Caller must already be SYSTEM (the service runs as SYSTEM by default) — only
/// SYSTEM can call <c>WTSQueryUserToken</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class InteractiveProcessLauncher
{
    public sealed class LaunchOptions
    {
        public required string FileName { get; init; }
        public required IReadOnlyList<string> Arguments { get; init; }
        public string? WorkingDirectory { get; init; }
        public IReadOnlyDictionary<string, string>? ExtraEnvVars { get; init; }
        public byte[]? StdinBytes { get; init; }
    }

    public sealed class LaunchResult : IDisposable
    {
        public required IntPtr ProcessHandle { get; init; }
        public required uint ProcessId { get; init; }
        public required StreamReader Stdout { get; init; }
        public required StreamReader Stderr { get; init; }

        public void Dispose()
        {
            try { Stdout.Dispose(); } catch { }
            try { Stderr.Dispose(); } catch { }
            if (ProcessHandle != IntPtr.Zero)
                try { CloseHandle(ProcessHandle); } catch { }
        }
    }

    public static LaunchResult Launch(LaunchOptions opts)
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == INVALID_SESSION_ID)
            throw new InvalidOperationException("No active console session - is anyone logged in at the keyboard?");

        if (!WTSQueryUserToken(sessionId, out var userToken))
            throw LastError("WTSQueryUserToken (must run as SYSTEM)");

        IntPtr primaryToken = IntPtr.Zero;
        IntPtr envBlock = IntPtr.Zero;
        IntPtr stdoutRead = IntPtr.Zero, stdoutWrite = IntPtr.Zero;
        IntPtr stderrRead = IntPtr.Zero, stderrWrite = IntPtr.Zero;
        IntPtr stdinRead = IntPtr.Zero, stdinWrite = IntPtr.Zero;
        var pi = new PROCESS_INFORMATION();
        bool succeeded = false;

        try
        {
            if (!DuplicateTokenEx(userToken, (uint)MAXIMUM_ALLOWED, IntPtr.Zero,
                    SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                    TOKEN_TYPE.TokenPrimary, out primaryToken))
                throw LastError("DuplicateTokenEx");

            if (!CreateEnvironmentBlock(out envBlock, primaryToken, false))
                throw LastError("CreateEnvironmentBlock");

            if (opts.ExtraEnvVars is { Count: > 0 })
                envBlock = AppendEnvVars(envBlock, opts.ExtraEnvVars);

            CreateInheritablePipe(out stdoutRead, out stdoutWrite, parentReads: true);
            CreateInheritablePipe(out stderrRead, out stderrWrite, parentReads: true);
            CreateInheritablePipe(out stdinRead, out stdinWrite, parentReads: false);

            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                dwFlags = STARTF_USESTDHANDLES,
                hStdInput = stdinRead,
                hStdOutput = stdoutWrite,
                hStdError = stderrWrite,
                lpDesktop = @"winsta0\default",
            };

            var commandLine = BuildCommandLine(opts.FileName, opts.Arguments);

            if (!CreateProcessAsUser(
                    primaryToken,
                    null,
                    commandLine,
                    IntPtr.Zero, IntPtr.Zero,
                    bInheritHandles: true,
                    CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW | NORMAL_PRIORITY_CLASS,
                    envBlock,
                    string.IsNullOrEmpty(opts.WorkingDirectory) ? null : opts.WorkingDirectory,
                    ref si,
                    out pi))
            {
                throw LastError("CreateProcessAsUser");
            }

            // Close child-side handles in our process so EOF propagates when the child exits.
            CloseHandle(stdoutWrite); stdoutWrite = IntPtr.Zero;
            CloseHandle(stderrWrite); stderrWrite = IntPtr.Zero;
            CloseHandle(stdinRead); stdinRead = IntPtr.Zero;

            // Pipe stdin if provided, then close the write end.
            if (opts.StdinBytes is { Length: > 0 })
            {
                using var stdinStream = new FileStream(new SafeFileHandle(stdinWrite, ownsHandle: true), FileAccess.Write);
                stdinStream.Write(opts.StdinBytes, 0, opts.StdinBytes.Length);
                stdinStream.Flush();
                stdinWrite = IntPtr.Zero; // ownership transferred to the FileStream
            }
            else
            {
                CloseHandle(stdinWrite); stdinWrite = IntPtr.Zero;
            }

            CloseHandle(pi.hThread);

            var stdout = new StreamReader(new FileStream(new SafeFileHandle(stdoutRead, true), FileAccess.Read), Encoding.UTF8);
            var stderr = new StreamReader(new FileStream(new SafeFileHandle(stderrRead, true), FileAccess.Read), Encoding.UTF8);
            stdoutRead = IntPtr.Zero;
            stderrRead = IntPtr.Zero;

            succeeded = true;
            return new LaunchResult
            {
                ProcessHandle = pi.hProcess,
                ProcessId = pi.dwProcessId,
                Stdout = stdout,
                Stderr = stderr,
            };
        }
        finally
        {
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            if (!succeeded)
            {
                if (stdoutRead != IntPtr.Zero) CloseHandle(stdoutRead);
                if (stdoutWrite != IntPtr.Zero) CloseHandle(stdoutWrite);
                if (stderrRead != IntPtr.Zero) CloseHandle(stderrRead);
                if (stderrWrite != IntPtr.Zero) CloseHandle(stderrWrite);
                if (stdinRead != IntPtr.Zero) CloseHandle(stdinRead);
                if (stdinWrite != IntPtr.Zero) CloseHandle(stdinWrite);
                if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
                if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            }
        }
    }

    public static async Task<int> WaitAsync(IntPtr processHandle, TimeSpan timeout, CancellationToken ct)
    {
        // Poll WaitForSingleObject in 100ms slices to honor cancellation/timeout cheaply.
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException();

            var slice = (uint)Math.Min(100, (int)remaining.TotalMilliseconds);
            var result = WaitForSingleObject(processHandle, slice);
            if (result == 0) // WAIT_OBJECT_0
            {
                if (!GetExitCodeProcess(processHandle, out var code))
                    throw LastError("GetExitCodeProcess");
                return (int)code;
            }
            if (result == 0xFFFFFFFF)
                throw LastError("WaitForSingleObject");
            await Task.Yield();
        }
    }

    public static void Kill(IntPtr processHandle)
    {
        try { TerminateProcess(processHandle, 1); } catch { }
    }

    private static void CreateInheritablePipe(out IntPtr read, out IntPtr write, bool parentReads)
    {
        var sa = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = 1,
            lpSecurityDescriptor = IntPtr.Zero,
        };
        if (!CreatePipe(out read, out write, ref sa, 0))
            throw LastError("CreatePipe");

        // Mark the parent's end non-inheritable so it's not duplicated into the child.
        var parentEnd = parentReads ? read : write;
        if (!SetHandleInformation(parentEnd, HANDLE_FLAG_INHERIT, 0))
            throw LastError("SetHandleInformation");
    }

    private static IntPtr AppendEnvVars(IntPtr existingBlock, IReadOnlyDictionary<string, string> extras)
    {
        // The existing block is a sequence of null-terminated WCHAR strings ending with
        // an extra null. Walk it byte-pair by byte-pair to find the end.
        var parsed = new List<string>();
        var offset = 0;
        while (true)
        {
            var ch0 = Marshal.ReadInt16(existingBlock, offset);
            if (ch0 == 0) break;
            var entry = Marshal.PtrToStringUni(existingBlock + offset)!;
            parsed.Add(entry);
            offset += (entry.Length + 1) * sizeof(char);
        }
        DestroyEnvironmentBlock(existingBlock);

        var combined = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in parsed)
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            combined[entry.Substring(0, eq)] = entry.Substring(eq + 1);
        }
        foreach (var (k, v) in extras) combined[k] = v;

        var sb = new StringBuilder();
        foreach (var (k, v) in combined)
        {
            sb.Append(k).Append('=').Append(v).Append('\0');
        }
        sb.Append('\0');

        var bytes = Encoding.Unicode.GetBytes(sb.ToString());
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    /// <summary>
    /// Build a Windows command line string from a filename + arg list using the
    /// quoting rules consumed by CommandLineToArgvW.
    /// </summary>
    private static string BuildCommandLine(string fileName, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        AppendArg(sb, fileName);
        foreach (var arg in args)
        {
            sb.Append(' ');
            AppendArg(sb, arg);
        }
        return sb.ToString();
    }

    private static void AppendArg(StringBuilder sb, string arg)
    {
        // Empty arg → "".
        if (arg.Length == 0) { sb.Append("\"\""); return; }

        var needsQuoting = arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) >= 0;
        if (!needsQuoting) { sb.Append(arg); return; }

        sb.Append('"');
        var backslashes = 0;
        foreach (var ch in arg)
        {
            if (ch == '\\') { backslashes++; continue; }
            if (ch == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
                continue;
            }
            if (backslashes > 0) { sb.Append('\\', backslashes); backslashes = 0; }
            sb.Append(ch);
        }
        // Trailing backslashes before closing quote must be doubled.
        if (backslashes > 0) sb.Append('\\', backslashes * 2);
        sb.Append('"');
    }
}
