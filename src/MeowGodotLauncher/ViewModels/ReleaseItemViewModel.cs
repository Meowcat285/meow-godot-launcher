using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MeowGodotLauncher.Core.Delta;
using MeowGodotLauncher.Core.GitHub;
using MeowGodotLauncher.Core.Library;
using MeowGodotLauncher.Core.Util;

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
                parts.Add(InstalledComponents is { } ids
                    ? $"export templates ({(ids.Count == 0 ? "none" : string.Join(", ", ids.Select(id => Core.Library.TemplateComponents.Find(id)?.Name ?? id)))})"
                    : "export templates (all platforms)");
            string text = $"Installed: {string.Join(" and ", parts)}";
            var patched = Build.Packages.Values.Select(p => p.PatchedFrom).FirstOrDefault(p => p is not null);
            return patched is null ? text : $"{text} (delta update from {patched})";
        }
    }

    /// <summary>Download size of installing what's missing, filled in asynchronously.</summary>
    [ObservableProperty]
    public partial string PlanText { get; set; } = "";

    /// <summary>Installed export template components, null if all of them are installed.</summary>
    public IReadOnlyList<string>? InstalledComponents => Build?.Packages.GetValueOrDefault(PackageKinds.ExportTemplates)?.Components;

    /// <summary>Platforms to pick when installing templates, from the settings. Null for all.</summary>
    public IReadOnlyList<string>? DefaultTemplateComponents { get; set; }

    /// <summary>Export template platforms of the release, loaded when the release is selected.</summary>
    public ObservableCollection<TemplateComponentItemViewModel> TemplateComponents { get; } = [];

    public bool TemplateComponentsLoaded { get; private set; }

    [ObservableProperty] public partial string TemplateStatusText { get; set; } = "";

    [ObservableProperty] public partial bool HasTemplateChanges { get; set; }

    /// <summary>Which build's templates Godot uses for this version.</summary>
    [ObservableProperty] public partial string SyncedTemplatesText { get; set; } = "";

    [ObservableProperty] public partial bool CanRemoveSyncedTemplates { get; set; }

    public string ApplyTemplatesText => HasTemplates ? "Apply" : "Download selected";

    public IReadOnlyList<string> SelectedTemplateComponents =>
        TemplateComponents.Where(c => c.IsSelected).Select(c => c.Id).ToList();

    public void SetTemplateComponents(IEnumerable<(TemplateComponent Component, long? Size)> components)
    {
        TemplateComponents.Clear();
        foreach (var (component, size) in components)
            TemplateComponents.Add(new TemplateComponentItemViewModel(component, size, UpdateTemplateChanges));
        TemplateComponentsLoaded = true;
        ResetTemplateSelection();
    }

    /// <summary>Selects what's installed, or the default platforms when the templates aren't installed.</summary>
    public void ResetTemplateSelection()
    {
        var installed = HasTemplates ? InstalledComponents : [];
        var selected = HasTemplates ? installed : DefaultTemplateComponents;
        foreach (var item in TemplateComponents)
        {
            item.IsInstalled = installed is null || installed.Contains(item.Id);
            item.IsSelected = selected is null || selected.Contains(item.Id);
        }
        OnPropertyChanged(nameof(ApplyTemplatesText));
        UpdateTemplateChanges();
    }

    private void UpdateTemplateChanges()
    {
        var added = TemplateComponents.Where(c => c.IsSelected && !c.IsInstalled).ToList();
        var removed = TemplateComponents.Where(c => !c.IsSelected && c.IsInstalled).ToList();
        HasTemplateChanges = added.Count > 0 || removed.Count > 0;
        var parts = new List<string>();
        if (added.Count > 0)
            parts.Add($"Downloads {string.Join(", ", added.Select(c => c.Name))} ({FileUtil.FormatSize(added.Sum(c => c.Size))})");
        if (removed.Count > 0)
            parts.Add($"Removes {string.Join(", ", removed.Select(c => c.Name))}");
        TemplateStatusText = parts.Count > 0 ? string.Join(". ", parts) + "." : "";
    }

    partial void OnBuildChanged(InstalledBuild? value)
    {
        OnPropertyChanged(nameof(InstalledComponents));
        if (TemplateComponentsLoaded)
            ResetTemplateSelection();
    }
}
