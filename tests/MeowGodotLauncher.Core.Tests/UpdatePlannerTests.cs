using MeowGodotLauncher.Core.Delta;

namespace MeowGodotLauncher.Core.Tests;

public sealed class UpdatePlannerTests
{
    private const string Package = "editor-linux-x86_64";

    private static ReleaseManifest Manifest(string tag, params (string From, long Size)[] deltas) => new()
    {
        Format = ReleaseManifest.CurrentFormat,
        Tag = tag,
        Packages =
        [
            new PackageManifest
            {
                Id = Package,
                Deltas = deltas.Select(d => new DeltaEntry { From = d.From, Size = d.Size, Asset = $"{Package}.from-{d.From}.mgdelta" }).ToList(),
            },
        ],
    };

    private static Dictionary<string, ReleaseManifest> Manifests(params ReleaseManifest[] manifests) => manifests.ToDictionary(m => m.Tag);

    [Fact]
    public void UsesFullDownloadWithoutInstalledBuilds()
    {
        var plan = UpdatePlanner.Plan(Package, "3", 100, Manifests(Manifest("3", ("2", 5))), new HashSet<string>());
        Assert.False(plan.UsesDeltas);
        Assert.Equal(100, plan.DownloadSize);
    }

    [Fact]
    public void UsesDirectDelta()
    {
        var plan = UpdatePlanner.Plan(Package, "3", 100, Manifests(Manifest("3", ("2", 5), ("1", 8))), new HashSet<string> { "1", "2" });
        Assert.Equal("2", plan.BaseTag);
        Assert.Equal(5, plan.DownloadSize);
        Assert.Single(plan.Steps);
    }

    [Fact]
    public void ChainsDeltasAcrossReleases()
    {
        var manifests = Manifests(Manifest("4", ("3", 5)), Manifest("3", ("2", 5)), Manifest("2", ("1", 5)));
        var plan = UpdatePlanner.Plan(Package, "4", 100, manifests, new HashSet<string> { "1" });

        Assert.Equal("1", plan.BaseTag);
        Assert.Equal(["1", "2", "3"], plan.Steps.Select(s => s.From));
        Assert.Equal(["2", "3", "4"], plan.Steps.Select(s => s.To));
        Assert.Equal(15, plan.DownloadSize);
    }

    [Fact]
    public void PrefersCheapestChain()
    {
        // 1 -> 3 directly (30) is worse than 1 -> 2 -> 3 (10 + 10).
        var manifests = Manifests(Manifest("3", ("2", 10), ("1", 30)), Manifest("2", ("1", 10)));
        var plan = UpdatePlanner.Plan(Package, "3", 100, manifests, new HashSet<string> { "1" });
        Assert.Equal(20, plan.DownloadSize);
        Assert.Equal(2, plan.Steps.Count);
    }

    [Fact]
    public void FallsBackWhenDeltasAreLargerThanFullDownload()
    {
        var manifests = Manifests(Manifest("3", ("2", 60)), Manifest("2", ("1", 60)));
        var plan = UpdatePlanner.Plan(Package, "3", 100, manifests, new HashSet<string> { "1" });
        Assert.False(plan.UsesDeltas);
    }

    [Fact]
    public void FallsBackWhenChainIsBroken()
    {
        // Release 2 has no manifest, so there's no way from 1 to 3.
        var plan = UpdatePlanner.Plan(Package, "3", 100, Manifests(Manifest("3", ("2", 5))), new HashSet<string> { "1" });
        Assert.False(plan.UsesDeltas);
    }
}
