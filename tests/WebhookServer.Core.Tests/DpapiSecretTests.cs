using System.Runtime.InteropServices;
using WebhookServer.Core.Storage;
using Xunit;

namespace WebhookServer.Core.Tests;

public class DpapiSecretTests
{
    [Fact]
    public void Round_trip_recovers_original_value()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var original = "topsecret-é-🚀";
        var encrypted = DpapiSecret.Protect(original);
        Assert.NotEmpty(encrypted);
        var decrypted = DpapiSecret.Unprotect(encrypted);
        Assert.Equal(original, decrypted);
    }

    [Fact]
    public void Empty_string_round_trips_as_empty()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        Assert.Equal("", DpapiSecret.Unprotect(DpapiSecret.Protect("")));
    }
}
