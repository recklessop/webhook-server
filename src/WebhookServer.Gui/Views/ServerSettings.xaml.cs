using System.Windows;
using System.Windows.Controls;
using WebhookServer.Gui.ViewModels;

namespace WebhookServer.Gui.Views;

public partial class ServerSettings : Window
{
    public ServerSettings()
    {
        InitializeComponent();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (DataContext is ServerSettingsViewModel vm)
            vm.SaveCommand.Execute(null);
        DialogResult = true;
        Close();
    }

    private void OnPfxPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ServerSettingsViewModel vm && sender is PasswordBox box)
            vm.PfxPasswordInput = box.Password;
    }

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (DataContext is ServerSettingsViewModel vm && sender is RadioButton rb && rb.Tag is string tag)
            vm.HttpsMode = tag;
    }
}
