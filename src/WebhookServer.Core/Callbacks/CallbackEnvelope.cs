using WebhookServer.Core.Models;

namespace WebhookServer.Core.Callbacks;

/// <summary>
/// Internal queue item pairing a payload with the resolved <see cref="CallbackConfig"/>
/// for the endpoint. The dispatcher reads from a channel of these.
/// </summary>
public sealed class CallbackEnvelope
{
    public required Guid EndpointId { get; init; }
    public required string EndpointSlug { get; init; }
    public required CallbackConfig Config { get; init; }
    public required CallbackPayload Payload { get; init; }
}
