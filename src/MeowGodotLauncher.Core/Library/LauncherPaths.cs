namespace MeowGodotLauncher.Core.Library;

public static class LauncherPaths
{
    /// <summary>
    /// The launcher's own data: %LOCALAPPDATA% on Windows, ~/Library/Application Support on macOS,
    /// and $XDG_DATA_HOME (~/.local/share) on Linux.
    /// </summary>
    public static string AppData { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        OperatingSystem.IsLinux() ? "meow-godot-launcher" : "MeowGodotLauncher");

    public static string SettingsFile => Path.Combine(AppData, "settings.json");

    public static string Cache => Path.Combine(AppData, "cache");

    public static string DefaultLibrary => Path.Combine(AppData, "builds");

    /// <summary>
    /// Godot's user data folder, which holds the export templates
    /// (see `OS::get_data_path()` and `EditorPaths` in the engine).
    /// </summary>
    public static string GodotData
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Godot");
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsMacOS())
                return Path.Combine(home, "Library", "Application Support", "Godot");
            string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".local", "share") : xdg, "godot");
        }
    }

    public static string GodotExportTemplates => Path.Combine(GodotData, "export_templates");
}
