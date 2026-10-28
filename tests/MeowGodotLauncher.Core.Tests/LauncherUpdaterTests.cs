using System.Formats.Tar;
using System.IO.Compression;
using MeowGodotLauncher.Core.GitHub;
using MeowGodotLauncher.Core.SelfUpdate;

namespace MeowGodotLauncher.Core.Tests;

public sealed class LauncherUpdaterTests : IDisposable
{
    private const string Package = "MeowGodotLauncher-linux-x64.tar.gz";

    private readonly string _root = Directory.CreateTempSubdirectory("launcher-update-tests").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ReleaseInfo Release(string tag, bool prerelease = false, string asset = Package) =>
        new() { Tag = tag, Prerelease = prerelease, Assets = [new ReleaseAsset { Name = asset }] };

    [Theory]
    [InlineData("v1.3.1", "1.3.1")]
    [InlineData("v10.0.0", "10.0.0")]
    [InlineData("v1.3.1-beta", null)]
    [InlineData("1.3.1", null)]
    [InlineData("v1.3", null)]
    [InlineData("2026.10.7", null)]
    public void ParsesTags(string tag, string? expected)
    {
        Assert.Equal(expected is null ? null : Version.Parse(expected), LauncherUpdater.ParseTag(tag));
    }

    [Fact]
    public void FindsTheNewestRelease()
    {
        var releases = new[] { Release("v1.3.2"), Release("v1.10.0"), Release("v1.9.0") };
        Assert.Equal("v1.10.0", LauncherUpdater.FindUpdate(releases, new Version(1, 3, 1), Package)?.Release.Tag);
    }

    [Fact]
    public void SkipsReleasesThatArentUpdates()
    {
        var releases = new[]
        {
            Release("v1.3.1"), Release("v1.4.0", prerelease: true), Release("v1.5.0", asset: "MeowGodotLauncher-win-x64.zip"),
            Release("v1.0.0"),
        };
        Assert.Null(LauncherUpdater.FindUpdate(releases, new Version(1, 3, 1), Package));
    }

    private LauncherUpdater Updater(string installDir) =>
        new(null!, installDir, Path.Combine(_root, "work"), new Version(1, 0, 0), Package);

    [Fact]
    public void ReplacesTheFilesOfTheInstallFolder()
    {
        string install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "MeowGodotLauncher"), "old");
        File.WriteAllText(Path.Combine(install, "user-file.txt"), "mine");
        string extracted = Path.Combine(_root, "extracted");
        Directory.CreateDirectory(extracted);
        File.WriteAllText(Path.Combine(extracted, "MeowGodotLauncher"), "new");
        File.WriteAllText(Path.Combine(extracted, "libSkiaSharp.so"), "lib");

        Updater(install).Swap(extracted);

        Assert.Equal("new", File.ReadAllText(Path.Combine(install, "MeowGodotLauncher")));
        Assert.Equal("lib", File.ReadAllText(Path.Combine(install, "libSkiaSharp.so")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(install, "user-file.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(install, "MeowGodotLauncher" + LauncherUpdater.OldSuffix)));

        LauncherUpdater.CleanUp(install);
        Assert.Equal(["MeowGodotLauncher", "libSkiaSharp.so", "user-file.txt"],
            Directory.EnumerateFiles(install).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void PutsBackFilesOfAnInterruptedUpdate()
    {
        string install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "MeowGodotLauncher" + LauncherUpdater.OldSuffix), "old");

        LauncherUpdater.CleanUp(install);

        Assert.Equal("old", File.ReadAllText(Path.Combine(install, "MeowGodotLauncher")));
        Assert.Single(Directory.EnumerateFiles(install));
    }

    // App bundles are only replaced as a whole on macOS.
    [Fact]
    public void ReplacesTheAppBundle()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        string bundle = Path.Combine(_root, LauncherUpdater.MacAppName);
        Directory.CreateDirectory(Path.Combine(bundle, "Contents"));
        File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"), "old");
        string extracted = Path.Combine(_root, "extracted");
        Directory.CreateDirectory(Path.Combine(extracted, LauncherUpdater.MacAppName, "Contents"));
        File.WriteAllText(Path.Combine(extracted, LauncherUpdater.MacAppName, "Contents", "Info.plist"), "new");

        Updater(bundle).Swap(extracted);

        Assert.Equal("new", File.ReadAllText(Path.Combine(bundle, "Contents", "Info.plist")));
        LauncherUpdater.CleanUp(bundle);
        Assert.False(Directory.Exists(bundle + LauncherUpdater.OldSuffix));
    }

    [Fact]
    public void ExtractsTarGz()
    {
        string source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        string exe = Path.Combine(source, "MeowGodotLauncher");
        File.WriteAllText(exe, "exe");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(exe, (UnixFileMode)0b111_101_101);
        string archive = Path.Combine(_root, Package);
        using (var gzip = new GZipStream(File.Create(archive), CompressionLevel.Fastest))
            TarFile.CreateFromDirectory(source, gzip, includeBaseDirectory: false);

        string destination = Path.Combine(_root, "extracted");
        LauncherUpdater.Extract(archive, destination);

        string extracted = Path.Combine(destination, "MeowGodotLauncher");
        Assert.Equal("exe", File.ReadAllText(extracted));
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(extracted).HasFlag(UnixFileMode.UserExecute));
    }
}
