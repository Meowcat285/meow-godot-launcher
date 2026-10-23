namespace MeowGodotLauncher.Core.Library;

/// <summary>Finds the editor executable in an extracted editor package.</summary>
public static class EditorLocator
{
    private static readonly string[] NonExecutableExtensions =
        [".so", ".dll", ".pdb", ".xml", ".json", ".txt", ".md", ".nupkg", ".snupkg", ".dylib", ".plist", ".icns"];

    /// <returns>The path relative to <paramref name="packageDir"/>, with '/' separators, or null.</returns>
    public static string? Find(string packageDir, string packageId)
    {
        string? found = packageId switch
        {
            PackageKinds.EditorMacOSUniversal => FindMacOS(packageDir),
            PackageKinds.EditorWindowsX64 => FindWindows(packageDir),
            PackageKinds.EditorLinuxX64 => FindLinux(packageDir),
            _ => null,
        };
        return found is null ? null : Path.GetRelativePath(packageDir, found).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static IEnumerable<string> FilesNearTop(string dir) =>
        Directory.EnumerateFiles(dir).Concat(Directory.EnumerateDirectories(dir).SelectMany(d => Directory.EnumerateFiles(d)));

    private static string? FindMacOS(string dir)
    {
        foreach (var app in Directory.EnumerateDirectories(dir, "*.app", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1 }))
        {
            string macOS = Path.Combine(app, "Contents", "MacOS");
            if (!Directory.Exists(macOS))
                continue;
            string godot = Path.Combine(macOS, "Godot");
            return File.Exists(godot) ? godot : Directory.EnumerateFiles(macOS).FirstOrDefault();
        }
        return null;
    }

    private static string? FindWindows(string dir)
    {
        // The console wrapper only exists to show logs in a terminal.
        var exes = FilesNearTop(dir)
            .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).Contains(".console.", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return exes.FirstOrDefault(f => Path.GetFileName(f).Contains("editor", StringComparison.OrdinalIgnoreCase)) ?? exes.FirstOrDefault();
    }

    private static string? FindLinux(string dir)
    {
        var candidates = FilesNearTop(dir)
            .Where(f => Path.GetFileName(f).StartsWith("godot", StringComparison.OrdinalIgnoreCase)
                && !NonExecutableExtensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return candidates.FirstOrDefault(f => Path.GetFileName(f).Contains(".editor.", StringComparison.Ordinal)) ?? candidates.FirstOrDefault();
    }
}
