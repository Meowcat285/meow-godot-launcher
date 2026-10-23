using System.Diagnostics;

namespace MeowGodotLauncher.Core.Util;

public static class Shell
{
    /// <summary>Opens a folder in the file manager, or a URL in the browser.</summary>
    public static void Open(string pathOrUrl)
    {
        string opener = OperatingSystem.IsWindows() ? "explorer.exe" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        Process.Start(new ProcessStartInfo(opener) { ArgumentList = { pathOrUrl }, UseShellExecute = false })?.Dispose();
    }
}
