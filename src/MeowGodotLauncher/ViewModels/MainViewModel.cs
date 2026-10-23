using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeowGodotLauncher.Core.Delta;
using MeowGodotLauncher.Core.GitHub;
using MeowGodotLauncher.Core.Library;
using MeowGodotLauncher.Core.Settings;
using MeowGodotLauncher.Core.Util;

namespace MeowGodotLauncher.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private static readonly HttpClient Http = GitHubReleaseClient.CreateHttpClient();

    private readonly LauncherSettings _settings = LauncherSettings.Load();
    private BuildLibrary _library = null!;
    private GitHubReleaseClient _client = null!;
    private Installer _installer = null!;
    private IReadOnlyList<ReleaseInfo> _releases = [];
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _planning;
    private readonly Stopwatch _progressThrottle = Stopwatch.StartNew();

    public MainViewModel()
    {
        Settings.LoadFrom(_settings);
        CreateServices();
    }

    public ObservableCollection<ReleaseItemViewModel> Releases { get; } = [];

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

    private void CreateServices()
    {
        _library = new BuildLibrary(_settings.ResolvedLibraryPath);
        _client = new GitHubReleaseClient(Http, _settings.Owner, _settings.Repository, _settings.GitHubToken, LauncherPaths.Cache);
        _installer = new Installer(_library, _client) { Log = message => StatusText = message };
        OnPropertyChanged(nameof(RepositoryText));
    }

    public async Task InitializeAsync()
    {
        _library.CleanStaging();
        await RefreshAsync();
    }

    partial void OnSelectedReleaseChanged(ReleaseItemViewModel? value) => _ = UpdatePlanTextAsync(value);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
            return;
        await RunAsync("Loading releases…", async _ =>
        {
            var list = await _client.GetReleasesAsync(_settings.MaxReleases);
            _releases = list.Releases;
            ReloadItems();
            StatusText = list.Warning ?? $"{_releases.Count} releases, {Releases.Count(r => r.IsInstalled)} installed.";
        }, cancellable: false);
    }

    /// <summary>Rebuilds the release list from <see cref="_releases"/> and the library, keeping the selection.</summary>
    private void ReloadItems()
    {
        string? selected = SelectedRelease?.Tag;
        var builds = _library.List().ToDictionary(b => b.Tag);

        Releases.Clear();
        for (int i = 0; i < _releases.Count; i++)
        {
            var item = new ReleaseItemViewModel(_releases[i], i == 0);
            item.Build = builds.GetValueOrDefault(_releases[i].Tag);
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
    }

    private void UpdateBanner(IEnumerable<InstalledBuild> builds)
    {
        var latest = Releases.FirstOrDefault();
        UpdateText = latest is not null && latest.CanInstallEditor && builds.Any(b => b.HasEditor)
            ? $"Build {latest.Tag} is available."
            : null;
    }

    /// <summary>Packages that installing <paramref name="item"/> would download.</summary>
    private List<string> MissingPackages(ReleaseItemViewModel item, bool includeTemplates)
    {
        var packages = new List<string>();
        if (item.CanInstallEditor && HostEditor is not null)
            packages.Add(HostEditor);
        if (includeTemplates && item.CanInstallTemplates)
            packages.Add(PackageKinds.ExportTemplates);
        return packages;
    }

    private async Task UpdatePlanTextAsync(ReleaseItemViewModel? item)
    {
        _planning?.Cancel();
        if (item is null)
            return;
        var cts = _planning = new CancellationTokenSource();
        var packages = MissingPackages(item, _settings.InstallExportTemplates);
        if (packages.Count == 0)
        {
            item.PlanText = "";
            return;
        }

        item.PlanText = "Working out the download size…";
        try
        {
            var lines = new List<string>();
            foreach (var package in packages)
            {
                var plan = await _installer.PlanAsync(item.Release, package, _releases, _settings.UseDeltaUpdates, cts.Token);
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
        string what = plan.PackageId == PackageKinds.ExportTemplates ? "Export templates" : "Editor";
        if (!plan.UsesDeltas)
            return $"{what}: {FileUtil.FormatSize(plan.FullSize)} download.";
        string chain = plan.Steps.Count == 1 ? $"from {plan.BaseTag}" : $"from {plan.BaseTag} in {plan.Steps.Count} steps";
        int saved = (int)Math.Round(100.0 * (1 - (double)plan.DownloadSize / Math.Max(1, plan.FullSize)));
        return $"{what}: {FileUtil.FormatSize(plan.DownloadSize)} delta update {chain} (full download is {FileUtil.FormatSize(plan.FullSize)}, {saved}% saved).";
    }

    [RelayCommand]
    private Task InstallAsync() =>
        SelectedRelease is { } item ? InstallPackagesAsync(item, MissingPackages(item, _settings.InstallExportTemplates)) : Task.CompletedTask;

    [RelayCommand]
    private Task InstallTemplatesAsync() =>
        SelectedRelease is { CanInstallTemplates: true } item ? InstallPackagesAsync(item, [PackageKinds.ExportTemplates]) : Task.CompletedTask;

    [RelayCommand]
    private Task UpdateToLatestAsync()
    {
        var latest = Releases.FirstOrDefault();
        if (latest is null)
            return Task.CompletedTask;
        SelectedRelease = latest;
        return InstallPackagesAsync(latest, MissingPackages(latest, _settings.InstallExportTemplates));
    }

    private async Task InstallPackagesAsync(ReleaseItemViewModel item, List<string> packages)
    {
        if (IsBusy || packages.Count == 0)
            return;
        await RunAsync($"Installing {item.Tag}…", async token =>
        {
            var replacedBases = new HashSet<string>();
            foreach (var package in packages)
            {
                var plan = await _installer.PlanAsync(item.Release, package, _releases, _settings.UseDeltaUpdates, token)
                    ?? throw new InvalidOperationException($"Release {item.Tag} has no {package} asset.");
                var progress = new Progress<InstallProgress>(ReportProgress);
                await _installer.InstallAsync(item.Release, plan, _releases, progress, token);
                if (plan.BaseTag is not null)
                    replacedBases.Add(plan.BaseTag);
            }

            if (_settings.RemoveOldBuildsAfterUpdate)
            {
                foreach (var tag in replacedBases)
                    if (_library.Find(tag) is { } old)
                        _library.Remove(old);
            }
            RefreshInstalledState();
            StatusText = $"Installed {item.Tag}.";
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
                var dest = await Task.Run(() => EditorLauncher.SyncExportTemplates(build));
                if (dest is not null)
                    StatusText = $"Export templates of {build.Tag} copied to {dest}.";
            }
            using var process = EditorLauncher.Launch(build, _settings.LaunchArguments);
            StatusText = $"Launched {build.Tag}.";
        }
        catch (Exception e)
        {
            StatusText = $"Couldn't launch {build.Tag}: {e.Message}";
        }
    }

    [RelayCommand]
    private async Task SyncTemplatesAsync()
    {
        if (SelectedRelease?.Build is not { HasTemplates: true } build)
            return;
        try
        {
            var dest = await Task.Run(() => EditorLauncher.SyncExportTemplates(build));
            StatusText = dest is null ? "Export templates are already installed in Godot." : $"Export templates copied to {dest}.";
        }
        catch (Exception e)
        {
            StatusText = $"Couldn't copy the export templates: {e.Message}";
        }
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
