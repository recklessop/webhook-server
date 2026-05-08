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

    private void OnCopyBearer(object sender, RoutedEventArgs e)
    {
        if (DataContext is EndpointEditorViewModel vm && !string.IsNullOrEmpty(vm.BearerSecret))
            try { Clipboard.SetText(vm.BearerSecret); } catch { /* clipboard busy — silent */ }
    }

    private void OnCopyHmac(object sender, RoutedEventArgs e)
    {
        if (DataContext is EndpointEditorViewModel vm && !string.IsNullOrEmpty(vm.HmacSecret))
            try { Clipboard.SetText(vm.HmacSecret); } catch { /* clipboard busy — silent */ }
    }
}
