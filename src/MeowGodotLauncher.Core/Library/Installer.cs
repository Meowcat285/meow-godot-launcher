using System.Text.Json;
using MeowGodotLauncher.Core.Delta;
using MeowGodotLauncher.Core.GitHub;
using MeowGodotLauncher.Core.Remote;
using MeowGodotLauncher.Core.Util;

namespace MeowGodotLauncher.Core.Library;

public sealed record InstallProgress(string Message, double? Fraction = null);

/// <summary>Installs packages of releases into a <see cref="BuildLibrary"/>, preferring delta updates.</summary>
/// <remarks>
/// Export templates can be installed per platform (<see cref="TemplateComponents"/>). Their files, and the parts of
/// deltas they need, are then downloaded with range requests instead of fetching whole assets.
/// </remarks>
public sealed class Installer(BuildLibrary library, GitHubReleaseClient client, HttpClient http)
{
    private readonly Dictionary<string, Task<RemoteZip>> _remoteZips = [];
    private readonly Dictionary<string, Task<DeltaDescriptor>> _descriptors = [];

    /// <summary>How many releases older than the target to look at for delta chains.</summary>
    public int MaxChainReleases { get; set; } = 30;

    /// <summary>Called with a message when a delta install fails and the full download is used instead.</summary>
    public Action<string>? Log { get; set; }

    public string DownloadsDirectory => Path.Combine(library.StagingRoot, "downloads");

    private static ReleaseAsset? FindAsset(ReleaseInfo release, string packageId) =>
        release.Assets.FirstOrDefault(a => PackageKinds.MatchesAsset(packageId, a.Name));

    /// <summary>Reads the file list of a zip asset, cached for the lifetime of the installer.</summary>
    public Task<RemoteZip> GetRemoteZipAsync(string url, long size, CancellationToken cancellationToken = default)
    {
        Task<RemoteZip> task;
        lock (_remoteZips)
        {
            // Shared between callers, so it isn't cancelled with any one of them.
            if (!_remoteZips.TryGetValue(url, out task!) || task.IsFaulted || task.IsCanceled)
                _remoteZips[url] = task = RemoteZip.OpenAsync(http, url, size, CancellationToken.None);
        }
        return task.WaitAsync(cancellationToken);
    }

    /// <summary>Lists the export templates of a release that can be installed separately, with their download sizes.</summary>
    public async Task<IReadOnlyList<(TemplateComponent Component, long Size)>> GetTemplateComponentsAsync(ReleaseInfo release,
        CancellationToken cancellationToken = default)
    {
        var asset = FindAsset(release, PackageKinds.ExportTemplates);
        if (asset is null)
            return [];
        var zip = await GetRemoteZipAsync(asset.DownloadUrl, asset.Size, cancellationToken);
        return TemplateComponents.AvailableIn(zip.Entries.Keys)
            .Select(c => (c, TemplateComponents.PathsFor([c.Id]).Sum(p => zip.Entries.GetValueOrDefault(p)?.CompressedSize ?? 0)))
            .ToList();
    }

