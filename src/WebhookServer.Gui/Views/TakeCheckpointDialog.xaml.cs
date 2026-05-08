using System.Windows;

namespace WebhookServer.Gui.Views;

public partial class TakeCheckpointDialog : Window
{
    public string Description { get; private set; } = "";

    public TakeCheckpointDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => DescriptionBox.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Description = DescriptionBox.Text?.Trim() ?? "";
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
