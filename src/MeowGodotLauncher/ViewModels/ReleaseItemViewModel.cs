using CommunityToolkit.Mvvm.ComponentModel;
using MeowGodotLauncher.Core.Delta;
using MeowGodotLauncher.Core.GitHub;
using MeowGodotLauncher.Core.Library;

namespace MeowGodotLauncher.ViewModels;

public partial class ReleaseItemViewModel(ReleaseInfo release, bool isLatest) : ViewModelBase
{
    public ReleaseInfo Release { get; } = release;

    public string Tag => Release.Tag;

    public bool IsLatest { get; } = isLatest;

    public string PublishedText => Release.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";

    public string ShortCommit => Release.TargetCommitish is { Length: >= 7 } c ? c[..7] : Release.TargetCommitish ?? "";

    public string Notes => string.IsNullOrWhiteSpace(Release.Body) ? "No release notes." : Release.Body.Trim();

    public bool SupportsDeltas => Release.FindAsset(ReleaseManifest.FileName) is not null;

    public bool HasHostEditorAsset =>
        PackageKinds.HostEditor is { } id && Release.Assets.Any(a => PackageKinds.MatchesAsset(id, a.Name));

    public bool HasTemplatesAsset => Release.Assets.Any(a => PackageKinds.MatchesAsset(PackageKinds.ExportTemplates, a.Name));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalled), nameof(HasEditor), nameof(HasTemplates), nameof(CanInstallEditor),
        nameof(CanInstallTemplates), nameof(InstalledText), nameof(ShowNoDeltaNote))]
    public partial InstalledBuild? Build { get; set; }

    public bool IsInstalled => Build is not null && Build.Packages.Count > 0;

    public bool HasEditor => Build?.HasEditor == true;

    public bool HasTemplates => Build?.HasTemplates == true;

    public bool CanInstallEditor => !HasEditor && HasHostEditorAsset;

    public bool CanInstallTemplates => !HasTemplates && HasTemplatesAsset;

    /// <summary>Whether to point out that installing this release can't use deltas.</summary>
    public bool ShowNoDeltaNote => !SupportsDeltas && (CanInstallEditor || CanInstallTemplates);

    public string InstalledText
    {
        get
        {
            if (Build is null || Build.Packages.Count == 0)
                return "Not installed";
            var parts = new List<string>();
            if (HasEditor)
                parts.Add("editor");
            if (HasTemplates)
                parts.Add("export templates");
            string text = $"Installed: {string.Join(" and ", parts)}";
            var patched = Build.Packages.Values.Select(p => p.PatchedFrom).FirstOrDefault(p => p is not null);
            return patched is null ? text : $"{text} (delta update from {patched})";
        }
    }

    /// <summary>Download size of installing what's missing, filled in asynchronously.</summary>
    [ObservableProperty]
    public partial string PlanText { get; set; } = "";
}