    /// <summary>Works out the smallest download for a package of <paramref name="target"/>.</summary>
    /// <param name="releases">All known releases, newest first.</param>
    /// <param name="components">For export templates, the components to install, or null for all of them.</param>
    public async Task<InstallPlan?> PlanAsync(ReleaseInfo target, string packageId, IReadOnlyList<ReleaseInfo> releases,
        bool useDeltas, IReadOnlyList<string>? components = null, CancellationToken cancellationToken = default)
    {
        var asset = FindAsset(target, packageId);
        if (asset is null)
            return null;

        HashSet<string>? paths = null;
        long fullSize = asset.Size;
        if (components is not null)
        {
            paths = TemplateComponents.PathsFor(components);
            try
            {
                var zip = await GetRemoteZipAsync(asset.DownloadUrl, asset.Size, cancellationToken);
                fullSize = paths.Sum(p => zip.Entries.GetValueOrDefault(p)?.CompressedSize ?? 0);
            }
            catch (RangeNotSupportedException)
            {
                // Falls back to downloading the whole archive.
            }
        }
        if (!useDeltas)
            return InstallPlan.Full(packageId, target.Tag, fullSize, components);

        // A build can only be the base of a delta if it has everything that's going to be installed.
        var installed = library.List()
            .Where(b => b.Tag != target.Tag && b.Packages.TryGetValue(packageId, out var p)
                && (components is null ? p.Components is null : p.Components is null || components.All(p.Components.Contains)))
            .Select(b => b.Tag)
            .ToHashSet();
        if (installed.Count == 0)
            return InstallPlan.Full(packageId, target.Tag, fullSize, components);

        // Only releases between the oldest installed build and the target can be part of a chain.
        int targetIndex = releases.ToList().FindIndex(r => r.Tag == target.Tag);
        var candidates = releases.Skip(Math.Max(0, targetIndex)).Take(MaxChainReleases + 1).ToList();
        int lastInstalled = candidates.FindLastIndex(r => installed.Contains(r.Tag));
        if (lastInstalled < 0)
            return InstallPlan.Full(packageId, target.Tag, fullSize, components);

        var manifests = new Dictionary<string, ReleaseManifest>();
        var partialCosts = new Dictionary<(string, string), long>();
        foreach (var release in candidates.Take(lastInstalled + 1))
        {
            try
            {
                var manifest = await client.GetManifestAsync(release, cancellationToken);
                if (manifest is null)
                    continue;
                manifests[release.Tag] = manifest;
                if (paths is null)
                    continue;
                // With only some templates installed, only part of each delta is downloaded.
                foreach (var delta in manifest.FindPackage(packageId)?.Deltas ?? [])
                    partialCosts[(release.Tag, delta.Asset)] = await PartialDeltaCostAsync(release, delta, paths, cancellationToken);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or DeltaException or JsonException)
            {
                // A missing manifest or delta only means fewer delta options.
            }
        }

        Func<string, DeltaEntry, long>? cost = paths is null
            ? null
            : (tag, delta) => partialCosts.TryGetValue((tag, delta.Asset), out var c) ? c : long.MaxValue / 4;
        return UpdatePlanner.Plan(packageId, target.Tag, fullSize, manifests, installed, cost, components);
    }

    private async Task<long> PartialDeltaCostAsync(ReleaseInfo release, DeltaEntry delta, HashSet<string> paths, CancellationToken cancellationToken)
    {
        var (zip, descriptor) = await GetDeltaAsync(release, delta, cancellationToken);
        return (zip.Entries.GetValueOrDefault(DeltaDescriptor.EntryName)?.CompressedSize ?? 0)
            + NeededEntries(descriptor, paths).Sum(e => zip.Entries.GetValueOrDefault(e)?.CompressedSize ?? 0);
    }

    private static IEnumerable<string> NeededEntries(DeltaDescriptor descriptor, HashSet<string> paths) =>
        descriptor.Operations.Where(o => paths.Contains(o.Path)).SelectMany(DeltaApplier.DataEntries).Distinct();

    private async Task<(RemoteZip Zip, DeltaDescriptor Descriptor)> GetDeltaAsync(ReleaseInfo release, DeltaEntry delta, CancellationToken cancellationToken)
    {
        string url = client.AssetUrl(release, delta.Asset);
        var zip = await GetRemoteZipAsync(url, delta.Size, cancellationToken);
        Task<DeltaDescriptor> task;
        lock (_descriptors)
        {
            if (!_descriptors.TryGetValue(url, out task!) || task.IsFaulted || task.IsCanceled)
                _descriptors[url] = task = ReadRemoteDescriptorAsync(zip, CancellationToken.None);
        }
        return (zip, await task.WaitAsync(cancellationToken));
    }

