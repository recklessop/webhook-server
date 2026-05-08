using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using WebhookServer.Core.Auth;
using WebhookServer.Core.Callbacks;
using WebhookServer.Core.Execution;
using WebhookServer.Core.Models;
using ExecCtx = WebhookServer.Core.Execution.ExecutionContext;

namespace WebhookServer.Service;

[SupportedOSPlatform("windows")]
public sealed class WebhookRouter
{
    private readonly ServiceState _state;
    private readonly IExecutor _executor;
    private readonly ConcurrencyGate _gate;
    private readonly CallbackDispatcher _callbacks;
    private readonly ILogger<WebhookRouter> _logger;

    public WebhookRouter(
        ServiceState state,
        IExecutor executor,
        ConcurrencyGate gate,
        CallbackDispatcher callbacks,
        ILogger<WebhookRouter> logger)
    {
        _state = state;
        _executor = executor;
        _gate = gate;
        _callbacks = callbacks;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext http, string slug)
    {
        var runId = Guid.NewGuid().ToString("N");

        if (!_state.TryGetEndpoint(slug, out var endpoint) || !endpoint.Enabled)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var clientIp = ResolveClientIp(http);

        // 1. IP allowlist (before auth, before reading body).
        var allowList = _state.GetAllowList(endpoint.Id);
        if (!allowList.IsEmpty && (clientIp is null || !allowList.Contains(clientIp)))
        {
            _logger.LogWarning("IP {Ip} blocked for endpoint {Slug} (run {RunId})", clientIp, slug, runId);
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // 2. Capture raw body bytes (needed for HMAC verification and stdin/template).
        byte[] bodyBytes;
        try
        {
            using var ms = new MemoryStream();
            await http.Request.Body.CopyToAsync(ms, http.RequestAborted).ConfigureAwait(false);
            bodyBytes = ms.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed reading body for {Slug} (run {RunId})", slug, runId);
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // 3. Auth.
        var authResult = VerifyAuth(endpoint, http, bodyBytes);
        if (!authResult.Success)
        {
            _logger.LogWarning("Auth failed for {Slug}: {Reason} (run {RunId})", slug, authResult.Reason, runId);
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // 4. Build execution context.
        var bodyString = Encoding.UTF8.GetString(bodyBytes);
        JsonNode? bodyJson = null;
        try
        {
            if (bodyBytes.Length > 0)
                bodyJson = JsonNode.Parse(bodyBytes);
        }
        catch
        {
            // Non-JSON body — leave bodyJson null so {{body.*}} renders empty.
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in http.Request.Headers)
            headers[key] = value.ToString();

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in http.Request.Query)
            query[key] = value.ToString();

        var route = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "slug", slug } };

        var ctx = new ExecCtx
        {
            RunId = runId,
            Slug = slug,
            BodyBytes = bodyBytes,
            BodyString = bodyString,
            BodyJson = bodyJson,
            Headers = headers,
            Query = query,
            Route = route,
        };

        // 5. Dispatch.
        if (endpoint.ResponseMode == ResponseMode.Async)
        {
            _ = Task.Run(() => RunAndDispatchCallbackAsync(endpoint, ctx, http.RequestAborted));
            http.Response.StatusCode = StatusCodes.Status202Accepted;
            await WriteJsonAsync(http, new { runId, accepted = true }).ConfigureAwait(false);
            return;
        }

        var result = await RunAsync(endpoint, ctx, http.RequestAborted).ConfigureAwait(false);
        LogResult(endpoint, ctx, result);
        DispatchCallback(endpoint, ctx, result);

        if (result.LaunchError is not null)
        {
            http.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await WriteJsonAsync(http, new { runId, error = result.LaunchError }).ConfigureAwait(false);
            return;
        }

        http.Response.StatusCode = endpoint.FailOnNonZeroExit && !result.Succeeded
            ? StatusCodes.Status502BadGateway
            : StatusCodes.Status200OK;

        await WriteJsonAsync(http, new
        {
            runId,
            exitCode = result.ExitCode,
            timedOut = result.TimedOut,
            durationMs = (long)result.Duration.TotalMilliseconds,
            stdout = result.Stdout,
            stderr = result.Stderr,
            stdoutTruncated = result.StdoutTruncated,
            stderrTruncated = result.StderrTruncated,
        }).ConfigureAwait(false);
    }

