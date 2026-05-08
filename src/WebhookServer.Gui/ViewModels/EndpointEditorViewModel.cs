using System.Runtime.Versioning;
using System.Text.Json;
using System.Windows;
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

    /// <summary>
    /// Proxy for <see cref="EndpointConfig.AuthMode"/> that emits change notifications
    /// for the visibility flags so the bearer/HMAC sections show/hide reactively.
    /// </summary>
    public AuthMode SelectedAuthMode
    {
        get => Endpoint.AuthMode;
        set
        {
            if (Endpoint.AuthMode == value) return;
            Endpoint.AuthMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BearerVisible));
            OnPropertyChanged(nameof(HmacVisible));
        }
    }

    public Visibility BearerVisible =>
        Endpoint.AuthMode == AuthMode.Bearer ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HmacVisible =>
        Endpoint.AuthMode == AuthMode.Hmac ? Visibility.Visible : Visibility.Collapsed;

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

    public string BearerSecret
    {
        get => Endpoint.Bearer?.Secret.Plaintext ?? "";
        set
        {
            Endpoint.Bearer ??= new BearerOptions();
            Endpoint.Bearer.Secret.Plaintext = string.IsNullOrEmpty(value) ? null : value;
            OnPropertyChanged();
        }
    }

    public string HmacSecret
    {
        get => Endpoint.Hmac?.Secret.Plaintext ?? "";
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