    private static async Task<DeltaDescriptor> ReadRemoteDescriptorAsync(RemoteZip zip, CancellationToken cancellationToken)
    {
        var entry = zip.Entries.GetValueOrDefault(DeltaDescriptor.EntryName) ?? throw new DeltaException("Delta archive has no descriptor.");
        using var buffer = new MemoryStream();
        await zip.ExtractAsync(entry, buffer, null, cancellationToken);
        buffer.Position = 0;
        return DeltaApplier.ReadDescriptor(buffer);
    }

    public async Task<InstalledBuild> InstallAsync(ReleaseInfo target, InstallPlan plan, IReadOnlyList<ReleaseInfo> releases,
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (plan.UsesDeltas)
        {
            try
            {
                return await InstallWithDeltasAsync(target, plan, releases, progress, cancellationToken);
            }
            catch (Exception e) when (e is DeltaException or IOException or HttpRequestException or InvalidDataException)
            {
                Log?.Invoke($"Delta update of {plan.PackageId} to {target.Tag} failed, downloading the full build instead: {e.Message}");
            }
        }
        if (plan.Components is not null)
        {
            try
            {
                return await InstallComponentsAsync(target, plan.PackageId, plan.Components, progress, cancellationToken);
            }
            catch (RangeNotSupportedException)
            {
                // Download the whole archive instead, below.
            }
        }
        return await InstallFullAsync(target, plan.PackageId, plan.Components, progress, cancellationToken);
    }

