namespace WebhookServer.Core.Models;

public enum AuthMode
{
    None = 0,
    Bearer = 1,
    Hmac = 2,
}

public enum HmacAlgorithm
{
    Sha1 = 1,
    Sha256 = 2,
    Sha512 = 3,
}

public enum HmacEncoding
{
    Hex = 0,
    Base64 = 1,
}

public enum ExecutorType
{
    WindowsPowerShell = 0,
    PwshCore = 1,
    Cmd = 2,
    Executable = 3,
}

public enum ResponseMode
{
    Sync = 0,
    Async = 1,
}

public enum CallbackTrigger
{
    OnComplete = 0,
    OnSuccess = 1,
    OnFailure = 2,
}

public enum CallbackHttpMethod
{
    Post = 0,
    Put = 1,
}

public enum HttpsBindingKind
{
    None = 0,
    PfxFile = 1,
    CertStoreThumbprint = 2,
}

public enum RunAsMode
{
    /// <summary>Run as whatever account the service itself runs under (default).</summary>
    Service = 0,

    /// <summary>Run as a specific username + password (batch logon, no UI).</summary>
    SpecificUser = 1,

    /// <summary>
    /// Run in the active console session under whoever is logged in at the keyboard.
    /// Lets hooks pop interactive UI on the user's desktop.
    /// </summary>
    InteractiveUser = 2,
}
