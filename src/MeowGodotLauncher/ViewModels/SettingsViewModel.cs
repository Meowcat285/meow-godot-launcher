using CommunityToolkit.Mvvm.ComponentModel;
using MeowGodotLauncher.Core.Library;
using MeowGodotLauncher.Core.Settings;

namespace MeowGodotLauncher.ViewModels;

/// <summary>Editable copy of the settings, applied with <see cref="ApplyTo"/>.</summary>
public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty] public partial string Owner { get; set; } = "";
    [ObservableProperty] public partial string Repository { get; set; } = "";
    [ObservableProperty] public partial string GitHubToken { get; set; } = "";
    [ObservableProperty] public partial string LibraryPath { get; set; } = "";
    [ObservableProperty] public partial decimal MaxReleases { get; set; }
    [ObservableProperty] public partial bool UseDeltaUpdates { get; set; }
    [ObservableProperty] public partial bool InstallExportTemplates { get; set; }
    [ObservableProperty] public partial bool SyncTemplatesOnLaunch { get; set; }
    [ObservableProperty] public partial bool RemoveOldBuildsAfterUpdate { get; set; }
    [ObservableProperty] public partial string LaunchArguments { get; set; } = "";

    /// <summary>Export template platforms downloaded by default.</summary>
    public IReadOnlyList<TemplateComponentItemViewModel> TemplateComponents { get; } =
        Core.Library.TemplateComponents.All.Select(c => new TemplateComponentItemViewModel(c, null)).ToList();

    public string DefaultLibraryPath => LauncherPaths.DefaultLibrary;

    public string GodotTemplatesPath => LauncherPaths.GodotExportTemplates;

    public void LoadFrom(LauncherSettings settings)
    {
        Owner = settings.Owner;
        Repository = settings.Repository;
        GitHubToken = settings.GitHubToken ?? "";
        LibraryPath = settings.LibraryPath ?? "";
        MaxReleases = settings.MaxReleases;
        UseDeltaUpdates = settings.UseDeltaUpdates;
        InstallExportTemplates = settings.InstallExportTemplates;
        foreach (var item in TemplateComponents)
            item.IsSelected = settings.TemplateComponents is not { } ids || ids.Contains(item.Id);
        SyncTemplatesOnLaunch = settings.SyncTemplatesOnLaunch;
        RemoveOldBuildsAfterUpdate = settings.RemoveOldBuildsAfterUpdate;
        LaunchArguments = settings.LaunchArguments;
    }

    public void ApplyTo(LauncherSettings settings)
    {
        settings.Owner = string.IsNullOrWhiteSpace(Owner) ? new LauncherSettings().Owner : Owner.Trim();
        settings.Repository = string.IsNullOrWhiteSpace(Repository) ? new LauncherSettings().Repository : Repository.Trim();
        settings.GitHubToken = string.IsNullOrWhiteSpace(GitHubToken) ? null : GitHubToken.Trim();
        settings.LibraryPath = string.IsNullOrWhiteSpace(LibraryPath) ? null : LibraryPath.Trim();
        settings.MaxReleases = (int)Math.Clamp(MaxReleases, 1, 100);
        settings.UseDeltaUpdates = UseDeltaUpdates;
        settings.InstallExportTemplates = InstallExportTemplates;
        var selected = TemplateComponents.Where(c => c.IsSelected).Select(c => c.Id).ToList();
        if (selected.Count == TemplateComponents.Count)
            settings.TemplateComponents = null;
        else if (selected.Count > 0)
            settings.TemplateComponents = selected;
        settings.SyncTemplatesOnLaunch = SyncTemplatesOnLaunch;
        settings.RemoveOldBuildsAfterUpdate = RemoveOldBuildsAfterUpdate;
        settings.LaunchArguments = LaunchArguments;
    }
}
