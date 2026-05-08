using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
    [ObservableProperty] private bool _listenAllInterfaces = true;
    [ObservableProperty] private string _displayHost = "localhost";

    /// <summary>One row per detected local IPv4/IPv6 address. Bound for "listen on" checkboxes.</summary>
    public ObservableCollection<NetworkAddressRow> Addresses { get; } = new();

    /// <summary>Suggestions for the Display URL host dropdown (detected IPs + localhost + machine name).</summary>
    public ObservableCollection<string> DisplayHostChoices { get; } = new();

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

        var detected = DetectLocalAddresses();
        var alreadyBound = new HashSet<string>(config.BindAddresses, StringComparer.OrdinalIgnoreCase);

        ListenAllInterfaces = config.BindAddresses.Count == 0;
        foreach (var (addr, label) in detected)
        {
            Addresses.Add(new NetworkAddressRow
            {
                Address = addr,
                Label = label,
                IsBound = !ListenAllInterfaces && alreadyBound.Contains(addr),
            });
        }
        // Surface any persisted address that isn't currently detected (e.g. a NIC unplugged
        // since save) so the user can keep or remove it explicitly.
        foreach (var entry in config.BindAddresses)
        {
            if (Addresses.Any(a => string.Equals(a.Address, entry, StringComparison.OrdinalIgnoreCase))) continue;
            Addresses.Add(new NetworkAddressRow { Address = entry, Label = "(not currently present)", IsBound = true });
        }

        DisplayHostChoices.Add("localhost");
        DisplayHostChoices.Add(Environment.MachineName);
        foreach (var (addr, _) in detected)
            if (!DisplayHostChoices.Contains(addr))
                DisplayHostChoices.Add(addr);

        DisplayHost = string.IsNullOrEmpty(config.DisplayHost) ? "localhost" : config.DisplayHost;
    }

    public List<string> TrustedProxiesList =>
        (TrustedProxiesText ?? "").Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public List<string> BindAddressesList =>
        ListenAllInterfaces
            ? new List<string>()
            : Addresses.Where(a => a.IsBound).Select(a => a.Address).ToList();

    public string? DisplayHostValue =>
        string.IsNullOrEmpty(DisplayHost) || DisplayHost == "localhost" ? null : DisplayHost.Trim();

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

    private static IEnumerable<(string Address, string Label)> DetectLocalAddresses()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork &&
                    ua.Address.AddressFamily != AddressFamily.InterNetworkV6) continue;
                var key = ua.Address.ToString();
                if (!seen.Add(key)) continue;
                yield return (key, $"{ni.Name} ({ni.NetworkInterfaceType})");
            }
        }
    }
}

public sealed partial class NetworkAddressRow : ObservableObject
{
    public required string Address { get; init; }
    public required string Label { get; init; }
    [ObservableProperty] private bool _isBound;
}