    private async Task<InstalledBuild> InstallFullAsync(ReleaseInfo target, string packageId, IReadOnlyList<string>? components,
        IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        var asset = FindAsset(target, packageId) ?? throw new InvalidOperationException($"Release {target.Tag} has no {packageId} asset.");
        string? sha256 = (await TryGetPackageManifestAsync(target, packageId, cancellationToken))?.AssetSha256;

        string download = Path.Combine(DownloadsDirectory, $"{target.Tag}-{asset.Name}");
        string staging = library.CreateStagingDirectory();
        try
        {
            await client.DownloadAsync(asset.DownloadUrl, download, asset.Size, sha256,
                new Progress<long>(n => progress?.Report(new($"Downloading {asset.Name} ({FileUtil.FormatSize(n)} of {FileUtil.FormatSize(asset.Size)})", (double)n / Math.Max(1, asset.Size)))),
                cancellationToken);

            var paths = components is null ? null : TemplateComponents.PathsFor(components);
            progress?.Report(new($"Extracting {asset.Name}", 0));
            await Task.Run(() => BuildLibrary.Extract(download, staging,
                new Progress<double>(f => progress?.Report(new($"Extracting {asset.Name}", f))), cancellationToken,
                paths is null ? null : paths.Contains), cancellationToken);

            return library.CommitPackage(target.Tag, target.TargetCommitish, target.PublishedAt, packageId, staging, null, components);
        }
        finally
        {
            TryDelete(download);
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>The manifest entry of a package, or null when the release has none or it can't be read.</summary>
    /// <remarks>Checksums from it are optional, sizes and CRCs are still checked without them.</remarks>
    private async Task<PackageManifest?> TryGetPackageManifestAsync(ReleaseInfo release, string packageId, CancellationToken cancellationToken)
    {
        try
        {
            return (await client.GetManifestAsync(release, cancellationToken))?.FindPackage(packageId);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Downloads only the files of some components, with range requests into the release asset.</summary>
    private async Task<InstalledBuild> InstallComponentsAsync(ReleaseInfo target, string packageId, IReadOnlyList<string> components,
        IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        string staging = library.CreateStagingDirectory();
        try
        {
            await DownloadEntriesAsync(target, packageId, TemplateComponents.PathsFor(components), staging, progress, cancellationToken);
            return library.CommitPackage(target.Tag, target.TargetCommitish, target.PublishedAt, packageId, staging, null, components);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private async Task DownloadEntriesAsync(ReleaseInfo release, string packageId, IEnumerable<string> paths, string destination,
        IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        var asset = FindAsset(release, packageId) ?? throw new InvalidOperationException($"Release {release.Tag} has no {packageId} asset.");
        var zip = await GetRemoteZipAsync(asset.DownloadUrl, asset.Size, cancellationToken);
        var entries = paths.Select(p => zip.Entries.GetValueOrDefault(p)).OfType<RemoteZipEntry>().Where(e => !e.IsDirectory).ToList();
        // The zip's CRCs come from the same server as the data, so check against the manifest's SHA-256 too.
        var expected = (await TryGetPackageManifestAsync(release, packageId, cancellationToken))?.Files.ToDictionary(f => f.Path);
        long total = Math.Max(1, entries.Sum(e => e.CompressedSize));
        long done = 0;
        foreach (var entry in entries)
        {
            string name = Path.GetFileName(entry.Name);
            long before = done;
            string path = FileUtil.ResolveUnder(destination, entry.Name);
            string download = path + ".download";
            try
            {
                await zip.ExtractToFileAsync(entry, download,
                    new Progress<long>(n => progress?.Report(new($"Downloading {name} ({FileUtil.FormatSize(before + n)} of {FileUtil.FormatSize(total)})", (double)(before + n) / total))),
                    cancellationToken);
                if (expected?.GetValueOrDefault(entry.Name) is { } file
                    && (new FileInfo(download).Length != file.Size || !string.Equals(FileUtil.Sha256(download), file.Sha256, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"\"{entry.Name}\" doesn't match the release's checksum.");
                File.Move(download, path, overwrite: true);
            }
            finally
            {
                TryDelete(download);
            }
            done += entry.CompressedSize;
            // Same permissions as extracting the whole archive.
            int mode = (entry.ExternalAttributes >> 16) & 0x1FF;
            FileUtil.SetMode(path, mode == 0 ? 0x1A4 : mode);
        }
    }

    /// <summary>
    /// Adds or removes export template components of an installed build, downloading only the added files.
    /// Installs the templates if the build has none yet.
    /// </summary>
    public async Task<InstalledBuild> SetTemplateComponentsAsync(ReleaseInfo release, IReadOnlyList<string> components,
        IReadOnlyList<ReleaseInfo> releases, bool useDeltas, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        const string packageId = PackageKinds.ExportTemplates;
        var build = library.Find(release.Tag);
        if (build is null || !build.Packages.TryGetValue(packageId, out var package))
        {
            var plan = await PlanAsync(release, packageId, releases, useDeltas, components, cancellationToken)
                ?? throw new InvalidOperationException($"Release {release.Tag} has no export templates.");
            return await InstallAsync(release, plan, releases, progress, cancellationToken);
        }

        string dir = build.PackageDirectory(packageId);
        var installedPaths = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f).Replace(Path.DirectorySeparatorChar, '/'))
            .ToHashSet();
        var wanted = TemplateComponents.PathsFor(components);
        // Files of no known component are left alone, they may be needed by every platform.
        var removable = TemplateComponents.PathsFor(TemplateComponents.All.Select(c => c.Id));
        removable.ExceptWith(wanted);

        await DownloadEntriesAsync(release, packageId, wanted.Where(p => !installedPaths.Contains(p)), dir, progress, cancellationToken);
        foreach (var path in installedPaths.Where(removable.Contains))
            File.Delete(FileUtil.ResolveUnder(dir, path));

        package.Components = components.Order().ToList();
        library.Save(build);
        return build;
    }

    private async Task<InstalledBuild> InstallWithDeltasAsync(ReleaseInfo target, InstallPlan plan, IReadOnlyList<ReleaseInfo> releases,
        IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        var baseBuild = library.Find(plan.BaseTag!) ?? throw new DeltaException($"Build {plan.BaseTag} is no longer installed.");
        string current = baseBuild.PackageDirectory(plan.PackageId);
        string? previousStaging = null;
        var paths = plan.Components is null ? null : TemplateComponents.PathsFor(plan.Components);

        try
        {
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                var step = plan.Steps[i];
                string label = $"{step.From} → {step.To}{(plan.Steps.Count > 1 ? $" (step {i + 1} of {plan.Steps.Count})" : "")}";
                var release = releases.FirstOrDefault(r => r.Tag == step.To) ?? throw new DeltaException($"Release {step.To} not found.");
                string staging = library.CreateStagingDirectory();
                string source = current;
                var applyProgress = new Progress<double>(f => progress?.Report(new($"Patching {label}", f)));
                try
                {
                    if (paths is null)
                    {
                        string download = FileUtil.ResolveUnder(DownloadsDirectory, step.Delta.Asset);
                        try
                        {
                            await client.DownloadAsync(client.AssetUrl(release, step.Delta.Asset), download, step.Delta.Size, step.Delta.Sha256,
                                new Progress<long>(n => progress?.Report(new($"Downloading update {label} ({FileUtil.FormatSize(n)} of {FileUtil.FormatSize(step.Delta.Size)})", (double)n / Math.Max(1, step.Delta.Size)))),
                                cancellationToken);
                            progress?.Report(new($"Patching {label}", 0));
                            await Task.Run(() => DeltaApplier.Apply(download, source, staging, applyProgress, cancellationToken), cancellationToken);
                        }
                        finally
                        {
                            TryDelete(download);
                        }
                    }
                    else
                    {
                        await ApplyPartialDeltaAsync(release, step, paths, source, staging, label, progress, applyProgress, cancellationToken);
                    }
                }
                catch
                {
                    TryDeleteDirectory(staging);
                    throw;
                }

                // Intermediate results are only needed for the next step.
                if (previousStaging is not null)
                    TryDeleteDirectory(previousStaging);
                previousStaging = staging;
                current = staging;
            }

            var build = library.CommitPackage(target.Tag, target.TargetCommitish, target.PublishedAt, plan.PackageId, current, plan.BaseTag, plan.Components);
            previousStaging = null;
            return build;
        }
        finally
        {
            if (previousStaging is not null)
                TryDeleteDirectory(previousStaging);
        }
    }

    /// <summary>Downloads only the data entries of a delta that the installed files need, then applies them.</summary>
    private async Task ApplyPartialDeltaAsync(ReleaseInfo release, PlanStep step, HashSet<string> paths, string source, string staging,
        string label, IProgress<InstallProgress>? progress, IProgress<double> applyProgress, CancellationToken cancellationToken)
    {
        var (zip, descriptor) = await GetDeltaAsync(release, step.Delta, cancellationToken);
        string dataDir = Path.Combine(DownloadsDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            var entries = NeededEntries(descriptor, paths)
                .Select(e => zip.Entries.GetValueOrDefault(e) ?? throw new DeltaException($"Delta archive is missing entry \"{e}\"."))
                .ToList();
            long total = Math.Max(1, entries.Sum(e => e.CompressedSize));
            long done = 0;
            var files = new Dictionary<string, string>();
            foreach (var entry in entries)
            {
                string file = Path.Combine(dataDir, files.Count.ToString());
                long before = done;
                await zip.ExtractToFileAsync(entry, file,
                    new Progress<long>(n => progress?.Report(new($"Downloading update {label} ({FileUtil.FormatSize(before + n)} of {FileUtil.FormatSize(total)})", (double)(before + n) / total))),
                    cancellationToken);
                files[entry.Name] = file;
                done += entry.CompressedSize;
            }

            progress?.Report(new($"Patching {label}", 0));
            await Task.Run(() => DeltaApplier.Apply(descriptor,
                name => files.TryGetValue(name, out var f) ? File.OpenRead(f) : throw new DeltaException($"Missing delta data \"{name}\"."),
                source, staging, op => paths.Contains(op.Path), applyProgress, cancellationToken), cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(dataDir);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            File.Delete(path + ".part");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            FileUtil.DeleteDirectory(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
