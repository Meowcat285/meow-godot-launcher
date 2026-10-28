using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using MeowGodotLauncher.Core.GitHub;
using MeowGodotLauncher.Core.Util;

namespace MeowGodotLauncher.Core.SelfUpdate;

/// <summary>A newer release of the launcher, with the package for this platform.</summary>
public sealed record LauncherUpdate(ReleaseInfo Release, Version Version, ReleaseAsset Asset);

/// <summary>
/// Updates the launcher itself from the releases of its own repository. The new package is downloaded and extracted
/// next to the cache, then swapped in: the files being replaced are renamed out of the way first, which works for a
/// running executable on every platform (Windows refuses to overwrite or delete it, but allows a rename). The renamed
/// files are deleted on the next start, by <see cref="CleanUp"/>.
/// </summary>
public sealed class LauncherUpdater
{
    public const string Owner = "Meowcat285";
    public const string Repository = "meow-godot-launcher";

    /// <summary>The name of the app bundle in the macOS packages.</summary>
    public const string MacAppName = "Meow Godot Launcher.app";

    /// <summary>Suffix of the files replaced by an update, deleted on the next start.</summary>
    internal const string OldSuffix = ".meow-update-old";

    private readonly GitHubReleaseClient _client;
    private readonly string _workDir;

    /// <param name="client">A client for <see cref="Owner"/>/<see cref="Repository"/>.</param>
    /// <param name="installDir">
    /// Where the package is installed: the folder holding the executable and its libraries on Windows and Linux, or
    /// the app bundle on macOS.
    /// </param>
    /// <param name="workDir">Folder to download and extract updates in.</param>
    public LauncherUpdater(GitHubReleaseClient client, string installDir, string workDir, Version currentVersion, string? packageName)
    {
        _client = client;
        InstallDir = installDir;
        _workDir = workDir;
        CurrentVersion = currentVersion;
        PackageName = packageName;
    }

    public string InstallDir { get; }

    public Version CurrentVersion { get; }

    /// <summary>The release asset for this platform, or null on platforms without launcher releases.</summary>
    public string? PackageName { get; }

    private bool IsBundle => InstallDir.EndsWith(".app", StringComparison.Ordinal);

    /// <summary>The package published for the platform the launcher runs on (see .github/workflows/ci.yml).</summary>
    public static string? HostPackageName()
    {
        string? arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        if (OperatingSystem.IsWindows() && arch == "x64")
            return "MeowGodotLauncher-win-x64.zip";
        if (OperatingSystem.IsLinux() && arch == "x64")
            return "MeowGodotLauncher-linux-x64.tar.gz";
        if (OperatingSystem.IsMacOS() && arch is not null)
            return $"MeowGodotLauncher-osx-{arch}.zip";
        return null;
    }

    /// <summary>Where the running launcher is installed, see the constructor's <c>installDir</c>.</summary>
    public static string? HostInstallDir()
    {
        if (Environment.ProcessPath is not { } exe)
            return null;
        string dir = Path.GetDirectoryName(exe)!;
        if (OperatingSystem.IsMacOS())
        {
            // <bundle>.app/Contents/MacOS/<exe>
            var contents = Directory.GetParent(dir);
            var bundle = contents?.Parent;
            return Path.GetFileName(dir) == "MacOS" && contents!.Name == "Contents" && bundle!.Name.EndsWith(".app", StringComparison.Ordinal)
                ? bundle.FullName
                : null;
        }
        return dir;
    }

    /// <summary>Parses a release tag like v1.2.3. Pre-releases (v1.2.3-beta) and other tags give null.</summary>
    public static Version? ParseTag(string tag) =>
        tag.StartsWith('v') && tag.Count(c => c == '.') == 2 && Version.TryParse(tag[1..], out var version) ? version : null;

