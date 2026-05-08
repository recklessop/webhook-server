using System.Drawing;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Forms;

namespace WebhookServer.Gui.Services;

/// <summary>
/// Minimal system tray icon using Windows Forms NotifyIcon. Owns a context menu
/// (Open / Restart service / Exit) and toggles the main window visibility on
/// double-click. Hide-to-tray on minimize is wired in MainWindow.xaml.cs.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Func<Window?> _resolveMainWindow;
    private readonly Func<Task> _restartServiceAsync;
    private readonly Action _onExit;

    public TrayIcon(Func<Window?> resolveMainWindow, Func<Task> restartServiceAsync, Action onExit)
    {
        _resolveMainWindow = resolveMainWindow;
        _restartServiceAsync = restartServiceAsync;
        _onExit = onExit;

        _icon = new NotifyIcon
        {
            Icon = LoadEmbeddedIcon(),
            Text = "Webhook Server",
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ShowMainWindow();
        _icon.ContextMenuStrip = BuildMenu();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("&Open Webhook Server", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("&Restart service", null, async (_, _) => await _restartServiceAsync().ConfigureAwait(false));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("E&xit", null, (_, _) => _onExit());
        return menu;
    }

    private void ShowMainWindow()
    {
        var w = _resolveMainWindow();
        if (w is null) return;
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show();
        w.Activate();
        w.Topmost = true;
        w.Topmost = false;
    }

    private static Icon LoadEmbeddedIcon()
    {
        // Pulled from the WPF Resource items in the csproj via the application
        // pack URI. Falling back to SystemIcons keeps the tray usable if the
        // resource is somehow missing.
        try
        {
            var uri = new Uri("pack://application:,,,/webhook-server.ico", UriKind.Absolute);
            using var stream = Application.GetResourceStream(uri).Stream;
            return new Icon(stream);
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    public void ShowBalloon(string title, string message)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.ShowBalloonTip(3000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
