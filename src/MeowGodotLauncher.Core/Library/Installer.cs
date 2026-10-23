using MeowGodotLauncher.Core.Delta;
using MeowGodotLauncher.Core.GitHub;
using MeowGodotLauncher.Core.Util;

namespace MeowGodotLauncher.Core.Library;

public sealed record InstallProgress(string Message, double? Fraction = null);

/// <summary>Installs packages of releases into a <see cref="BuildLibrary"/>, preferring delta updates.</summary>
public sealed class Installer(BuildLibrary library, GitHubReleaseClient client)
{
    /// <summary>How many releases older than the target to look at for delta chains.</summary>
    public int MaxChainReleases { get; set; } = 30;

    /// <summary>Called with a message when a delta install fails and the full download is used instead.</summary>
    public Action<string>? Log { get; set; }

    public string DownloadsDirectory => Path.Combine(library.StagingRoot, "downloads");

    /// <summary>Works out the smallest download for a package of <paramref name="target"/>.</summary>
    /// <param name="releases">All known releases, newest first.</param>
    public async Task<InstallPlan?> PlanAsync(ReleaseInfo target, string packageId, IReadOnlyList<ReleaseInfo> releases,
        bool useDeltas, CancellationToken cancellationToken = default)
    {
        var asset = target.Assets.FirstOrDefault(a => PackageKinds.MatchesAsset(packageId, a.Name));
        if (asset is null)
            return null;
        if (!useDeltas)
            return InstallPlan.Full(packageId, target.Tag, asset.Size);

        var installed = library.List()
            .Where(b => b.Tag != target.Tag && b.Packages.ContainsKey(packageId))
            .Select(b => b.Tag)
            .ToHashSet();
        if (installed.Count == 0)
            return InstallPlan.Full(packageId, target.Tag, asset.Size);

        // Only releases between the oldest installed build and the target can be part of a chain.
        int targetIndex = releases.ToList().FindIndex(r => r.Tag == target.Tag);
        var candidates = releases.Skip(Math.Max(0, targetIndex)).Take(MaxChainReleases + 1).ToList();
        int lastInstalled = candidates.FindLastIndex(r => installed.Contains(r.Tag));
        if (lastInstalled < 0)
            return InstallPlan.Full(packageId, target.Tag, asset.Size);

        var manifests = new Dictionary<string, ReleaseManifest>();
        foreach (var release in candidates.Take(lastInstalled + 1))
        {
            try
            {
                var manifest = await client.GetManifestAsync(release, cancellationToken);
                if (manifest is not null)
                    manifests[release.Tag] = manifest;
            }
            catch (HttpRequestException)
            {
                // A missing manifest only means fewer delta options.
            }
        }
        return UpdatePlanner.Plan(packageId, target.Tag, asset.Size, manifests, installed);
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
        return await InstallFullAsync(target, plan.PackageId, progress, cancellationToken);
    }

    private async Task<InstalledBuild> InstallFullAsync(ReleaseInfo target, string packageId,
        IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        var asset = target.Assets.FirstOrDefault(a => PackageKinds.MatchesAsset(packageId, a.Name))
            ?? throw new InvalidOperationException($"Release {target.Tag} has no {packageId} asset.");
        string? sha256 = null;
        try
        {
            sha256 = (await client.GetManifestAsync(target, cancellationToken))?.FindPackage(packageId)?.AssetSha256;
        }
        catch (HttpRequestException)
        {
            // The checksum is optional, the size is still checked.
        }

        string download = Path.Combine(DownloadsDirectory, $"{target.Tag}-{asset.Name}");
        string staging = library.CreateStagingDirectory();
        try
        {
            await client.DownloadAsync(asset.DownloadUrl, download, asset.Size, sha256,
                new Progress<long>(n => progress?.Report(new($"Downloading {asset.Name} ({FileUtil.FormatSize(n)} of {FileUtil.FormatSize(asset.Size)})", (double)n / Math.Max(1, asset.Size)))),
                cancellationToken);

            progress?.Report(new($"Extracting {asset.Name}", 0));
            await Task.Run(() => BuildLibrary.Extract(download, staging,
                new Progress<double>(f => progress?.Report(new($"Extracting {asset.Name}", f))), cancellationToken), cancellationToken);

            return library.CommitPackage(target.Tag, target.TargetCommitish, target.PublishedAt, packageId, staging, null);
        }
        finally
        {
            TryDelete(download);
            TryDeleteDirectory(staging);
        }
    }

    private async Task<InstalledBuild> InstallWithDeltasAsync(ReleaseInfo target, InstallPlan plan, IReadOnlyList<ReleaseInfo> releases,
        IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        var baseBuild = library.Find(plan.BaseTag!) ?? throw new DeltaException($"Build {plan.BaseTag} is no longer installed.");
        string current = baseBuild.PackageDirectory(plan.PackageId);
        string? previousStaging = null;

        try
        {
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                var step = plan.Steps[i];
                string stepLabel = plan.Steps.Count > 1 ? $" (step {i + 1} of {plan.Steps.Count})" : "";
                var release = releases.FirstOrDefault(r => r.Tag == step.To) ?? throw new DeltaException($"Release {step.To} not found.");
                string download = Path.Combine(DownloadsDirectory, step.Delta.Asset);
                try
                {
                    await client.DownloadAsync(client.AssetUrl(release, step.Delta.Asset), download, step.Delta.Size, step.Delta.Sha256,
                        new Progress<long>(n => progress?.Report(new($"Downloading update {step.From} → {step.To}{stepLabel} ({FileUtil.FormatSize(n)} of {FileUtil.FormatSize(step.Delta.Size)})", (double)n / Math.Max(1, step.Delta.Size)))),
                        cancellationToken);

                    string staging = library.CreateStagingDirectory();
                    string source = current;
                    progress?.Report(new($"Patching {step.From} → {step.To}{stepLabel}", 0));
                    try
                    {
                        await Task.Run(() => DeltaApplier.Apply(download, source, staging,
                            new Progress<double>(f => progress?.Report(new($"Patching {step.From} → {step.To}{stepLabel}", f))), cancellationToken), cancellationToken);
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
                finally
                {
                    TryDelete(download);
                }
            }

            var build = library.CommitPackage(target.Tag, target.TargetCommitish, target.PublishedAt, plan.PackageId, current, plan.BaseTag);
            previousStaging = null;
            return build;
        }
        finally
        {
            if (previousStaging is not null)
                TryDeleteDirectory(previousStaging);
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
