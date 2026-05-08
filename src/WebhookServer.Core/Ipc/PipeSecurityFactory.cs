using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WebhookServer.Core.Ipc;

/// <summary>
/// Builds a <see cref="PipeSecurity"/> that allows SYSTEM and the local Administrators
/// group full control, and denies everyone else. Required so non-admin users cannot
/// read or write the admin pipe even if they know the name.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PipeSecurityFactory
{
    public const string PipeName = "WebhookServerAdmin";

    public static PipeSecurity Create()
    {
        var security = new PipeSecurity();

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        security.AddAccessRule(new PipeAccessRule(
            system, PipeAccessRights.FullControl, AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            administrators, PipeAccessRights.FullControl, AccessControlType.Allow));

        return security;
    }
}
