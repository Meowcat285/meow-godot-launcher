using System.Text.Json;
using MeowGodotLauncher.Core.Library;

namespace MeowGodotLauncher.Core.Settings;

public sealed class LauncherSettings
{
    public string Owner { get; set; } = "Meowcat285";
    public string Repository { get; set; } = "godot";

    /// <summary>Optional GitHub token, to raise the API rate limit from 60 to 5000 requests per hour.</summary>
    public string? GitHubToken { get; set; }

    /// <summary>Where builds are installed. Defaults to <see cref="LauncherPaths.DefaultLibrary"/>.</summary>
    public string? LibraryPath { get; set; }

    public int MaxReleases { get; set; } = 50;

    public bool UseDeltaUpdates { get; set; } = true;

    /// <summary>Also install export templates when installing a build.</summary>
    public bool InstallExportTemplates { get; set; }

    /// <summary>Copy a build's export templates into Godot's export templates folder when launching it.</summary>
    public bool SyncTemplatesOnLaunch { get; set; } = true;

    /// <summary>Remove a build after it has been used as the base of a delta update to a newer one.</summary>
    public bool RemoveOldBuildsAfterUpdate { get; set; }

    /// <summary>Extra command line arguments passed to the editor.</summary>
    public string LaunchArguments { get; set; } = "";

    public string ResolvedLibraryPath => string.IsNullOrWhiteSpace(LibraryPath) ? LauncherPaths.DefaultLibrary : LibraryPath;

    public static LauncherSettings Load()
    {
        try
        {
            if (File.Exists(LauncherPaths.SettingsFile))
            {
                var json = File.ReadAllText(LauncherPaths.SettingsFile);
                return JsonSerializer.Deserialize(json, LauncherJsonContext.Default.LauncherSettings) ?? new LauncherSettings();
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall back to the defaults rather than refusing to start.
        }
        return new LauncherSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(LauncherPaths.AppData);
        File.WriteAllText(LauncherPaths.SettingsFile, JsonSerializer.Serialize(this, LauncherJsonContext.Default.LauncherSettings));
    }
}
