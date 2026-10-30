using System.Text.Json;
using MeowGodotLauncher.Core.Delta;
using MeowGodotLauncher.Core.GitHub;
using MeowGodotLauncher.Core.Library;
using MeowGodotLauncher.Core.Util;

using static MeowGodotLauncher.Core.Tests.TestUtil;

namespace MeowGodotLauncher.Core.Tests;

/// <summary>Runs the installer against a fake GitHub serving the fixtures as two releases, 1.0 and 1.1.</summary>
public sealed class InstallerTests : IDisposable
{
    private const string Package = PackageKinds.EditorLinuxX64;
    private const string AssetName = "Godot_v4.8.beta.mono_linux_x86_64.zip";
    private const string DeltaName = "editor-linux-x86_64.from-1.0.mgdelta";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mgl-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeGitHub _github = new();
    private readonly BuildLibrary _library;
    private readonly GitHubReleaseClient _client;
    private readonly Installer _installer;
    private readonly List<string> _log = [];

    public InstallerTests()
    {
        _library = new BuildLibrary(Path.Combine(_dir, "library"));
        _client = new GitHubReleaseClient(new HttpClient(_github), "owner", "repo", null, Path.Combine(_dir, "cache"));
        _installer = new Installer(_library, _client, new HttpClient(_github)) { Log = _log.Add };

        var manifest = new ReleaseManifest
        {
            Format = ReleaseManifest.CurrentFormat,
            Tag = "1.1",
            Packages =
            [
                new PackageManifest
                {
                    Id = Package,
                    Asset = AssetName,
                    AssetSize = new FileInfo(Fixture("new.zip")).Length,
                    AssetSha256 = FileUtil.Sha256(Fixture("new.zip")),
                    Deltas = [new DeltaEntry { From = "1.0", Asset = DeltaName, Size = new FileInfo(Fixture(DeltaName)).Length, Sha256 = FileUtil.Sha256(Fixture(DeltaName)) }],
                },
            ],
        };
        _github.Files["https://dl/1.1/manifest"] = JsonSerializer.SerializeToUtf8Bytes(manifest, LauncherJsonContext.Default.ReleaseManifest);
        _github.Files["https://dl/1.1/delta"] = File.ReadAllBytes(Fixture(DeltaName));
        _github.Files["https://dl/1.1/full"] = File.ReadAllBytes(Fixture("new.zip"));
        _github.Files["https://dl/1.0/full"] = File.ReadAllBytes(Fixture("old.zip"));

        var releases = new List<ReleaseInfo>
        {
            Release("1.1", "2026-10-02T00:00:00Z",
                Asset(1, ReleaseManifest.FileName, "https://dl/1.1/manifest"),
                Asset(2, DeltaName, "https://dl/1.1/delta"),
                Asset(3, AssetName, "https://dl/1.1/full")),
            Release("1.0", "2026-10-01T00:00:00Z", Asset(4, AssetName, "https://dl/1.0/full")),
        };
        _github.Files["https://api.github.com/repos/owner/repo/releases?per_page=50"] =
            JsonSerializer.SerializeToUtf8Bytes(releases, LauncherJsonContext.Default.ListReleaseInfo);
    }

    public void Dispose() => FileUtil.DeleteDirectory(_dir);

    private ReleaseAsset Asset(long id, string name, string url) =>
        new() { Id = id, Name = name, DownloadUrl = url, Size = _github.Files[url].Length };

    private static ReleaseInfo Release(string tag, string published, params ReleaseAsset[] assets) =>
        new() { Tag = tag, PublishedAt = DateTimeOffset.Parse(published), Assets = [.. assets] };

    private Dictionary<string, string> ExpectedNew()
    {
        string expected = Path.Combine(_dir, "expected");
        BuildLibrary.Extract(Fixture("new.zip"), expected);
        return Snapshot(expected);
    }

    private async Task<IReadOnlyList<ReleaseInfo>> InstallOldAsync()
    {
        var releases = (await _client.GetReleasesAsync(50)).Releases;
        var plan = await _installer.PlanAsync(releases[1], Package, releases, useDeltas: true);
        Assert.NotNull(plan);
        Assert.False(plan.UsesDeltas);
        await _installer.InstallAsync(releases[1], plan, releases);
        return releases;
    }

    [Fact]
    public async Task UpdatesWithDelta()
    {
        var releases = await InstallOldAsync();
        Assert.Equal(["1.1", "1.0"], releases.Select(r => r.Tag));

        var plan = await _installer.PlanAsync(releases[0], Package, releases, useDeltas: true);
        Assert.NotNull(plan);
        Assert.Equal("1.0", plan.BaseTag);
        Assert.True(plan.DownloadSize < plan.FullSize);

        var build = await _installer.InstallAsync(releases[0], plan, releases);

        Assert.Equal("1.0", build.Packages[Package].PatchedFrom);
        Assert.Equal(ExpectedNew(), Snapshot(build.PackageDirectory(Package)));
        Assert.DoesNotContain("https://dl/1.1/full", _github.Requested);
        Assert.Empty(_log);
        // The base build is left untouched.
        Assert.NotNull(_library.Find("1.0"));
    }

    [Fact]
    public async Task FallsBackToFullDownloadWhenBaseIsModified()
    {
        var releases = await InstallOldAsync();
        string exe = Path.Combine(_library.Find("1.0")!.PackageDirectory(Package), "pkg", "bin", "godot.linuxbsd.editor.x86_64");
        var bytes = File.ReadAllBytes(exe);
        // Patches don't necessarily read every byte of the source.
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] ^= 0xFF;
        File.WriteAllBytes(exe, bytes);

        var plan = await _installer.PlanAsync(releases[0], Package, releases, useDeltas: true);
        var build = await _installer.InstallAsync(releases[0], plan!, releases);

        Assert.Null(build.Packages[Package].PatchedFrom);
        Assert.Equal(ExpectedNew(), Snapshot(build.PackageDirectory(Package)));
        Assert.Contains("https://dl/1.1/full", _github.Requested);
        Assert.Single(_log);
        Assert.False(Directory.Exists(_library.StagingRoot) && Directory.EnumerateDirectories(_library.StagingRoot).Any(d => Path.GetFileName(d) != "downloads"));
    }

    [Fact]
    public async Task UsesCachedReleasesWhenOffline()
    {
        await _client.GetReleasesAsync(50);
        _github.Offline = true;

        var list = await _client.GetReleasesAsync(50);

        Assert.True(list.FromCache);
        Assert.NotNull(list.Warning);
        Assert.Equal(2, list.Releases.Count);
    }

    [Fact]
    public async Task RecoversFromDamagedManifestCache()
    {
        var releases = (await _client.GetReleasesAsync(50)).Releases;
        Assert.NotNull(await _client.GetManifestAsync(releases[0]));
        string cached = Path.Combine(_dir, "cache", "manifests", "1.json");
        File.WriteAllText(cached, File.ReadAllText(cached)[..20]);

        var manifest = await _client.GetManifestAsync(releases[0]);

        Assert.NotNull(manifest);
        Assert.Equal(_github.Files["https://dl/1.1/manifest"], File.ReadAllBytes(cached));
    }
}
