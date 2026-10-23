using System.Runtime.InteropServices;

namespace MeowGodotLauncher.Core.Library;

/// <summary>
/// The kinds of release assets the launcher knows how to install. Ids must match
/// `misc/scripts/generate_delta_patches.py` in the Godot fork.
/// </summary>
public static class PackageKinds
{
    public const string EditorLinuxX64 = "editor-linux-x86_64";
    public const string EditorWindowsX64 = "editor-windows-x86_64";
    public const string EditorMacOSUniversal = "editor-macos-universal";
    public const string ExportTemplates = "export-templates";

    // Release asset names look like `Godot_v4.8.beta.mono_linux_x86_64.zip`, so match on the suffix
    // to keep working when the version changes.
    private static readonly Dictionary<string, string> AssetSuffixes = new()
    {
        [EditorLinuxX64] = "_linux_x86_64.zip",
        [EditorWindowsX64] = "_win64.zip",
        [EditorMacOSUniversal] = "_macos.universal.zip",
        [ExportTemplates] = "_export_templates.tpz",
    };

    public static IReadOnlyCollection<string> All => AssetSuffixes.Keys;

    public static bool MatchesAsset(string packageId, string assetName) =>
        AssetSuffixes.TryGetValue(packageId, out var suffix) && assetName.EndsWith(suffix, StringComparison.Ordinal);

    /// <summary>The editor package for the machine the launcher runs on, or null if there is none.</summary>
    public static string? HostEditor
    {
        get
        {
            if (OperatingSystem.IsMacOS())
                return EditorMacOSUniversal;
            // x64 builds also run on Windows on Arm through emulation.
            if (OperatingSystem.IsWindows())
                return RuntimeInformation.OSArchitecture is Architecture.X64 or Architecture.Arm64 ? EditorWindowsX64 : null;
            if (OperatingSystem.IsLinux())
                return RuntimeInformation.OSArchitecture == Architecture.X64 ? EditorLinuxX64 : null;
            return null;
        }
    }
}
