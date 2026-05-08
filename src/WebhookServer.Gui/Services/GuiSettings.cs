using System.IO;
using System.Text.Json;

namespace WebhookServer.Gui.Services;

/// <summary>
/// Per-user GUI preferences that don't belong in the service-side ServerConfig.
/// Persisted to %APPDATA%\WebhookServer\gui.json. Best-effort: failures to read
/// or write fall back silently to defaults.
/// </summary>
public sealed class GuiSettings
{
    /// <summary>
    /// When true, the X / Alt+F4 / minimize buttons hide the window to the tray
    /// and keep the GUI process alive. When false, X exits the app and minimize
    /// behaves like a normal Windows minimize.
    /// </summary>
    public bool MinimizeToTrayEnabled { get; set; } = true;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WebhookServer",
        "gui.json");

    public static GuiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                if (!string.IsNullOrWhiteSpace(json))
                    return JsonSerializer.Deserialize<GuiSettings>(json) ?? new GuiSettings();
            }
        }
        catch { /* fall through to defaults */ }
        return new GuiSettings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }
}
