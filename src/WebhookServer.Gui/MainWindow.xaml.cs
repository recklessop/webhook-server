using System.ComponentModel;
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

    /// <summary>
    /// Set to true when the user has explicitly asked to quit (File -> Exit or
    /// Tray -> Exit). The OnClosing handler reads this to decide whether to
    /// actually let the window close or hide it to the tray.
    /// </summary>
    public bool ExitForReal { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(new AdminPipeClient());
        DataContext = _vm;
        _vm.RealExitRequested += OnRealExitRequested;

        _tray = new TrayIcon(
            resolveMainWindow: () => Application.Current.MainWindow,
            restartServiceAsync: async () => await new AdminPipeClient().RestartListenerAsync(),
            onExit: OnRealExitRequested);

        Loaded += async (_, _) => await _vm.RefreshCommand.ExecuteAsync(null);
        StateChanged += OnStateChanged;
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (ExitForReal || !_vm.MinimizeToTrayEnabled)
        {
            _tray.Dispose();
            return;
        }
        // Treat the X button / Alt+F4 like a minimize: hide to tray, keep the
        // process alive so the tray icon persists.
        e.Cancel = true;
        Hide();
        ShowInTaskbar = false;
    }

    private void OnRealExitRequested()
    {
        ExitForReal = true;
        Application.Current.Shutdown();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // Minimize-to-tray: hide the window when the user minimizes IF they've
        // opted in via File -> Minimize to tray. Otherwise behave like a normal
        // Windows minimize.
        if (WindowState == WindowState.Minimized && _vm.MinimizeToTrayEnabled)
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
