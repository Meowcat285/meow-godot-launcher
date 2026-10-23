namespace MeowGodotLauncher.Core.Delta;

/// <summary>One delta to download and apply, upgrading <see cref="From"/> to <see cref="To"/>.</summary>
public sealed record PlanStep(string From, string To, DeltaEntry Delta);

/// <summary>How to obtain a package of a release: either the full asset, or a chain of deltas from an installed build.</summary>
public sealed record InstallPlan(string PackageId, string TargetTag, long FullSize, string? BaseTag, IReadOnlyList<PlanStep> Steps)
{
    public bool UsesDeltas => BaseTag is not null;

    public long DownloadSize => UsesDeltas ? Steps.Sum(s => s.Delta.Size) : FullSize;

    public static InstallPlan Full(string packageId, string targetTag, long fullSize) => new(packageId, targetTag, fullSize, null, []);
}

public static class UpdatePlanner
{
    /// <summary>
    /// Finds the smallest download for a package of <paramref name="targetTag"/>: deltas can be chained across
    /// releases, starting from any build in <paramref name="installedTags"/>. Falls back to the full asset when
    /// that's smaller or no chain exists.
    /// </summary>
    /// <param name="manifests">Manifests of the target and of releases that may be traversed, by tag.</param>
    public static InstallPlan Plan(string packageId, string targetTag, long fullSize,
        IReadOnlyDictionary<string, ReleaseManifest> manifests, IReadOnlySet<string> installedTags)
    {
        var full = InstallPlan.Full(packageId, targetTag, fullSize);
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
                return new InstallPlan(packageId, targetTag, fullSize, tag, steps);
            }
            if (tagCost >= fullSize || !manifests.TryGetValue(tag, out var manifest))
                continue;
            var package = manifest.FindPackage(packageId);
            if (package is null)
                continue;
            foreach (var delta in package.Deltas)
            {
                long newCost = tagCost + delta.Size;
                if (!cost.TryGetValue(delta.From, out var oldCost) || newCost < oldCost)
                {
                    cost[delta.From] = newCost;
                    next[delta.From] = new PlanStep(delta.From, tag, delta);
                    queue.Enqueue(delta.From, newCost);
                }
            }
        }
        return full;
    }
}
