using System.Runtime.Versioning;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebhookServer.Core.Models;
using WebhookServer.Core.Storage;

namespace WebhookServer.Gui.ViewModels;

[SupportedOSPlatform("windows")]
public sealed partial class EndpointEditorViewModel : ObservableObject
{
    public EndpointConfig Endpoint { get; }
    public bool IsNew { get; }

    [ObservableProperty] private bool _accepted;

    public EndpointEditorViewModel(EndpointConfig template, bool isNew)
    {
        // Deep clone via JSON so cancel-on-close cleanly drops edits.
        var json = JsonSerializer.Serialize(template, ConfigJson.Compact);
        Endpoint = JsonSerializer.Deserialize<EndpointConfig>(json, ConfigJson.Compact)!;
        Endpoint.Bearer ??= new BearerOptions();
        Endpoint.Hmac ??= new HmacOptions();
        IsNew = isNew;
    }

    public Array AuthModes { get; } = Enum.GetValues(typeof(AuthMode));
    public Array ExecutorTypes { get; } = Enum.GetValues(typeof(ExecutorType));
    public Array ResponseModes { get; } = Enum.GetValues(typeof(ResponseMode));

    public string AllowedClientsText
    {
        get => string.Join(Environment.NewLine, Endpoint.AllowedClients);
        set
        {
            Endpoint.AllowedClients = (value ?? "").Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            OnPropertyChanged();
        }
    }

    public string ExecutableArgsText
    {
        get => string.Join(" ", Endpoint.ExecutableArgs);
        set
        {
            Endpoint.ExecutableArgs = (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            OnPropertyChanged();
        }
    }

    public string BearerSecretInput
    {
        get => "";
        set
        {
            Endpoint.Bearer ??= new BearerOptions();
            Endpoint.Bearer.Secret.Plaintext = string.IsNullOrEmpty(value) ? null : value;
            OnPropertyChanged();
        }
    }

    public string HmacSecretInput
    {
        get => "";
        set
        {
            Endpoint.Hmac ??= new HmacOptions();
            Endpoint.Hmac.Secret.Plaintext = string.IsNullOrEmpty(value) ? null : value;
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void Save() => Accepted = true;
}
