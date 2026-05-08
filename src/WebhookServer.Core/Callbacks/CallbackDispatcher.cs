using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WebhookServer.Core.Auth;
using WebhookServer.Core.Models;
using WebhookServer.Core.Storage;

namespace WebhookServer.Core.Callbacks;

/// <summary>
/// Bounded queue of pending callback deliveries with retry + backoff. Reuses
/// <see cref="HmacVerifier.Compute"/> so outbound HMAC matches the inbound code path.
///
/// Run <see cref="RunAsync"/> from a single long-running task (BackgroundService in the
/// service host); call <see cref="Enqueue"/> from anywhere. Disposing the dispatcher
/// disposes its <see cref="HttpClient"/>.
/// </summary>
public sealed class CallbackDispatcher : IDisposable
{
    private const int QueueCapacity = 1024;
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    private readonly Channel<CallbackEnvelope> _channel;
    private readonly HttpClient _http;
    private readonly ILogger<CallbackDispatcher>? _logger;

    public CallbackDispatcher(ILogger<CallbackDispatcher>? logger = null, HttpClient? httpClient = null)
    {
        _logger = logger;
        _channel = Channel.CreateBounded<CallbackEnvelope>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        _http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
        });
    }

    public bool Enqueue(CallbackEnvelope envelope)
    {
        var ok = _channel.Writer.TryWrite(envelope);
        if (!ok)
        {
            _logger?.LogWarning("Callback queue full; dropped envelope for endpoint {Slug}", envelope.EndpointSlug);
        }
        return ok;
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        await foreach (var envelope in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await DeliverAsync(envelope, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unhandled error in callback dispatcher for {Slug}", envelope.EndpointSlug);
            }
        }
    }

    private async Task DeliverAsync(CallbackEnvelope envelope, CancellationToken stoppingToken)
    {
        var cfg = envelope.Config;
        var maxAttempts = Math.Max(1, cfg.MaxAttempts);
        var bodyBytes = SerializePayload(envelope.Payload, cfg);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            attemptCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, cfg.TimeoutSeconds)));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            HttpResponseMessage? response = null;
            string? errorReason = null;

            try
            {
                using var request = BuildRequest(envelope, bodyBytes);
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (attemptCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                errorReason = "timeout";
            }
            catch (Exception ex)
            {
                errorReason = ex.GetType().Name;
            }
            sw.Stop();

            int? statusCode = (int?)response?.StatusCode;
            bool delivered = response is { IsSuccessStatusCode: true };

            _logger?.LogInformation(
                "Callback {Slug} attempt {Attempt}/{Max} -> {Status} ({Latency} ms){Error}",
                envelope.EndpointSlug, attempt, maxAttempts,
                statusCode?.ToString() ?? "ERR",
                sw.ElapsedMilliseconds,
                errorReason is null ? "" : $" [{errorReason}]");

            if (delivered)
            {
                response?.Dispose();
                return;
            }

            var transient = errorReason is not null || (statusCode.HasValue && IsRetryable(statusCode.Value));
            if (!transient || attempt == maxAttempts)
            {
                _logger?.LogWarning("Callback {Slug} {Disposition} after {Attempts} attempts",
                    envelope.EndpointSlug,
                    transient ? "gave-up" : "dropped",
                    attempt);
                response?.Dispose();
                return;
            }

            var delay = ComputeBackoff(attempt, response);
            response?.Dispose();
            try { await Task.Delay(delay, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private HttpRequestMessage BuildRequest(CallbackEnvelope envelope, byte[] bodyBytes)
    {
        var cfg = envelope.Config;
        var method = cfg.Method == CallbackHttpMethod.Put ? HttpMethod.Put : HttpMethod.Post;
        var request = new HttpRequestMessage(method, cfg.Url)
        {
            Content = new ByteArrayContent(bodyBytes),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        switch (cfg.AuthMode)
        {
            case AuthMode.Bearer:
                if (cfg.Bearer?.Secret.Plaintext is { Length: > 0 } token)
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                break;
            case AuthMode.Hmac:
                if (cfg.Hmac is { } hmac && hmac.Secret.Plaintext is { Length: > 0 } secret)
                {
                    var sig = HmacVerifier.Compute(bodyBytes, secret, hmac.Algorithm, hmac.Encoding);
                    request.Headers.TryAddWithoutValidation(hmac.HeaderName, hmac.Prefix + sig);
                }
                break;
        }

        return request;
    }

    private static byte[] SerializePayload(CallbackPayload payload, CallbackConfig cfg)
    {
        // Honor the IncludeStdout / IncludeStderr flags by wiping them out before serialization.
        var effective = new CallbackPayload
        {
            RunId = payload.RunId,
            Endpoint = payload.Endpoint,
            StartedAt = payload.StartedAt,
            CompletedAt = payload.CompletedAt,
            DurationMs = payload.DurationMs,
            ExitCode = payload.ExitCode,
            Succeeded = payload.Succeeded,
            TimedOut = payload.TimedOut,
            Stdout = cfg.IncludeStdout ? payload.Stdout : null,
            Stderr = cfg.IncludeStderr ? payload.Stderr : null,
            StdoutTruncated = cfg.IncludeStdout && payload.StdoutTruncated,
            StderrTruncated = cfg.IncludeStderr && payload.StderrTruncated,
        };

        return JsonSerializer.SerializeToUtf8Bytes(effective, ConfigJson.Compact);
    }

    private static bool IsRetryable(int status) => status switch
    {
        408 or 425 or 429 => true,
        >= 500 and <= 599 => true,
        _ => false,
    };

    private static TimeSpan ComputeBackoff(int attempt, HttpResponseMessage? response)
    {
        if (response?.Headers.RetryAfter is { } ra)
        {
            if (ra.Delta.HasValue)
                return Min(ra.Delta.Value, MaxRetryAfter);
            if (ra.Date.HasValue)
            {
                var delta = ra.Date.Value - DateTimeOffset.UtcNow;
                if (delta > TimeSpan.Zero) return Min(delta, MaxRetryAfter);
            }
        }

        // Exponential: 1s, 2s, 4s, 8s, 16s, 32s, 60s cap
        var seconds = Math.Min(60, Math.Pow(2, attempt - 1));
        var jitter = (Random.Shared.NextDouble() * 0.5) - 0.25; // ±25%
        return TimeSpan.FromSeconds(seconds * (1 + jitter));
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    public void Dispose() => _http.Dispose();
}
