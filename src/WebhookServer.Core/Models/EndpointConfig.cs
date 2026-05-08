namespace WebhookServer.Core.Models;

public sealed class EndpointConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;

    public List<string> AllowedClients { get; set; } = new();

    public AuthMode AuthMode { get; set; } = AuthMode.None;
    public BearerOptions? Bearer { get; set; }
    public HmacOptions? Hmac { get; set; }

    public ExecutorType ExecutorType { get; set; } = ExecutorType.WindowsPowerShell;

    /// <summary>Path to a script file (.ps1, .bat, .cmd) when applicable.</summary>
    public string? ScriptPath { get; set; }

    /// <summary>Inline command body when no script file is used (PowerShell -Command, cmd /c).</summary>
    public string? InlineCommand { get; set; }

    /// <summary>Path to the executable when ExecutorType = Executable.</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>Static argv prefix for Executable mode; the rendered ArgTemplate appends after.</summary>
    public List<string> ExecutableArgs { get; set; } = new();

    public string? WorkingDirectory { get; set; }

    public DataPassingOptions DataPassing { get; set; } = new();

    public ResponseMode ResponseMode { get; set; } = ResponseMode.Sync;

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>If true, a non-zero process exit produces 502 in sync mode (default true).</summary>
    public bool FailOnNonZeroExit { get; set; } = true;

    /// <summary>If true, requests are processed one at a time per endpoint.</summary>
    public bool Serialize { get; set; }

    public CallbackConfig? Callback { get; set; }
}
