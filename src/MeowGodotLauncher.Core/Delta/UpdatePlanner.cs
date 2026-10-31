namespace MeowGodotLauncher.Core.Delta;

/// <summary>A delta to download and apply, upgrading <see cref="From"/> to <see cref="To"/>.</summary>
/// <remarks><see cref="Cost"/> is what will actually be downloaded, which is less than the delta's size when only
/// some of the package's files are installed.</remarks>
public sealed record PlanStep(string From, string To, DeltaEntry Delta, long Cost);

/// <summary>How to obtain a package of a release: either the full asset, or one delta from an installed build.</summary>
/// <param name="FullSize">Download size without deltas.</param>
/// <param name="Components">For export templates installed per platform, the <see cref="Library.TemplateComponents"/>
/// to install. Null to install the whole package.</param>
public sealed record InstallPlan(string PackageId, string TargetTag, long FullSize, PlanStep? Step,
    IReadOnlyList<string>? Components = null)
{
    public bool UsesDeltas => Step is not null;

    /// <summary>The installed build the delta upgrades from.</summary>
    public string? BaseTag => Step?.From;

    public long DownloadSize => Step?.Cost ?? FullSize;

    public static InstallPlan Full(string packageId, string targetTag, long fullSize, IReadOnlyList<string>? components = null) =>
        new(packageId, targetTag, fullSize, null, components);
}

public static class UpdatePlanner
{
    /// <summary>
    /// Finds the smallest download for a package of <paramref name="targetTag"/>: the cheapest of its deltas from a
    /// build in <paramref name="installedTags"/>, or the full asset when that's smaller or no such delta exists.
    /// Deltas aren't chained: CI generates them from the builds people are likely to have installed.
    /// </summary>
    /// <param name="manifest">Manifest of the target, null if it has none.</param>
    /// <param name="deltaCost">Download size of a delta, when it isn't the delta's full size.</param>
    public static InstallPlan Plan(string packageId, string targetTag, long fullSize, ReleaseManifest? manifest,
        IReadOnlySet<string> installedTags, Func<DeltaEntry, long>? deltaCost = null, IReadOnlyList<string>? components = null)
    {
        var best = (manifest?.FindPackage(packageId)?.Deltas ?? [])
            .Where(d => d.From != targetTag && installedTags.Contains(d.From))
            .Select(d => new PlanStep(d.From, targetTag, d, deltaCost?.Invoke(d) ?? d.Size))
            .MinBy(s => s.Cost);
        return best is not null && best.Cost < fullSize
            ? new InstallPlan(packageId, targetTag, fullSize, best, components)
            : InstallPlan.Full(packageId, targetTag, fullSize, components);
    }
}
