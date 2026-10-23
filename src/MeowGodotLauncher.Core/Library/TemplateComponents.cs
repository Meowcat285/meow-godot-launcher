namespace MeowGodotLauncher.Core.Library;

/// <summary>A set of export template files that can be installed on its own, like a platform in the editor.</summary>
public sealed record TemplateComponent(string Id, string Name, string Group, string Description, IReadOnlyList<string> Files);

/// <summary>
/// The export templates the launcher can install separately. Mirrors `_initialize_template_data()` in the
/// editor's `export_template_manager.cpp`, so the choices match what the editor offers.
/// </summary>
public static class TemplateComponents
{
    /// <summary>Folder of the templates inside the `.tpz` and inside an installed templates package.</summary>
    public const string Folder = "templates";

    /// <summary>Always installed: the editor uses it to match templates to its version.</summary>
    public const string VersionFile = "version.txt";

    public static IReadOnlyList<TemplateComponent> All { get; } =
    [
        Windows("windows-x86_64", "Windows x86_64", "x86_64", "64-bit build for Microsoft Windows, including console wrapper."),
        Windows("windows-x86_32", "Windows x86_32", "x86_32", "32-bit build for Microsoft Windows, including console wrapper."),
        Windows("windows-arm64", "Windows arm64", "arm64", "64-bit build for Microsoft Windows on ARM, including console wrapper."),
        Linux("linux-x86_64", "Linux x86_64", "x86_64", "64-bit build for Linux systems."),
        Linux("linux-x86_32", "Linux x86_32", "x86_32", "32-bit build for Linux systems."),
        Linux("linux-arm64", "Linux arm64", "arm64", "64-bit build for Linux systems on ARM."),
        Linux("linux-arm32", "Linux arm32", "arm32", "32-bit build for Linux systems on ARM."),
        new("macos", "macOS", "Desktop", "Universal build for macOS.", ["macos.zip"]),
        new("android", "Android", "Mobile", "Android APK templates and source for Gradle builds.", ["android_debug.apk", "android_release.apk", "android_source.zip"]),
        new("ios", "iOS", "Mobile", "Build for Apple's iOS.", ["ios.zip"]),
        new("visionos", "visionOS", "Mobile", "Build for Apple's visionOS.", ["visionos.zip"]),
        new("web", "Web", "Web", "Web build with threading support.", ["web_debug.zip", "web_release.zip"]),
        new("web-extensions", "Web with Extensions", "Web", "Web build with GDExtension support.", ["web_dlink_debug.zip", "web_dlink_release.zip"]),
        new("web-nothreads", "Web Single-Threaded", "Web", "Web build without threading support.", ["web_nothreads_debug.zip", "web_nothreads_release.zip"]),
        new("web-extensions-nothreads", "Web with Extensions Single-Threaded", "Web", "Web build with GDExtension support and no threading.", ["web_dlink_nothreads_debug.zip", "web_dlink_nothreads_release.zip"]),
        new("icu-data", "ICU Data", "Common", "Line breaking dictionaries for TextServer, used by certain languages.", ["icudt_godot.dat"]),
    ];

    private static TemplateComponent Windows(string id, string name, string arch, string description) =>
        new(id, name, "Desktop", description,
            [$"windows_debug_{arch}.exe", $"windows_debug_{arch}_console.exe", $"windows_release_{arch}.exe", $"windows_release_{arch}_console.exe"]);

    private static TemplateComponent Linux(string id, string name, string arch, string description) =>
        new(id, name, "Desktop", description, [$"linux_debug.{arch}", $"linux_release.{arch}"]);

    public static TemplateComponent? Find(string id) => All.FirstOrDefault(c => c.Id == id);

    /// <summary>The component exporting to the machine the launcher runs on, a sensible default selection.</summary>
    public static string? HostComponent
    {
        get
        {
            if (OperatingSystem.IsMacOS())
                return "macos";
            string arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
                System.Runtime.InteropServices.Architecture.X86 => "x86_32",
                _ => "x86_64",
            };
            if (OperatingSystem.IsWindows())
                return $"windows-{arch}";
            return OperatingSystem.IsLinux() ? $"linux-{arch}" : null;
        }
    }

    /// <summary>Paths (as in the `.tpz` and the templates package) of the files making up the given components.</summary>
    public static HashSet<string> PathsFor(IEnumerable<string> componentIds)
    {
        var paths = new HashSet<string> { $"{Folder}/{VersionFile}" };
        foreach (var id in componentIds)
            if (Find(id) is { } component)
                foreach (var file in component.Files)
                    paths.Add($"{Folder}/{file}");
        return paths;
    }

    /// <summary>Components with at least one file among <paramref name="paths"/>.</summary>
    public static IEnumerable<TemplateComponent> AvailableIn(IEnumerable<string> paths)
    {
        var set = paths.ToHashSet();
        return All.Where(c => c.Files.Any(f => set.Contains($"{Folder}/{f}")));
    }
}
