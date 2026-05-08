using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WebhookServer.Gui.Services;
using WebhookServer.Gui.ViewModels;

namespace WebhookServer.Gui;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel(new AdminPipeClient());
        DataContext = vm;
        Loaded += async (_, _) => await vm.RefreshCommand.ExecuteAsync(null);
    }

    private void OnLogTailChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.AutoScrollLogs && sender is TextBox box)
            box.ScrollToEnd();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.EditEndpointCommand.CanExecute(null))
            vm.EditEndpointCommand.Execute(null);
    }
}
