using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeowGodotLauncher.Core.Delta;
using MeowGodotLauncher.Core.GitHub;
using MeowGodotLauncher.Core.Library;
using MeowGodotLauncher.Core.Settings;
using MeowGodotLauncher.Core.Util;
using Velopack;
using Velopack.Sources;

namespace MeowGodotLauncher.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private static readonly HttpClient Http = GitHubReleaseClient.CreateHttpClient();

    private readonly LauncherSettings _settings = LauncherSettings.Load();
    private BuildLibrary _library = null!;
    private GitHubReleaseClient _client = null!;
    private Installer _installer = null!;
    // All the releases and pre-releases, which deltas can go through. Only those of the selected branch are listed.
    private IReadOnlyList<ReleaseInfo> _releases = [];
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _planning;
    private UpdateManager? _launcherUpdater;
    private UpdateInfo? _launcherUpdate;
    private readonly Stopwatch _progressThrottle = Stopwatch.StartNew();

    public MainViewModel()
    {
        Settings.LoadFrom(_settings);
        CreateServices();
    }

    public ObservableCollection<ReleaseItemViewModel> Releases { get; } = [];

    /// <summary>Releases of the default branch, then the branches that have pre-releases, most recently built first.</summary>
    public ObservableCollection<BranchOption> Branches { get; } = [];

    [ObservableProperty]
    public partial BranchOption? SelectedBranch { get; set; }

    public SettingsViewModel Settings { get; } = new();

    public string RepositoryText => $"github.com/{_client.Repository}";

    public string? HostEditor => PackageKinds.HostEditor;

    public bool IsHostSupported => HostEditor is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial ReleaseItemViewModel? SelectedRelease { get; set; }

    public bool HasSelection => SelectedRelease is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }

    public bool IsIdle => !IsBusy;

    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial double ProgressValue { get; set; }
    [ObservableProperty] public partial bool IsProgressIndeterminate { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    public partial string? UpdateText { get; set; }

    public bool HasUpdate => UpdateText is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLauncherUpdate))]
    public partial string? LauncherUpdateText { get; set; }

    public bool HasLauncherUpdate => LauncherUpdateText is not null;

    private void CreateServices()
    {
        _library = new BuildLibrary(_settings.ResolvedLibraryPath);
        _client = new GitHubReleaseClient(Http, _settings.Owner, _settings.Repository, _settings.GitHubToken, LauncherPaths.Cache);
        _installer = new Installer(_library, _client, Http) { Log = message => StatusText = message };
        // The launcher's own releases, which the Velopack packages update from (see .github/workflows/ci.yml).
        _launcherUpdater = new UpdateManager(new GithubSource("https://github.com/Meowcat285/meow-godot-launcher", _settings.GitHubToken, false));
        OnPropertyChanged(nameof(RepositoryText));
    }

    public async Task InitializeAsync()
    {
        _library.CleanStaging();
        await RefreshAsync();
        await CheckLauncherUpdateAsync();
    }

    private async Task CheckLauncherUpdateAsync()
    {
        // Builds that weren't installed from a Velopack package (from source, or run from the publish folder) don't update.
        if (_launcherUpdater is not { IsInstalled: true } updater)
            return;
        try
        {
            _launcherUpdate = await updater.CheckForUpdatesAsync();
            LauncherUpdateText = _launcherUpdate is { } update ? $"Meow Godot Launcher {update.TargetFullRelease.Version} is available." : null;
        }
        catch (Exception)
        {
            // Not worth bothering about, the next start checks again.
        }
    }

    [RelayCommand]
    private async Task UpdateLauncherAsync()
    {
        if (IsBusy || _launcherUpdater is not { } updater || _launcherUpdate is not { } update)
            return;
        await RunAsync($"Updating the launcher to {update.TargetFullRelease.Version}…", async token =>
        {
            // Reports whole percents, from a background thread.
            var progress = new Progress<int>(percent => ReportProgress(new InstallProgress(
                $"Downloading the launcher {update.TargetFullRelease.Version}… {percent}%", percent / 100.0)));
            await updater.DownloadUpdatesAsync(update, ((IProgress<int>)progress).Report, token);
            updater.ApplyUpdatesAndRestart(update.TargetFullRelease);
        });
    }

    [RelayCommand]
    private void OpenLauncherReleasePage() => Shell.Open("https://github.com/Meowcat285/meow-godot-launcher/releases/latest");

    partial void OnSelectedReleaseChanged(ReleaseItemViewModel? value)
    {
        _ = UpdatePlanTextAsync(value);
        _ = LoadTemplateComponentsAsync(value);
        UpdateSyncedTemplatesText(value);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
            return;
        await RunAsync("Loading releases…", async _ =>
        {
            // As many as possible, since pre-releases of busy branches can push releases out of the first page.
            var list = await _client.GetReleasesAsync(100);
            _releases = list.Releases;
            ReloadBranches();
            ReloadItems();
            StatusText = list.Warning ?? $"{Releases.Count} releases, {Releases.Count(r => r.IsInstalled)} installed.";
        }, cancellable: false);
    }

    private void ReloadBranches()
    {
        Branches.Clear();
        Branches.Add(new BranchOption(null));
        foreach (var branch in _releases.Select(r => r.Branch).OfType<string>().Distinct())
            Branches.Add(new BranchOption(branch));
        // Keep a branch without pre-releases in the list, if it's the selected one.
        if (_settings.Branch is { } selected && Branches.All(b => b.Branch != selected))
            Branches.Add(new BranchOption(selected));
        SelectedBranch = Branches.First(b => b.Branch == _settings.Branch);
    }

    partial void OnSelectedBranchChanged(BranchOption? value)
    {
        if (value is null || value.Branch == _settings.Branch)
            return;
        _settings.Branch = value.Branch;
        try
        {
            _settings.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Couldn't save settings: {e.Message}";
        }
        ReloadItems();
    }

    /// <summary>Rebuilds the release list from <see cref="_releases"/> and the library, keeping the selection.</summary>
    private void ReloadItems()
    {
        string? selected = SelectedRelease?.Tag;
        var builds = _library.List().ToDictionary(b => b.Tag);
        var releases = _releases
            .Where(r => _settings.Branch is null ? !r.Prerelease : r.Branch == _settings.Branch)
            .Take(_settings.MaxReleases)
            .ToList();

        Releases.Clear();
        for (int i = 0; i < releases.Count; i++)
        {
            var item = new ReleaseItemViewModel(releases[i], i == 0) { DefaultTemplateComponents = _settings.TemplateComponents };
            item.Build = builds.GetValueOrDefault(releases[i].Tag);
            Releases.Add(item);
        }
        SelectedRelease = Releases.FirstOrDefault(r => r.Tag == selected) ?? Releases.FirstOrDefault(r => r.IsInstalled) ?? Releases.FirstOrDefault();
        UpdateBanner(builds.Values);
    }

    private void RefreshInstalledState()
    {
        var builds = _library.List().ToDictionary(b => b.Tag);
        foreach (var item in Releases)
            item.Build = builds.GetValueOrDefault(item.Tag);
        UpdateBanner(builds.Values);
        _ = UpdatePlanTextAsync(SelectedRelease);
        UpdateSyncedTemplatesText(SelectedRelease);
    }

    private void UpdateBanner(IEnumerable<InstalledBuild> builds)
    {
        var latest = Releases.FirstOrDefault();
        UpdateText = latest is not null && latest.CanInstallEditor && builds.Any(b => b.HasEditor)
            ? $"Build {latest.Tag} is available."
            : null;
    }

    /// <summary>Packages that installing <paramref name="item"/> would download, with the template platforms to install.</summary>
    private List<(string Package, IReadOnlyList<string>? Components)> MissingPackages(ReleaseItemViewModel item, bool includeTemplates,
        IReadOnlyList<string>? templateComponents)
    {
        var packages = new List<(string, IReadOnlyList<string>?)>();
        if (item.CanInstallEditor && HostEditor is not null)
            packages.Add((HostEditor, null));
        if (includeTemplates && item.CanInstallTemplates)
            packages.Add((PackageKinds.ExportTemplates, templateComponents));
        return packages;
    }

    private List<(string Package, IReadOnlyList<string>? Components)> MissingPackages(ReleaseItemViewModel item) =>
        MissingPackages(item, _settings.InstallExportTemplates, _settings.TemplateComponents);

    private async Task UpdatePlanTextAsync(ReleaseItemViewModel? item)
    {
        _planning?.Cancel();
        if (item is null)
            return;
        var cts = _planning = new CancellationTokenSource();
        var packages = MissingPackages(item);
        if (packages.Count == 0)
        {
            item.PlanText = "";
            return;
        }

        item.PlanText = "Working out the download size…";
        try
        {
            var lines = new List<string>();
            foreach (var (package, components) in packages)
            {
                var plan = await _installer.PlanAsync(item.Release, package, _settings.UseDeltaUpdates, components, cts.Token);
                if (plan is not null)
                    lines.Add(DescribePlan(plan));
            }
            if (!cts.IsCancellationRequested)
                item.PlanText = string.Join("\n", lines);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            item.PlanText = $"Couldn't work out the download size: {e.Message}";
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string DescribePlan(InstallPlan plan)
    {
        string what = plan.PackageId != PackageKinds.ExportTemplates ? "Editor"
            : plan.Components is null ? "Export templates" : $"Export templates for {DescribeComponents(plan.Components)}";
        if (!plan.UsesDeltas)
            return $"{what}: {FileUtil.FormatSize(plan.FullSize)} download.";
        int saved = (int)Math.Round(100.0 * (1 - (double)plan.DownloadSize / Math.Max(1, plan.FullSize)));
        return $"{what}: {FileUtil.FormatSize(plan.DownloadSize)} delta update from {plan.BaseTag} (full download is {FileUtil.FormatSize(plan.FullSize)}, {saved}% saved).";
    }

    private static string DescribeComponents(IEnumerable<string> ids) =>
        string.Join(", ", ids.Select(id => TemplateComponents.Find(id)?.Name ?? id));

    [RelayCommand]
    private Task InstallAsync() =>
        SelectedRelease is { } item ? InstallPackagesAsync(item, MissingPackages(item)) : Task.CompletedTask;

    [RelayCommand]
    private Task InstallTemplatesAsync() =>
        SelectedRelease is { CanInstallTemplates: true } item
            ? InstallPackagesAsync(item, [(PackageKinds.ExportTemplates, _settings.TemplateComponents)])
            : Task.CompletedTask;

    [RelayCommand]
    private Task UpdateToLatestAsync()
    {
        var latest = Releases.FirstOrDefault();
        if (latest is null)
            return Task.CompletedTask;
        SelectedRelease = latest;
        // Updates keep the template platforms of the newest installed templates, so deltas from them can be used.
        var templates = _library.List()
            .Where(b => b.HasTemplates)
            .Select(b => b.Packages[PackageKinds.ExportTemplates])
            .OrderByDescending(p => p.InstalledAt)
            .FirstOrDefault();
        bool includeTemplates = _settings.InstallExportTemplates || templates is not null;
        return InstallPackagesAsync(latest, MissingPackages(latest, includeTemplates, templates is null ? _settings.TemplateComponents : templates.Components));
    }

    private async Task InstallPackagesAsync(ReleaseItemViewModel item, List<(string Package, IReadOnlyList<string>? Components)> packages)
    {
        if (IsBusy || packages.Count == 0)
            return;
        await RunAsync($"Installing {item.Tag}…", async token =>
        {
            var replaced = new HashSet<(string Tag, string Package)>();
            foreach (var (package, components) in packages)
            {
                var plan = await _installer.PlanAsync(item.Release, package, _settings.UseDeltaUpdates, components, token)
                    ?? throw new InvalidOperationException($"Release {item.Tag} has no {package} asset.");
                var progress = new Progress<InstallProgress>(ReportProgress);
                await _installer.InstallAsync(item.Release, plan, progress, token);
                if (plan.BaseTag is not null)
                    replaced.Add((plan.BaseTag, package));
            }

            string? warning = null;
            if (_settings.RemoveOldBuildsAfterUpdate)
            {
                // Only the packages that were updated, the base build may have others still in use.
                foreach (var (tag, package) in replaced)
                {
                    try
                    {
                        if (_library.Find(tag) is not { } old)
                            continue;
                        _library.RemovePackage(old, package);
                        if (old.Packages.Count == 0)
                            _library.Remove(old);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        warning = $" Couldn't remove the old build {tag}, is it still running? {e.Message}";
                    }
                }
            }
            RefreshInstalledState();
            StatusText = $"Installed {item.Tag}.{warning}";
        });
    }

    private void ReportProgress(InstallProgress progress)
    {
        // Downloads report very often, keep the UI responsive.
        if (_progressThrottle.ElapsedMilliseconds < 100 && progress.Fraction is > 0 and < 1)
            return;
        _progressThrottle.Restart();
        StatusText = progress.Message;
        IsProgressIndeterminate = progress.Fraction is null;
        ProgressValue = (progress.Fraction ?? 0) * 100;
    }

    [RelayCommand]
    private async Task LaunchAsync()
    {
        if (SelectedRelease?.Build is not { } build)
            return;
        try
        {
            if (_settings.SyncTemplatesOnLaunch && build.HasTemplates)
            {
                StatusText = "Copying export templates…";
                bool foreign = EditorLauncher.SyncedTemplatesTag(build) == "";
                var dest = await Task.Run(() => EditorLauncher.SyncExportTemplates(build));
                if (dest is not null)
                    StatusText = $"Export templates of {build.Tag} copied to {dest}{ReplacedNote(foreign)}.";
            }
            using var process = EditorLauncher.Launch(build, _settings.LaunchArguments);
            StatusText = $"Launched {build.Tag}.";
        }
        catch (Exception e)
        {
            StatusText = $"Couldn't launch {build.Tag}: {e.Message}";
        }
    }

    /// <summary>Points out that templates the launcher didn't install, e.g. from the editor, were replaced.</summary>
    private static string ReplacedNote(bool foreign) =>
        foreign ? ", replacing export templates that weren't installed by the launcher" : "";

    [RelayCommand]
    private async Task SyncTemplatesAsync()
    {
        if (SelectedRelease?.Build is not { HasTemplates: true } build)
            return;
        try
        {
            bool foreign = EditorLauncher.SyncedTemplatesTag(build) == "";
            var dest = await Task.Run(() => EditorLauncher.SyncExportTemplates(build));
            StatusText = dest is null ? "Export templates are already installed in Godot." : $"Export templates copied to {dest}{ReplacedNote(foreign)}.";
            UpdateSyncedTemplatesText(SelectedRelease);
        }
        catch (Exception e)
        {
            StatusText = $"Couldn't copy the export templates: {e.Message}";
        }
    }

    private async Task LoadTemplateComponentsAsync(ReleaseItemViewModel? item)
    {
        if (item is null || item.TemplateComponentsLoaded || !item.HasTemplatesAsset)
            return;
        item.TemplateStatusText = "Reading the list of export templates…";
        try
        {
            var components = await _installer.GetTemplateComponentsAsync(item.Release);
            item.SetTemplateComponents(components.Select(c => (c.Component, (long?)c.Size)));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException)
        {
            // Without the file list, offer every platform. Installing then downloads the whole archive if needed.
            item.SetTemplateComponents(TemplateComponents.All.Select(c => (c, (long?)null)));
            item.TemplateStatusText = $"Couldn't read the export templates of {item.Tag}, sizes are unknown: {e.Message}";
        }
    }

    private void UpdateSyncedTemplatesText(ReleaseItemViewModel? item)
    {
        if (item is null)
            return;
        item.CanRemoveSyncedTemplates = false;
        if (item.Build is not { HasTemplates: true } build)
        {
            item.SyncedTemplatesText = "";
            return;
        }
        try
        {
            string? tag = EditorLauncher.SyncedTemplatesTag(build);
            item.CanRemoveSyncedTemplates = !string.IsNullOrEmpty(tag);
            item.SyncedTemplatesText = tag switch
            {
                null => "Godot has no export templates for this version yet.",
                "" => "Godot has export templates for this version that the launcher didn't install.",
                _ when tag == build.Tag => "Godot uses these export templates.",
                _ => $"Godot uses the export templates of {tag} for this version.",
            };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            item.SyncedTemplatesText = $"Couldn't check Godot's export templates: {e.Message}";
        }
    }

    /// <summary>Downloads the checked template platforms of the selected build and removes the unchecked ones.</summary>
    [RelayCommand]
    private async Task ApplyTemplatesAsync()
    {
        if (IsBusy || SelectedRelease is not { } item)
            return;
        var components = item.SelectedTemplateComponents;
        if (!item.HasTemplates)
        {
            if (components.Count > 0)
                await InstallPackagesAsync(item, [(PackageKinds.ExportTemplates, components)]);
            return;
        }

        await RunAsync($"Updating the export templates of {item.Tag}…", async token =>
        {
            var build = item.Build!;
            bool synced = EditorLauncher.SyncedTemplatesTag(build) == build.Tag;
            if (components.Count == 0)
            {
                if (synced)
                    EditorLauncher.RemoveSyncedTemplates(build);
                await Task.Run(() => _library.RemovePackage(build, PackageKinds.ExportTemplates), token);
            }
            else
            {
                build = await _installer.SetTemplateComponentsAsync(item.Release, components, _settings.UseDeltaUpdates,
                    new Progress<InstallProgress>(ReportProgress), token);
                // Keep Godot's copy in step with the build.
                if (synced)
                    await Task.Run(() => EditorLauncher.SyncExportTemplates(build), token);
            }
            RefreshInstalledState();
            StatusText = components.Count == 0
                ? $"Removed the export templates of {item.Tag}."
                : $"Export templates of {item.Tag}: {DescribeComponents(components)}.";
        });
    }

    [RelayCommand]
    private void ResetTemplateSelection() => SelectedRelease?.ResetTemplateSelection();

    [RelayCommand]
    private void SelectAllTemplates(string? select)
    {
        foreach (var component in SelectedRelease?.TemplateComponents ?? [])
            component.IsSelected = select == "true";
    }

    [RelayCommand]
    private void RemoveSyncedTemplates()
    {
        if (SelectedRelease?.Build is not { HasTemplates: true } build)
            return;
        try
        {
            StatusText = EditorLauncher.RemoveSyncedTemplates(build)
                ? "Removed the export templates from Godot's folder."
                : "Those export templates weren't installed by the launcher, so they were left alone.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText = $"Couldn't remove the export templates: {e.Message}";
        }
        UpdateSyncedTemplatesText(SelectedRelease);
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (IsBusy || SelectedRelease?.Build is not { } build)
            return;
        await RunAsync($"Removing {build.Tag}…", async _ =>
        {
            await Task.Run(() => _library.Remove(build));
            RefreshInstalledState();
            StatusText = $"Removed {build.Tag}.";
        }, cancellable: false);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (SelectedRelease?.Build is { } build)
            Shell.Open(build.Directory);
        else
            Shell.Open(_library.Root);
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        if (SelectedRelease?.Release.HtmlUrl is { } url)
            Shell.Open(url);
    }

    [RelayCommand]
    private void Cancel()
    {
        _operation?.Cancel();
        _planning?.Cancel();
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (IsBusy)
        {
            StatusText = "Wait for the current operation to finish before changing settings.";
            return;
        }
        Settings.ApplyTo(_settings);
        try
        {
            _settings.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Couldn't save settings: {e.Message}";
            return;
        }
        Settings.LoadFrom(_settings);
        CreateServices();
        await RefreshAsync();
    }

    [RelayCommand]
    private void ResetSettings() => Settings.LoadFrom(_settings);

    /// <summary>Runs an operation with the busy state, progress bar and error reporting.</summary>
    private async Task RunAsync(string message, Func<CancellationToken, Task> action, bool cancellable = true)
    {
        IsBusy = true;
        StatusText = message;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        using var cts = cancellable ? new CancellationTokenSource() : null;
        _operation = cts;
        try
        {
            await action(cts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (Exception e)
        {
            StatusText = $"Error: {e.Message}";
        }
        finally
        {
            _operation = null;
            IsBusy = false;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
        }
    }
}

/// <summary>An entry of the branch picker: a branch with pre-releases, or the releases of the default branch when null.</summary>
public sealed record BranchOption(string? Branch)
{
    public override string ToString() => Branch ?? "Releases";
}
