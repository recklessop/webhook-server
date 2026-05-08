using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebhookServer.Core.Models;

namespace WebhookServer.Gui.ViewModels;

[SupportedOSPlatform("windows")]
public sealed partial class ServerSettingsViewModel : ObservableObject
{
    [ObservableProperty] private int _httpPort;
    [ObservableProperty] private int _httpsPort;
    [ObservableProperty] private bool _httpsEnabled;
    [ObservableProperty] private string _httpsMode = "PfxFile";
    [ObservableProperty] private string _pfxPath = "";
    [ObservableProperty] private string _pfxPassword = "";
    [ObservableProperty] private string _thumbprint = "";
    [ObservableProperty] private string _trustedProxiesText = "";

    public bool Accepted { get; private set; }

    public ServerSettingsViewModel(ServerConfig config)
    {
        HttpPort = config.HttpPort;
        TrustedProxiesText = string.Join(Environment.NewLine, config.TrustedProxies);

        var b = config.HttpsBinding;
        HttpsEnabled = b is not null && b.Kind != HttpsBindingKind.None;
        HttpsPort = b?.Port ?? 8443;
        HttpsMode = b?.Kind == HttpsBindingKind.CertStoreThumbprint ? "Thumbprint" : "PfxFile";
        PfxPath = b?.PfxPath ?? "";
        PfxPassword = b?.PfxPassword?.Plaintext ?? "";
        Thumbprint = b?.Thumbprint ?? "";
    }

    public List<string> TrustedProxiesList =>
        (TrustedProxiesText ?? "").Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public HttpsBinding? BuildBinding()
    {
        if (!HttpsEnabled) return null;

        var binding = new HttpsBinding { Port = HttpsPort };
        if (string.Equals(HttpsMode, "Thumbprint", StringComparison.OrdinalIgnoreCase))
        {
            binding.Kind = HttpsBindingKind.CertStoreThumbprint;
            binding.Thumbprint = Thumbprint?.Trim();
        }
        else
        {
            binding.Kind = HttpsBindingKind.PfxFile;
            binding.PfxPath = PfxPath;
            if (!string.IsNullOrEmpty(PfxPassword))
                binding.PfxPassword = ProtectedString.FromPlaintext(PfxPassword);
        }
        return binding;
    }

    [RelayCommand]
    private void Save() => Accepted = true;
}
