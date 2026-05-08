using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebhookServer.Core.Ipc;
using WebhookServer.Core.Models;
using WebhookServer.Gui.Services;
using WebhookServer.Gui.Views;

namespace WebhookServer.Gui.ViewModels;

[SupportedOSPlatform("windows")]
public sealed partial class MainViewModel : ObservableObject
{
    private readonly AdminPipeClient _client;

    public ObservableCollection<EndpointConfig> Endpoints { get; } = new();

    [ObservableProperty] private EndpointConfig? _selectedEndpoint;
    [ObservableProperty] private string _connectionStatus = "Disconnected";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _logTail = "";
    [ObservableProperty] private ServerConfig _serverConfig = new();

    public MainViewModel(AdminPipeClient client)
    {
        _client = client;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var status = await _client.GetStatusAsync().ConfigureAwait(false);
            var config = await _client.GetConfigAsync().ConfigureAwait(false);

            Application.Current.Dispatcher.Invoke(() =>
            {
                IsConnected = status?.Running == true;
                ConnectionStatus = IsConnected
                    ? $"Connected — HTTP {status!.HttpPort}{(status.HttpsPort.HasValue ? $" / HTTPS {status.HttpsPort}" : "")}"
                    : "Disconnected";

                Endpoints.Clear();
                if (config is not null)
                {
                    ServerConfig = config;
                    foreach (var ep in config.Endpoints) Endpoints.Add(ep);
                }
            });
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsConnected = false;
                ConnectionStatus = $"Disconnected: {ex.Message}";
            });
        }

        await RefreshLogTailAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task RefreshLogTailAsync()
    {
        try
        {
            var lines = await _client.TailLogsAsync(100).ConfigureAwait(false);
            var text = new StringBuilder();
            foreach (var line in lines) text.AppendLine(line.Message);
            Application.Current.Dispatcher.Invoke(() => LogTail = text.ToString());
        }
        catch
        {
            // ignore — main connection state already reflects pipe failure
        }
    }

    [RelayCommand]
    private async Task AddEndpointAsync()
    {
        var draft = new EndpointConfig { Id = Guid.NewGuid(), Slug = "new-hook" };
        var dlg = new EndpointEditor { Owner = Application.Current.MainWindow };
        var vm = new EndpointEditorViewModel(draft, isNew: true);
        dlg.DataContext = vm;
        if (dlg.ShowDialog() != true) return;

        try
        {
            await _client.CreateEndpointAsync(vm.Endpoint).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ShowError("Create failed", ex);
        }
    }

    [RelayCommand]
    private async Task EditEndpointAsync()
    {
        if (SelectedEndpoint is null) return;
        var dlg = new EndpointEditor { Owner = Application.Current.MainWindow };
        var vm = new EndpointEditorViewModel(SelectedEndpoint, isNew: false);
        dlg.DataContext = vm;
        if (dlg.ShowDialog() != true) return;

        try
        {
            await _client.UpdateEndpointAsync(vm.Endpoint).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ShowError("Update failed", ex);
        }
    }

    [RelayCommand]
    private async Task DeleteEndpointAsync()
    {
        if (SelectedEndpoint is null) return;
        var ok = MessageBox.Show(
            $"Delete endpoint '{SelectedEndpoint.Slug}'?",
            "Confirm",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;

        try
        {
            await _client.DeleteEndpointAsync(SelectedEndpoint.Id).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ShowError("Delete failed", ex);
        }
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync(EndpointConfig? ep)
    {
        if (ep is null) return;
        try
        {
            await _client.SetEndpointEnabledAsync(ep.Id, !ep.Enabled).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ShowError("Toggle failed", ex);
        }
    }

    [RelayCommand]
    private async Task EditServerSettingsAsync()
    {
        var dlg = new ServerSettings { Owner = Application.Current.MainWindow };
        var vm = new ServerSettingsViewModel(ServerConfig);
        dlg.DataContext = vm;
        if (dlg.ShowDialog() != true) return;

        try
        {
            ServerConfig.HttpPort = vm.HttpPort;
            ServerConfig.TrustedProxies = vm.TrustedProxiesList;
            ServerConfig.HttpsBinding = vm.BuildBinding();
            await _client.InvokeAsync(AdminOps.UpdateConfig, ServerConfig).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ShowError("Save failed", ex);
        }
    }

    private static void ShowError(string title, Exception ex)
    {
        Application.Current.Dispatcher.Invoke(() =>
            MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error));
    }
}
