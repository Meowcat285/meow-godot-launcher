using MeowGodotLauncher.Core.Library;
using MeowGodotLauncher.Core.Util;

namespace MeowGodotLauncher.Core.Tests;

public sealed class UtilTests
{
    [Theory]
    [InlineData("../evil")]
    [InlineData("a/../../evil")]
    public void ResolveUnderRejectsEscapingPaths(string path)
    {
        Assert.Throws<InvalidDataException>(() => FileUtil.ResolveUnder(Path.GetTempPath(), path));
    }

    [Fact]
    public void ResolveUnderAcceptsNestedPaths()
    {
        string root = Path.Combine(Path.GetTempPath(), "root");
        Assert.Equal(Path.Combine(root, "a", "b.txt"), FileUtil.ResolveUnder(root, "a/b.txt"));
    }

    [Theory]
    [InlineData("2026.10.6", true)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("", false)]
    public void ChecksSafeNames(string name, bool safe) => Assert.Equal(safe, FileUtil.IsSafeName(name));

    [Fact]
    public void SplitsArguments()
    {
        Assert.Equal(["--path", "/my project", "-e"], EditorLauncher.SplitArguments("--path \"/my project\"  -e"));
        Assert.Empty(EditorLauncher.SplitArguments("   "));
    }

    [Theory]
    [InlineData("editor-linux-x86_64", "Godot_v4.8.beta.mono_linux_x86_64.zip", true)]
    [InlineData("editor-windows-x86_64", "Godot_v4.8.beta.mono_win64.zip", true)]
    [InlineData("editor-macos-universal", "Godot_v4.8.beta.mono_macos.universal.zip", true)]
    [InlineData("export-templates", "Godot_v4.8.beta.mono_export_templates.tpz", true)]
    [InlineData("editor-linux-x86_64", "Godot_v4.8.beta.mono_win64.zip", false)]
    public void MatchesReleaseAssets(string package, string asset, bool matches) =>
        Assert.Equal(matches, PackageKinds.MatchesAsset(package, asset));
}
