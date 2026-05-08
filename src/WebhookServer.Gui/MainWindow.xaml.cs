using System.Windows;
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
}
