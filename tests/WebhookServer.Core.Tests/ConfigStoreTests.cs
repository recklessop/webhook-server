using System.Runtime.InteropServices;
using WebhookServer.Core.Models;
using WebhookServer.Core.Storage;
using Xunit;

namespace WebhookServer.Core.Tests;

public class ConfigStoreTests
{
    [Fact]
    public async Task Save_then_load_preserves_endpoints_and_encrypts_secrets()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var path = Path.Combine(Path.GetTempPath(), $"webhook-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ConfigStore(path);
            var cfg = new ServerConfig
            {
                HttpPort = 9000,
                Endpoints =
                {
                    new EndpointConfig
                    {
                        Slug = "deploy",
                        AuthMode = AuthMode.Bearer,
                        Bearer = new BearerOptions { Secret = ProtectedString.FromPlaintext("topsecret") },
                    },
                },
            };

            await store.SaveAsync(cfg);

            // Persisted config must not contain plaintext.
            var rawJson = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("topsecret", rawJson);
            Assert.Contains("encrypted", rawJson);

            var reloaded = await store.LoadAsync();
            ConfigStore.DecryptSecrets(reloaded);

            var ep = Assert.Single(reloaded.Endpoints);
            Assert.Equal("deploy", ep.Slug);
            Assert.Equal("topsecret", ep.Bearer!.Secret.Plaintext);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
