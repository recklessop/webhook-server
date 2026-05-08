using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WebhookServer.Gui.Services;
using WebhookServer.Gui.ViewModels;

namespace WebhookServer.Gui;

public partial class MainWindow : Window
{
    private readonly TrayIcon _tray;
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(new AdminPipeClient());
        DataContext = _vm;

        _tray = new TrayIcon(
            resolveMainWindow: () => Application.Current.MainWindow,
            restartServiceAsync: async () => await new AdminPipeClient().RestartListenerAsync());

        Loaded += async (_, _) => await _vm.RefreshCommand.ExecuteAsync(null);
        StateChanged += OnStateChanged;
        Closed += (_, _) => _tray.Dispose();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // Minimize-to-tray: hide the window when the user minimizes; restoring is
        // via the tray icon's double-click or context menu.
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            ShowInTaskbar = false;
        }
        else
        {
            ShowInTaskbar = true;
        }
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
