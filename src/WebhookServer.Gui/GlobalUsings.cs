// Enabling UseWindowsForms (for the system tray NotifyIcon) brings the WinForms
// namespace into scope, which conflicts with WPF for several common type names.
// Alias the most-used types to their WPF variants project-wide so existing code
// keeps compiling. Files that genuinely need a WinForms type import it explicitly
// (System.Windows.Forms.NotifyIcon etc. in Services/TrayIcon.cs).

global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
global using Clipboard = System.Windows.Clipboard;
global using TextBox = System.Windows.Controls.TextBox;
global using RadioButton = System.Windows.Controls.RadioButton;
global using MessageBoxButton = System.Windows.MessageBoxButton;
global using MessageBoxImage = System.Windows.MessageBoxImage;
global using MessageBoxResult = System.Windows.MessageBoxResult;
global using Binding = System.Windows.Data.Binding;