    /// <summary>Finds the newest release that is newer than the running launcher and has a package for this platform.</summary>
    public async Task<LauncherUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (PackageName is null)
            return null;
        var list = await _client.GetReleasesAsync(20, cancellationToken);
        return FindUpdate(list.Releases, CurrentVersion, PackageName);
    }

    internal static LauncherUpdate? FindUpdate(IEnumerable<ReleaseInfo> releases, Version current, string packageName) =>
        releases
            .Where(r => !r.Prerelease)
            .Select(r => (Release: r, Version: ParseTag(r.Tag), Asset: r.FindAsset(packageName)))
            .Where(r => r.Version is not null && r.Version > current && r.Asset is not null)
            .OrderByDescending(r => r.Version)
            .Select(r => new LauncherUpdate(r.Release, r.Version!, r.Asset!))
            .FirstOrDefault();

    /// <summary>Downloads and installs an update. The new version runs once the launcher is restarted.</summary>
    public async Task InstallAsync(LauncherUpdate update, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        FileUtil.DeleteDirectory(_workDir);
        string archive = Path.Combine(_workDir, update.Asset.Name);
        // GitHub publishes a SHA-256 digest for each asset (as "sha256:<hex>"), when it has one.
        string? sha256 = update.Asset.Digest is { } digest && digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest[7..] : null;
        await _client.DownloadAsync(update.Asset.DownloadUrl, archive, update.Asset.Size, sha256, progress, cancellationToken);

        string extracted = Path.Combine(_workDir, "package");
        await Task.Run(() => Extract(archive, extracted), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // Past this point the update isn't cancelled, so it's never left half done.
        await Task.Run(() => Swap(extracted));
        FileUtil.DeleteDirectory(_workDir);
    }

    internal static void Extract(string archive, string destination)
    {
        Directory.CreateDirectory(destination);
        // All of them keep the Unix permissions of the files, which the executable needs.
        if (OperatingSystem.IsMacOS())
        {
            // The macOS packages are made with ditto, which can store extended attributes as extra "._" files in the
            // zip. Extracting them with anything else would add those files to the signed bundle and break it.
            using var ditto = Process.Start(new ProcessStartInfo("ditto") { ArgumentList = { "-x", "-k", archive, destination } })
                ?? throw new IOException("Couldn't start ditto.");
            ditto.WaitForExit();
            if (ditto.ExitCode != 0)
                throw new IOException($"Couldn't extract {Path.GetFileName(archive)} (ditto exited with {ditto.ExitCode}).");
        }
        else if (archive.EndsWith(".tar.gz", StringComparison.Ordinal))
        {
            using var gzip = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: false);
        }
        else
        {
            ZipFile.ExtractToDirectory(archive, destination);
        }
    }

    /// <summary>Moves the extracted package into <see cref="InstallDir"/>, undoing the moves already done if one fails.</summary>
    internal void Swap(string extracted)
    {
        // The macOS packages hold the app bundle, replaced as a whole. The others hold the files of the install folder,
        // replaced one by one, so anything else in the folder is left alone.
        List<(string Source, string Target)> moves = IsBundle
            ? [(Path.Combine(extracted, MacAppName), InstallDir)]
            : Directory.EnumerateFiles(extracted).Select(f => (f, Path.Combine(InstallDir, Path.GetFileName(f)))).ToList();
        if (IsBundle && !Directory.Exists(moves[0].Source))
            throw new InvalidDataException($"The update has no {MacAppName}.");
        if (moves.Count == 0)
            throw new InvalidDataException("The update is empty.");

        var done = new List<(string Source, string Target, bool HadOld)>();
        try
        {
            foreach (var (source, target) in moves)
            {
                string old = target + OldSuffix;
                bool hadOld = Path.Exists(target);
                if (hadOld)
                {
                    DeletePath(old);
                    MovePath(target, old);
                }
                done.Add((source, target, hadOld));
                MovePath(source, target);
            }
        }
        catch
        {
            for (int i = done.Count - 1; i >= 0; i--)
            {
                var (source, target, hadOld) = done[i];
                try
                {
                    if (Path.Exists(target))
                        MovePath(target, source);
                    if (hadOld)
                        MovePath(target + OldSuffix, target);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Keep undoing the others, the original error is the one reported.
                }
            }
            throw;
        }
    }

    /// <summary>Deletes what an earlier update replaced. Files still in use (an old process still exiting) are left for next time.</summary>
    public static void CleanUp(string installDir)
    {
        try
        {
            if (installDir.EndsWith(".app", StringComparison.Ordinal))
            {
                DeletePath(installDir + OldSuffix);
                return;
            }
            foreach (var path in Directory.EnumerateFiles(installDir, "*" + OldSuffix))
                File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Starts the installed launcher, normally right before the running one exits.</summary>
    public void Restart()
    {
        var start = IsBundle
            // A new instance even though this one is still running.
            ? new ProcessStartInfo("open") { ArgumentList = { "-n", InstallDir } }
            : new ProcessStartInfo(Path.Combine(InstallDir, Path.GetFileName(Environment.ProcessPath!)));
        start.UseShellExecute = false;
        Process.Start(start)?.Dispose();
    }

    private static void MovePath(string source, string target)
    {
        if (Directory.Exists(source))
            Directory.Move(source, target);
        else
            File.Move(source, target);
    }

    private static void DeletePath(string path)
    {
        if (Directory.Exists(path))
            FileUtil.DeleteDirectory(path);
        else if (File.Exists(path))
            File.Delete(path);
    }
}
