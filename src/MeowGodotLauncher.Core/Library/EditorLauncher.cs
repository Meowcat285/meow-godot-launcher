using System.Diagnostics;
using MeowGodotLauncher.Core.Util;

namespace MeowGodotLauncher.Core.Library;

public static class EditorLauncher
{
    public const string TemplatesMarker = ".meow-launcher-build";

    public static Process Launch(InstalledBuild build, string arguments)
    {
        string exe = build.EditorExecutable ?? throw new InvalidOperationException($"Build {build.Tag} has no editor installed.");
        if (!File.Exists(exe))
            throw new FileNotFoundException($"The editor of build {build.Tag} is missing, try reinstalling it.", exe);

        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
        };
        foreach (var arg in SplitArguments(arguments))
            info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new InvalidOperationException("The editor didn't start.");
    }

    /// <summary>
    /// Copies the build's export templates to Godot's export templates folder for their version, so the editor
    /// finds them. All builds of the fork share a version string, so this replaces the templates of other builds.
    /// </summary>
    /// <returns>The folder the templates were installed to, or null if they were already there.</returns>
    public static string? SyncExportTemplates(InstalledBuild build)
    {
        string source = TemplatesSource(build);
        string dest = GodotTemplatesFolder(build);
        string marker = Path.Combine(dest, TemplatesMarker);
        string stamp = MarkerFor(build);
        if (File.Exists(marker) && File.ReadAllText(marker).Trim() == stamp)
            return null;

        FileUtil.DeleteDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(dest, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(target, File.GetUnixFileMode(file));
        }
        File.WriteAllText(marker, stamp);
        return dest;
    }

    /// <summary>
    /// Which build's export templates are in Godot's folder for the version of <paramref name="build"/>'s templates:
    /// its tag if the launcher put them there, "" if they were installed some other way, or null if there are none.
    /// </summary>
    public static string? SyncedTemplatesTag(InstalledBuild build)
    {
        string dest = GodotTemplatesFolder(build);
        if (!Directory.Exists(dest))
            return null;
        string marker = Path.Combine(dest, TemplatesMarker);
        return File.Exists(marker) ? File.ReadLines(marker).FirstOrDefault()?.Trim() ?? "" : "";
    }

    /// <summary>Removes export templates the launcher copied to Godot's folder, leaving others alone.</summary>
    /// <returns>Whether templates were removed.</returns>
    public static bool RemoveSyncedTemplates(InstalledBuild build)
    {
        string dest = GodotTemplatesFolder(build);
        if (!File.Exists(Path.Combine(dest, TemplatesMarker)))
            return false;
        FileUtil.DeleteDirectory(dest);
        return true;
    }

    private static string TemplatesSource(InstalledBuild build)
    {
        if (!build.HasTemplates)
            throw new InvalidOperationException($"Build {build.Tag} has no export templates installed.");
        return Path.Combine(build.PackageDirectory(PackageKinds.ExportTemplates), TemplateComponents.Folder);
    }

    private static string GodotTemplatesFolder(InstalledBuild build)
    {
        string versionFile = Path.Combine(TemplatesSource(build), TemplateComponents.VersionFile);
        if (!File.Exists(versionFile))
            throw new FileNotFoundException("The export templates have no version.txt.", versionFile);
        string version = File.ReadAllText(versionFile).Trim();
        if (!FileUtil.IsSafeName(version))
            throw new InvalidDataException($"Unexpected export templates version \"{version}\".");
        return Path.Combine(LauncherPaths.GodotExportTemplates, version);
    }

    /// <summary>Identifies the copied templates: the build's tag, then its installed components.</summary>
    private static string MarkerFor(InstalledBuild build)
    {
        var components = build.Packages[PackageKinds.ExportTemplates].Components;
        return $"{build.Tag}\n{(components is null ? "all" : string.Join(',', components))}";
    }

    /// <summary>Splits a command line on spaces, honoring double quotes.</summary>
    public static List<string> SplitArguments(string arguments)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        foreach (char c in arguments)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                    result.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }
        if (any)
            result.Add(current.ToString());
        return result;
    }
}
