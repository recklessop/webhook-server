using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
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
    [ObservableProperty] private bool _autoScrollLogs = true;
    [ObservableProperty] private ServerConfig _serverConfig = new();
    [ObservableProperty] private string _httpBaseUrl = "http://localhost:8080";
    [ObservableProperty] private string? _httpsBaseUrl;
    [ObservableProperty] private bool _minimizeToTrayEnabled;

    private readonly DispatcherTimer _logTimer;
    private readonly GuiSettings _settings;

    public MainViewModel(AdminPipeClient client)
    {
        _client = client;
        _settings = GuiSettings.Load();
        _minimizeToTrayEnabled = _settings.MinimizeToTrayEnabled;

        _logTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _logTimer.Tick += async (_, _) => await RefreshLogTailAsync();
        _logTimer.Start();
    }

    partial void OnMinimizeToTrayEnabledChanged(bool value)
    {
        _settings.MinimizeToTrayEnabled = value;
        _settings.Save();
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

                if (status is not null)
                {
                    var host = string.IsNullOrEmpty(status.DisplayHost) ? "localhost" : status.DisplayHost;
                    HttpBaseUrl = $"http://{host}:{status.HttpPort}";
                    HttpsBaseUrl = status.HttpsPort.HasValue ? $"https://{host}:{status.HttpsPort.Value}" : null;
                }

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
    private void ShowConfigCheckpoints()
    {
        var dlg = new Views.ConfigCheckpointsDialog
        {
            Owner = Application.Current.MainWindow,
            DataContext = new ConfigCheckpointsViewModel(_client),
        };
        dlg.ShowDialog();
        // After the dialog closes, the live config may have changed via rollback,
        // so refresh the main grid.
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task ExportConfigAsync()
    {
        try
        {
            var snap = await _client.GetConfigAsync().ConfigureAwait(false);
            if (snap is null) { ShowError("Export failed", new InvalidOperationException("Service did not return a config.")); return; }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"webhook-server-config-{DateTime.Now:yyyyMMdd-HHmmss}.json",
                DefaultExt = ".json",
                Filter = "JSON config (*.json)|*.json",
            };
            if (dlg.ShowDialog() != true) return;

            var json = System.Text.Json.JsonSerializer.Serialize(snap, WebhookServer.Core.Storage.ConfigJson.Pretty);
            await System.IO.File.WriteAllTextAsync(dlg.FileName, json).ConfigureAwait(false);
        }
        catch (Exception ex) { ShowError("Export failed", ex); }
    }

    [RelayCommand]
    private async Task ImportConfigAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON config (*.json)|*.json",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var json = await System.IO.File.ReadAllTextAsync(dlg.FileName).ConfigureAwait(false);
            var cfg = System.Text.Json.JsonSerializer.Deserialize<ServerConfig>(json, WebhookServer.Core.Storage.ConfigJson.Pretty);
            if (cfg is null) throw new InvalidOperationException("File did not contain a valid config.");

            var ok = MessageBox.Show(
                $"Replace the current configuration with {dlg.FileName}?\n\nA checkpoint of the current config is saved first, so you can roll back from File → Config Checkpoints.",
                "Import config",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (ok != MessageBoxResult.OK) return;

            await _client.ImportConfigAsync(cfg).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { ShowError("Import failed", ex); }
    }

    [RelayCommand]
    private async Task RestartServiceAsync()
    {
        var ok = MessageBox.Show(
            "Restart the WebhookServer service? In-flight requests will be aborted.",
            "Restart service",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;

        try
        {
            await _client.RestartListenerAsync().ConfigureAwait(false);
            await Task.Delay(2000).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ShowError("Restart failed", ex);
        }
    }

    [RelayCommand]
    private void ShowAbout()
    {
        var dlg = new Views.AboutDialog { Owner = Application.Current.MainWindow };
        dlg.ShowDialog();
    }

    [RelayCommand]
    private void OpenDocumentation()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/recklessop/webhook-server/tree/main/docs",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowError("Could not open documentation", ex);
        }
    }

    /// <summary>Raised when the user picks File -> Exit. MainWindow flips its
    /// ExitForReal flag and shuts down, bypassing the X-hides-to-tray logic.</summary>
    public event Action? RealExitRequested;

    [RelayCommand]
    private void Exit()
    {
        RealExitRequested?.Invoke();
    }

    [RelayCommand]
    private void CopyEndpointUrl()
    {
        if (SelectedEndpoint is null || string.IsNullOrEmpty(HttpBaseUrl)) return;
        var url = $"{HttpBaseUrl.TrimEnd('/')}/hook/{SelectedEndpoint.Slug}";
        try
        {
            Clipboard.SetText(url);
        }
        catch (Exception ex)
        {
            ShowError("Copy failed", ex);
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
            ServerConfig.BindAddresses = vm.BindAddressesList;
            ServerConfig.DisplayHost = vm.DisplayHostValue;
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
