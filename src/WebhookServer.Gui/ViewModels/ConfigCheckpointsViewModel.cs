using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebhookServer.Core.Ipc;
using WebhookServer.Gui.Services;

namespace WebhookServer.Gui.ViewModels;

[SupportedOSPlatform("windows")]
public sealed partial class ConfigCheckpointsViewModel : ObservableObject
{
    private readonly AdminPipeClient _client;

    public ObservableCollection<BackupEntry> Checkpoints { get; } = new();

    [ObservableProperty] private BackupEntry? _selected;
    [ObservableProperty] private string _statusMessage = "";

    public ConfigCheckpointsViewModel(AdminPipeClient client)
    {
        _client = client;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            var list = await _client.ListBackupsAsync().ConfigureAwait(false);
            Application.Current.Dispatcher.Invoke(() =>
            {
                Checkpoints.Clear();
                foreach (var b in list) Checkpoints.Add(b);
                StatusMessage = list.Count == 0
                    ? "No checkpoints yet. Save the config or click Take Checkpoint Now."
                    : $"{list.Count} checkpoint{(list.Count == 1 ? "" : "s")}.";
            });
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() => StatusMessage = $"Could not load: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task TakeCheckpointAsync()
    {
        try
        {
            var entry = await _client.CreateCheckpointAsync().ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
            if (entry is not null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    Selected = Checkpoints.FirstOrDefault(c => c.FileName == entry.FileName);
                    StatusMessage = $"Created {entry.FileName}";
                });
            }
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() =>
                MessageBox.Show(ex.Message, "Take checkpoint failed", MessageBoxButton.OK, MessageBoxImage.Error));
        }
    }

    [RelayCommand]
    private async Task RollbackAsync()
    {
        if (Selected is null) return;

        var ok = MessageBox.Show(
            $"Roll the configuration back to the checkpoint from {Selected.SavedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}?\n\nThe current configuration is automatically saved as a new checkpoint first, so you can roll forward again.",
            "Confirm rollback",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;

        try
        {
            await _client.RestoreBackupAsync(Selected.FileName).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
            Application.Current.Dispatcher.Invoke(() =>
                StatusMessage = $"Rolled back to {Selected!.FileName}.");
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() =>
                MessageBox.Show(ex.Message, "Rollback failed", MessageBoxButton.OK, MessageBoxImage.Error));
        }
    }
}
