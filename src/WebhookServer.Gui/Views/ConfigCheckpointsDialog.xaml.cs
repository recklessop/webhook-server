using System.Windows;
using WebhookServer.Gui.ViewModels;

namespace WebhookServer.Gui.Views;

public partial class ConfigCheckpointsDialog : Window
{
    public ConfigCheckpointsDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is ConfigCheckpointsViewModel vm)
                await vm.RefreshAsync();
        };
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
