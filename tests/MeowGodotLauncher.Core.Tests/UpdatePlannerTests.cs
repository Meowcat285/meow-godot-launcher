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

    [Fact]
    public void UsesFullDownloadWithoutInstalledBuilds()
    {
        var plan = UpdatePlanner.Plan(Package, "3", 100, Manifest("3", ("2", 5)), new HashSet<string>());
        Assert.False(plan.UsesDeltas);
        Assert.Equal(100, plan.DownloadSize);
    }

    [Fact]
    public void UsesCheapestDeltaFromInstalledBuild()
    {
        var plan = UpdatePlanner.Plan(Package, "3", 100, Manifest("3", ("2", 5), ("1", 8)), new HashSet<string> { "1", "2" });
        Assert.Equal("2", plan.BaseTag);
        Assert.Equal("3", plan.Step!.To);
        Assert.Equal(5, plan.DownloadSize);
    }

    [Fact]
    public void UsesPartialCost()
    {
        var plan = UpdatePlanner.Plan(Package, "3", 100, Manifest("3", ("2", 5), ("1", 8)), new HashSet<string> { "1", "2" },
            d => d.From == "1" ? 3 : d.Size);
        Assert.Equal("1", plan.BaseTag);
        Assert.Equal(3, plan.DownloadSize);
    }

    [Fact]
    public void FallsBackWhenDeltaIsLargerThanFullDownload()
    {
        var plan = UpdatePlanner.Plan(Package, "3", 100, Manifest("3", ("2", 100)), new HashSet<string> { "2" });
        Assert.False(plan.UsesDeltas);
    }

    [Fact]
    public void DoesNotChainDeltas()
    {
        // Only 2 has a delta to 3, and 1 is installed: that needs a full download.
        var plan = UpdatePlanner.Plan(Package, "3", 100, Manifest("3", ("2", 5)), new HashSet<string> { "1" });
        Assert.False(plan.UsesDeltas);
    }

    [Fact]
    public void FallsBackWithoutManifest()
    {
        var plan = UpdatePlanner.Plan(Package, "3", 100, null, new HashSet<string> { "2" });
        Assert.False(plan.UsesDeltas);
    }
}
