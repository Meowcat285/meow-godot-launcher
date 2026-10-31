using System.IO.Compression;
using System.Text.Json;
using MeowGodotLauncher.Core.Delta;
using MeowGodotLauncher.Core.GitHub;
using MeowGodotLauncher.Core.Library;
using MeowGodotLauncher.Core.Util;

using static MeowGodotLauncher.Core.Tests.TestUtil;

namespace MeowGodotLauncher.Core.Tests;

/// <summary>Installs export templates per platform from a fake GitHub serving two releases, 1.0 and 1.1.</summary>
public sealed class TemplateInstallTests : IDisposable
{
    private const string Package = PackageKinds.ExportTemplates;
    private const string AssetName = "Godot_v4.8.beta.mono_export_templates.tpz";
    private const string DeltaName = "export-templates.from-1.0.mgdelta";
    private const string Linux = "linux-x86_64";
    private const string Windows = "windows-x86_64";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mgl-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeGitHub _github = new();
    private readonly BuildLibrary _library;
    private readonly GitHubReleaseClient _client;
    private readonly Installer _installer;
    private readonly List<string> _log = [];
    private readonly ReleaseManifest _manifest;

    public TemplateInstallTests()
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
                    AssetSize = new FileInfo(Fixture("templates-new.zip")).Length,
                    AssetSha256 = FileUtil.Sha256(Fixture("templates-new.zip")),
                    Files = ManifestFiles(Fixture("templates-new.zip")),
                    Deltas = [new DeltaEntry { From = "1.0", Asset = DeltaName, Size = new FileInfo(Fixture(DeltaName)).Length, Sha256 = FileUtil.Sha256(Fixture(DeltaName)) }],
                },
            ],
        };
        _manifest = manifest;
        _github.Files["https://dl/1.1/manifest"] = JsonSerializer.SerializeToUtf8Bytes(manifest, LauncherJsonContext.Default.ReleaseManifest);
        _github.Files["https://dl/1.1/delta"] = File.ReadAllBytes(Fixture(DeltaName));
        _github.Files["https://dl/1.1/full"] = File.ReadAllBytes(Fixture("templates-new.zip"));
        _github.Files["https://dl/1.0/full"] = File.ReadAllBytes(Fixture("templates-old.zip"));

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

    private static List<FileEntry> ManifestFiles(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e =>
        {
            using var stream = e.Open();
            return new FileEntry { Path = e.FullName, Size = e.Length, Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)) };
        }).ToList();
    }

    private ReleaseAsset Asset(long id, string name, string url) =>
        new() { Id = id, Name = name, DownloadUrl = url, Size = _github.Files[url].Length };

    private static ReleaseInfo Release(string tag, string published, params ReleaseAsset[] assets) =>
        new() { Tag = tag, PublishedAt = DateTimeOffset.Parse(published), Assets = [.. assets] };

    /// <summary>The files of the given components in a fixture.</summary>
    private Dictionary<string, string> Expected(string fixture, params string[] components)
    {
        string expected = Path.Combine(_dir, "expected-" + Guid.NewGuid().ToString("N"));
        var paths = TemplateComponents.PathsFor(components);
        BuildLibrary.Extract(Fixture(fixture), expected, include: paths.Contains);
        return Snapshot(expected);
    }

    private async Task<IReadOnlyList<ReleaseInfo>> ReleasesAsync() => (await _client.GetReleasesAsync(50)).Releases;

    private async Task<InstalledBuild> InstallAsync(ReleaseInfo release, params string[] components)
    {
        var plan = await _installer.PlanAsync(release, Package, useDeltas: true, components);
        Assert.NotNull(plan);
        return await _installer.InstallAsync(release, plan);
    }

    [Fact]
    public async Task ListsComponentsWithSizes()
    {
        var releases = await ReleasesAsync();
        var components = await _installer.GetTemplateComponentsAsync(releases[0]);

        Assert.Equal(["windows-x86_64", "linux-x86_64", "web"], components.Select(c => c.Component.Id));
        Assert.All(components, c => Assert.True(c.Size > 0));
        Assert.Equal(["linux-x86_64", "web"], (await _installer.GetTemplateComponentsAsync(releases[1])).Select(c => c.Component.Id));
    }

    [Fact]
    public async Task DownloadsOnlySelectedPlatforms()
    {
        var releases = await ReleasesAsync();
        var plan = await _installer.PlanAsync(releases[0], Package, useDeltas: true, [Linux]);
        Assert.NotNull(plan);
        Assert.False(plan.UsesDeltas);
        Assert.True(plan.FullSize < _github.Files["https://dl/1.1/full"].Length / 2);

        var build = await _installer.InstallAsync(releases[0], plan);

        Assert.Equal([Linux], build.Packages[Package].Components);
        Assert.Equal(Expected("templates-new.zip", Linux), Snapshot(build.PackageDirectory(Package)));
        Assert.DoesNotContain("https://dl/1.1/full", _github.Requested);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(0x1ED, (int)File.GetUnixFileMode(Path.Combine(build.PackageDirectory(Package), "templates", "linux_debug.x86_64")) & 0x1FF);
    }

    [Fact]
    public async Task DownloadsWholeArchiveWithoutRangeSupport()
    {
        _github.SupportsRanges = false;
        var releases = await ReleasesAsync();

        var build = await InstallAsync(releases[0], Linux);

        Assert.Equal([Linux], build.Packages[Package].Components);
        Assert.Equal(Expected("templates-new.zip", Linux), Snapshot(build.PackageDirectory(Package)));
    }

    [Fact]
    public async Task UpdatesSelectedPlatformsWithPartOfTheDelta()
    {
        var releases = await ReleasesAsync();
        await InstallAsync(releases[1], Linux);

        var plan = await _installer.PlanAsync(releases[0], Package, useDeltas: true, [Linux]);
        Assert.NotNull(plan);
        Assert.Equal("1.0", plan.BaseTag);
        // The Windows templates added by the delta aren't downloaded.
        Assert.True(plan.DownloadSize < plan.Step!.Delta.Size / 2);

        var build = await _installer.InstallAsync(releases[0], plan);

        Assert.Empty(_log);
        Assert.Equal("1.0", build.Packages[Package].PatchedFrom);
        Assert.Equal([Linux], build.Packages[Package].Components);
        Assert.Equal(Expected("templates-new.zip", Linux), Snapshot(build.PackageDirectory(Package)));
        // Only range requests, the whole delta and templates aren't downloaded.
        Assert.DoesNotContain("https://dl/1.1/full", _github.Requested);
        Assert.DoesNotContain("https://dl/1.1/delta", _github.Requested);
    }

    [Fact]
    public async Task UpdatesPartOfAFullInstallWithTheDelta()
    {
        var releases = await ReleasesAsync();
        var plan = await _installer.PlanAsync(releases[1], Package, useDeltas: true);
        await _installer.InstallAsync(releases[1], plan!);

        var build = await InstallAsync(releases[0], "web");

        Assert.Equal("1.0", build.Packages[Package].PatchedFrom);
        Assert.Equal(Expected("templates-new.zip", "web"), Snapshot(build.PackageDirectory(Package)));
    }

    [Fact]
    public async Task DoesNotPatchFromBuildsMissingPlatforms()
    {
        var releases = await ReleasesAsync();
        await InstallAsync(releases[1], Linux);

        var plan = await _installer.PlanAsync(releases[0], Package, useDeltas: true, [Linux, "web"]);

        Assert.NotNull(plan);
        Assert.False(plan.UsesDeltas);
    }

    [Fact]
    public async Task AddsAndRemovesPlatforms()
    {
        var releases = await ReleasesAsync();
        await InstallAsync(releases[0], Linux);

        var build = await _installer.SetTemplateComponentsAsync(releases[0], [Windows, "web"], useDeltas: true);

        Assert.Equal(["web", Windows], build.Packages[Package].Components);
        Assert.Equal(Expected("templates-new.zip", Windows, "web"), Snapshot(build.PackageDirectory(Package)));
        Assert.Equal(["web", Windows], _library.Find("1.1")!.Packages[Package].Components);
    }

    [Fact]
    public async Task FullDownloadKeepsOnlySelectedPlatforms()
    {
        var paths = TemplateComponents.PathsFor([Linux]);
        var releases = await ReleasesAsync();
        var plan = await _installer.PlanAsync(releases[0], Package, useDeltas: false, [Linux]);
        _github.SupportsRanges = false;

        var build = await _installer.InstallAsync(releases[0], plan!);

        Assert.Equal(paths.Order(), Snapshot(build.PackageDirectory(Package)).Keys.Order());
    }

    [Fact]
    public async Task ChecksDownloadedTemplatesAgainstTheManifest()
    {
        _manifest.Packages[0].Files.First(f => f.Path == "templates/linux_debug.x86_64").Sha256 = new string('0', 64);
        _github.Files["https://dl/1.1/manifest"] = JsonSerializer.SerializeToUtf8Bytes(_manifest, LauncherJsonContext.Default.ReleaseManifest);
        var releases = await ReleasesAsync();
        var plan = await _installer.PlanAsync(releases[0], Package, useDeltas: false, [Linux]);

        await Assert.ThrowsAsync<InvalidDataException>(() => _installer.InstallAsync(releases[0], plan!));
        Assert.Null(_library.Find("1.1"));
    }

    [Fact]
    public async Task OneCallerCancellingDoesNotCancelOthers()
    {
        var releases = await ReleasesAsync();
        var asset = releases[0].Assets.First(a => a.Name == AssetName);

        _github.Gate = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        var cancelled = _installer.GetRemoteZipAsync(asset.DownloadUrl, asset.Size, cts.Token);
        var other = _installer.GetRemoteZipAsync(asset.DownloadUrl, asset.Size);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        _github.Gate.SetResult();
        var zip = await other;

        Assert.Contains("templates/version.txt", zip.Entries.Keys);
    }
}
