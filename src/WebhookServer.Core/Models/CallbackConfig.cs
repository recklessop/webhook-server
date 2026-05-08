namespace WebhookServer.Core.Models;

public sealed class CallbackConfig
{
    public string Url { get; set; } = "";
    public CallbackHttpMethod Method { get; set; } = CallbackHttpMethod.Post;
    public AuthMode AuthMode { get; set; } = AuthMode.None;
    public BearerOptions? Bearer { get; set; }
    public HmacOptions? Hmac { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxAttempts { get; set; } = 5;
    public bool IncludeStdout { get; set; } = true;
    public bool IncludeStderr { get; set; } = true;
    public int MaxOutputBytes { get; set; } = 64 * 1024;
    public CallbackTrigger Trigger { get; set; } = CallbackTrigger.OnComplete;
}
