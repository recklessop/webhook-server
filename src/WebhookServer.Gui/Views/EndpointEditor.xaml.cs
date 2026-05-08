using System.Windows;
using System.Windows.Controls;
using WebhookServer.Gui.ViewModels;

namespace WebhookServer.Gui.Views;

public partial class EndpointEditor : Window
{
    public EndpointEditor()
    {
        InitializeComponent();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (DataContext is EndpointEditorViewModel vm)
            vm.SaveCommand.Execute(null);
        DialogResult = true;
        Close();
    }

    private void OnBearerPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is EndpointEditorViewModel vm && sender is PasswordBox box)
            vm.BearerSecretInput = box.Password;
    }

    private void OnHmacPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is EndpointEditorViewModel vm && sender is PasswordBox box)
            vm.HmacSecretInput = box.Password;
    }
}
