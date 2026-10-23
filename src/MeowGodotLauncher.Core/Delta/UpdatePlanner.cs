namespace MeowGodotLauncher.Core.Delta;

/// <summary>One delta to download and apply, upgrading <see cref="From"/> to <see cref="To"/>.</summary>
/// <remarks><see cref="Cost"/> is what will actually be downloaded, which is less than the delta's size when only
/// some of the package's files are installed.</remarks>
public sealed record PlanStep(string From, string To, DeltaEntry Delta, long Cost);

/// <summary>How to obtain a package of a release: either the full asset, or a chain of deltas from an installed build.</summary>
/// <param name="FullSize">Download size without deltas.</param>
/// <param name="Components">For export templates installed per platform, the <see cref="Library.TemplateComponents"/>
/// to install. Null to install the whole package.</param>
public sealed record InstallPlan(string PackageId, string TargetTag, long FullSize, string? BaseTag, IReadOnlyList<PlanStep> Steps,
    IReadOnlyList<string>? Components = null)
{
    public bool UsesDeltas => BaseTag is not null;

    public long DownloadSize => UsesDeltas ? Steps.Sum(s => s.Cost) : FullSize;

    public static InstallPlan Full(string packageId, string targetTag, long fullSize, IReadOnlyList<string>? components = null) =>
        new(packageId, targetTag, fullSize, null, [], components);
}

public static class UpdatePlanner
{
    /// <summary>
    /// Finds the smallest download for a package of <paramref name="targetTag"/>: deltas can be chained across
    /// releases, starting from any build in <paramref name="installedTags"/>. Falls back to the full asset when
    /// that's smaller or no chain exists.
    /// </summary>
    /// <param name="manifests">Manifests of the target and of releases that may be traversed, by tag.</param>
    /// <param name="deltaCost">Download size of a delta of a release, when it isn't the delta's full size.</param>
    public static InstallPlan Plan(string packageId, string targetTag, long fullSize,
        IReadOnlyDictionary<string, ReleaseManifest> manifests, IReadOnlySet<string> installedTags,
        Func<string, DeltaEntry, long>? deltaCost = null, IReadOnlyList<string>? components = null)
    {
        var full = InstallPlan.Full(packageId, targetTag, fullSize, components);
        if (installedTags.Count == 0)
            return full;

        // Dijkstra from the target towards older releases, following deltas backwards.
        var cost = new Dictionary<string, long> { [targetTag] = 0 };
        var next = new Dictionary<string, PlanStep>();
        var queue = new PriorityQueue<string, long>();
        queue.Enqueue(targetTag, 0);
        var done = new HashSet<string>();

        while (queue.TryDequeue(out var tag, out var tagCost))
        {
            if (!done.Add(tag))
                continue;
            if (installedTags.Contains(tag) && tag != targetTag)
            {
                // The first installed build reached is the cheapest one.
                if (tagCost >= fullSize)
                    return full;
                var steps = new List<PlanStep>();
                for (var current = tag; current != targetTag; current = next[current].To)
                    steps.Add(next[current]);
                return new InstallPlan(packageId, targetTag, fullSize, tag, steps, components);
            }
            if (tagCost >= fullSize || !manifests.TryGetValue(tag, out var manifest))
                continue;
            var package = manifest.FindPackage(packageId);
            if (package is null)
                continue;
            foreach (var delta in package.Deltas)
            {
                long stepCost = deltaCost?.Invoke(tag, delta) ?? delta.Size;
                long newCost = tagCost + stepCost;
                if (!cost.TryGetValue(delta.From, out var oldCost) || newCost < oldCost)
                {
                    cost[delta.From] = newCost;
                    next[delta.From] = new PlanStep(delta.From, tag, delta, stepCost);
                    queue.Enqueue(delta.From, newCost);
                }
            }
        }
        return full;
    }
}