    private async Task RunAndDispatchCallbackAsync(EndpointConfig endpoint, ExecCtx ctx, CancellationToken ct)
    {
        try
        {
            var result = await RunAsync(endpoint, ctx, ct).ConfigureAwait(false);
            LogResult(endpoint, ctx, result);
            DispatchCallback(endpoint, ctx, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Async run failed for {Slug} (run {RunId})", ctx.Slug, ctx.RunId);
        }
    }

    private void LogResult(EndpointConfig endpoint, ExecCtx ctx, ExecutionResult result)
    {
        if (result.LaunchError is not null)
        {
            _logger.LogWarning("Run {RunId} {Slug} failed to launch: {Error}",
                ctx.RunId, ctx.Slug, result.LaunchError);
            return;
        }
        if (result.TimedOut)
        {
            _logger.LogWarning("Run {RunId} {Slug} timed out after {Sec}s; process killed",
                ctx.RunId, ctx.Slug, endpoint.TimeoutSeconds);
            return;
        }

        var stdout = TruncateForLog(result.Stdout, 512);
        var stderr = TruncateForLog(result.Stderr, 512);
        if (result.Succeeded)
        {
            _logger.LogInformation(
                "Run {RunId} {Slug} ok exit={Exit} dur={Ms}ms stdout={Stdout}{StderrPart}",
                ctx.RunId, ctx.Slug, result.ExitCode, (long)result.Duration.TotalMilliseconds,
                stdout, string.IsNullOrEmpty(stderr) ? "" : $" stderr={stderr}");
        }
        else
        {
            _logger.LogWarning(
                "Run {RunId} {Slug} non-zero exit={Exit} dur={Ms}ms stdout={Stdout} stderr={Stderr}",
                ctx.RunId, ctx.Slug, result.ExitCode, (long)result.Duration.TotalMilliseconds,
                stdout, stderr);
        }
    }

    private static string TruncateForLog(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "(empty)";
        var trimmed = s.Trim();
        if (trimmed.Length <= max) return trimmed;
        return trimmed.Substring(0, max) + $"... [+{trimmed.Length - max} chars]";
    }

    private async Task<ExecutionResult> RunAsync(EndpointConfig endpoint, ExecCtx ctx, CancellationToken ct)
    {
        if (endpoint.Serialize)
        {
            using var _ = await _gate.AcquireAsync(endpoint.Id, ct).ConfigureAwait(false);
            return await _executor.RunAsync(endpoint, ctx, ct).ConfigureAwait(false);
        }
        return await _executor.RunAsync(endpoint, ctx, ct).ConfigureAwait(false);
    }

    private void DispatchCallback(EndpointConfig endpoint, ExecCtx ctx, ExecutionResult result)
    {
        var cb = endpoint.Callback;
        if (cb is null || string.IsNullOrEmpty(cb.Url)) return;

        var trigger = cb.Trigger;
        var fire = trigger switch
        {
            CallbackTrigger.OnSuccess => result.Succeeded,
            CallbackTrigger.OnFailure => !result.Succeeded,
            _ => true,
        };
        if (!fire) return;

        var stdout = TruncateBytes(result.Stdout, cb.MaxOutputBytes, out var stdoutCut);
        var stderr = TruncateBytes(result.Stderr, cb.MaxOutputBytes, out var stderrCut);

        var payload = new CallbackPayload
        {
            RunId = ctx.RunId,
            Endpoint = ctx.Slug,
            StartedAt = result.StartedAt,
            CompletedAt = result.CompletedAt,
            DurationMs = (long)result.Duration.TotalMilliseconds,
            ExitCode = result.ExitCode,
            Succeeded = result.Succeeded,
            TimedOut = result.TimedOut,
            Stdout = stdout,
            Stderr = stderr,
            StdoutTruncated = result.StdoutTruncated || stdoutCut,
            StderrTruncated = result.StderrTruncated || stderrCut,
        };

        _callbacks.Enqueue(new CallbackEnvelope
        {
            EndpointId = endpoint.Id,
            EndpointSlug = ctx.Slug,
            Config = cb,
            Payload = payload,
        });
    }

    private static string TruncateBytes(string s, int maxBytes, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrEmpty(s)) return s;
        if (maxBytes <= 0) { truncated = true; return ""; }
        var bytes = Encoding.UTF8.GetByteCount(s);
        if (bytes <= maxBytes) return s;

        // Trim from the end until under cap. Cheap and good enough.
        var bs = Encoding.UTF8.GetBytes(s);
        truncated = true;
        return Encoding.UTF8.GetString(bs.AsSpan(0, maxBytes));
    }

    private static async Task WriteJsonAsync(HttpContext http, object payload)
    {
        http.Response.ContentType = "application/json; charset=utf-8";
        await System.Text.Json.JsonSerializer.SerializeAsync(http.Response.Body, payload, options: new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }, http.RequestAborted).ConfigureAwait(false);
    }

    private AuthResult VerifyAuth(EndpointConfig endpoint, HttpContext http, byte[] body)
    {
        switch (endpoint.AuthMode)
        {
            case AuthMode.None:
                return AuthResult.Ok();
            case AuthMode.Bearer:
                var token = endpoint.Bearer?.Secret.Plaintext ?? "";
                return BearerVerifier.Verify(http.Request.Headers.Authorization.ToString(), token);
            case AuthMode.Hmac:
                if (endpoint.Hmac is null) return AuthResult.Fail("HMAC config missing");
                var headerName = endpoint.Hmac.HeaderName;
                var presented = http.Request.Headers.TryGetValue(headerName, out var v) ? v.ToString() : null;
                return HmacVerifier.Verify(body, presented, endpoint.Hmac);
            default:
                return AuthResult.Fail("unknown auth mode");
        }
    }

    private IPAddress? ResolveClientIp(HttpContext http)
    {
        var direct = http.Connection.RemoteIpAddress;
        if (direct is null) return null;

        var trustedProxies = _state.GetTrustedProxies();
        if (trustedProxies.IsEmpty || !trustedProxies.Contains(direct))
            return Normalize(direct);

        // Direct hop is a trusted proxy — honor X-Forwarded-For (leftmost).
        if (http.Request.Headers.TryGetValue("X-Forwarded-For", out var xff) && !string.IsNullOrEmpty(xff))
        {
            var first = xff.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (!string.IsNullOrEmpty(first) && IPAddress.TryParse(first, out var parsed))
                return Normalize(parsed);
        }

        return Normalize(direct);
    }

    private static IPAddress Normalize(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
            return address.MapToIPv4();
        return address;
    }
}
