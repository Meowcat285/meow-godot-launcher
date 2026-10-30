using MeowGodotLauncher.Core.Delta;
using MeowGodotLauncher.Core.Library;
using MeowGodotLauncher.Core.Util;

using static MeowGodotLauncher.Core.Tests.TestUtil;

namespace MeowGodotLauncher.Core.Tests;

// The fixtures are produced by the real generator in the Godot fork, see `Fixtures/make_fixtures.py`.
public sealed class DeltaApplierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mgl-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => FileUtil.DeleteDirectory(_dir);

    private (string Old, string Expected) ExtractFixtures()
    {
        string old = Path.Combine(_dir, "old");
        string expected = Path.Combine(_dir, "expected");
        BuildLibrary.Extract(Fixture("old.zip"), old);
        BuildLibrary.Extract(Fixture("new.zip"), expected);
        return (old, expected);
    }

    [Fact]
    public void AppliesDeltaGeneratedByTheFork()
    {
        var (old, expected) = ExtractFixtures();
        string target = Path.Combine(_dir, "target");

        var descriptor = DeltaApplier.Apply(Fixture("editor-linux-x86_64.from-1.0.mgdelta"), old, target);

        Assert.Equal("1.1", descriptor.To);
        Assert.Contains(descriptor.Operations, o => o.Op == DeltaOps.HPatch);
        Assert.Contains(descriptor.Operations, o => o.Op == DeltaOps.Add);
        Assert.Contains(descriptor.Operations, o => o.Op == DeltaOps.Copy && o.Source != o.Path);
        var archive = Assert.Single(descriptor.Operations, o => o.Path == "pkg/templates.zip" && o.Op == DeltaOps.Archive);
        Assert.Contains(archive.Entries!, o => o.Op == DeltaOps.HPatch);
        var apk = Assert.Single(descriptor.Operations, o => o.Path == "pkg/android.apk" && o.Op == DeltaOps.Archive);
        Assert.Contains(apk.Entries!, o => o.Op == DeltaOps.HPatch);
        var androidSource = Assert.Single(descriptor.Operations, o => o.Path == "pkg/android_source.zip" && o.Op == DeltaOps.Archive);
        var aar = Assert.Single(androidSource.Entries!, o => o.Path == "libs/lib.aar" && o.Op == DeltaOps.Archive);
        Assert.Contains(aar.Entries!, o => o.Op == DeltaOps.HPatch);
        Assert.Equal(Snapshot(expected), Snapshot(target));
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(Path.Combine(target, "pkg", "bin", "godot.linuxbsd.editor.x86_64"));
            Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Fact]
    public void AppliesBsdiffDeltaOfOlderReleases()
    {
        var (old, expected) = ExtractFixtures();
        string target = Path.Combine(_dir, "target");

        var descriptor = DeltaApplier.Apply(Fixture("editor-linux-x86_64.from-1.0.bsdiff.mgdelta"), old, target);

        Assert.Contains(descriptor.Operations, o => o.Op == DeltaOps.Patch);
        Assert.Equal(Snapshot(expected), Snapshot(target));
    }

    [Fact]
    public void RejectsModifiedSource()
    {
        var (old, _) = ExtractFixtures();
        string exe = Path.Combine(old, "pkg", "bin", "godot.linuxbsd.editor.x86_64");
        var bytes = File.ReadAllBytes(exe);
        // Patches don't necessarily read every byte of the source.
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] ^= 0xFF;
        File.WriteAllBytes(exe, bytes);

        var e = Assert.Throws<DeltaException>(() => DeltaApplier.Apply(Fixture("editor-linux-x86_64.from-1.0.mgdelta"), old, Path.Combine(_dir, "target")));
        Assert.Contains("godot.linuxbsd.editor.x86_64", e.Message);
    }

    [Fact]
    public void RejectsMissingSource()
    {
        var (old, _) = ExtractFixtures();
        File.Delete(Path.Combine(old, "pkg", "old-name.dat"));

        Assert.Throws<DeltaException>(() => DeltaApplier.Apply(Fixture("editor-linux-x86_64.from-1.0.mgdelta"), old, Path.Combine(_dir, "target")));
    }

    [Theory]
    [InlineData(PackageKinds.EditorLinuxX64, "Godot_v4.8.beta.mono_linux_x86_64/godot.linuxbsd.editor.x86_64.mono",
        "Godot_v4.8.beta.mono_linux_x86_64/GodotSharp/Api/Release/GodotSharp.dll")]
    [InlineData(PackageKinds.EditorWindowsX64, "Godot_v4.8.beta.mono_win64/godot.windows.editor.x86_64.mono.exe",
        "Godot_v4.8.beta.mono_win64/godot.windows.editor.x86_64.mono.console.exe")]
    [InlineData(PackageKinds.EditorMacOSUniversal, "Godot_mono.app/Contents/MacOS/Godot",
        "Godot_mono.app/Contents/Resources/GodotSharp/Api/Release/GodotSharp.dll")]
    public void LocatesEditorInReleaseLayouts(string package, string editor, string other)
    {
        foreach (var file in new[] { other, editor })
        {
            string path = Path.Combine(_dir, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }
        Assert.Equal(editor, EditorLocator.Find(_dir, package));
    }
}
