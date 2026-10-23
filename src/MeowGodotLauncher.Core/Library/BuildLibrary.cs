using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeowGodotLauncher.Core.Util;

namespace MeowGodotLauncher.Core.Library;

/// <summary>Stored as `build.json` in each build's folder.</summary>
public sealed class InstalledBuild
{
    public const string FileName = "build.json";

    public string Tag { get; set; } = "";
    public string? Commit { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>Installed packages, by id.</summary>
    public Dictionary<string, InstalledPackage> Packages { get; set; } = [];

    [JsonIgnore]
    public string Directory { get; set; } = "";

    public string PackageDirectory(string packageId) => Path.Combine(Directory, packageId);

    public string? EditorExecutable
    {
        get
        {
            foreach (var (id, package) in Packages)
                if (id != PackageKinds.ExportTemplates && package.Executable is not null)
                    return Path.Combine(PackageDirectory(id), package.Executable.Replace('/', Path.DirectorySeparatorChar));
            return null;
        }
    }

    public bool HasEditor => Packages.Keys.Any(k => k != PackageKinds.ExportTemplates);
    public bool HasTemplates => Packages.ContainsKey(PackageKinds.ExportTemplates);
}

public sealed class InstalledPackage
{
    public DateTimeOffset InstalledAt { get; set; }

    /// <summary>For editors, the path of the executable relative to the package folder.</summary>
    public string? Executable { get; set; }

    /// <summary>The release the package was patched from, if it was installed with deltas.</summary>
    public string? PatchedFrom { get; set; }

    /// <summary>For export templates, the installed <see cref="TemplateComponents"/>. Null means all of them.</summary>
    public List<string>? Components { get; set; }
}

/// <summary>
/// Builds installed on disk. Each build is a folder named after its release tag, holding one subfolder per
/// installed package: `&lt;library&gt;/&lt;tag&gt;/&lt;package id&gt;/...`. Installed packages are kept identical to the
/// release assets so they can serve as the base for delta updates.
/// </summary>
public sealed class BuildLibrary(string root)
{
    public string Root { get; } = root;

    public string StagingRoot => Path.Combine(Root, ".staging");

    public string BuildDirectory(string tag) =>
        FileUtil.IsSafeName(tag) ? Path.Combine(Root, tag) : throw new ArgumentException($"Unsafe tag \"{tag}\".");

    public IReadOnlyList<InstalledBuild> List()
    {
        var builds = new List<InstalledBuild>();
        if (!System.IO.Directory.Exists(Root))
            return builds;
        foreach (var dir in System.IO.Directory.EnumerateDirectories(Root))
        {
            var build = Load(dir);
            if (build is not null)
                builds.Add(build);
        }
        return builds;
    }

    public InstalledBuild? Find(string tag) => FileUtil.IsSafeName(tag) ? Load(BuildDirectory(tag)) : null;

    private static InstalledBuild? Load(string dir)
    {
        string path = Path.Combine(dir, InstalledBuild.FileName);
        if (!File.Exists(path))
            return null;
        try
        {
            var build = JsonSerializer.Deserialize(File.ReadAllText(path), LauncherJsonContext.Default.InstalledBuild);
            if (build is null || build.Tag != Path.GetFileName(dir))
                return null;
            build.Directory = dir;
            // Drop packages whose folder went missing.
            foreach (var id in build.Packages.Keys.ToList())
                if (!System.IO.Directory.Exists(build.PackageDirectory(id)))
                    build.Packages.Remove(id);
            return build;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }

    public void Save(InstalledBuild build)
    {
        System.IO.Directory.CreateDirectory(build.Directory);
        string path = Path.Combine(build.Directory, InstalledBuild.FileName);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(build, LauncherJsonContext.Default.InstalledBuild));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public string CreateStagingDirectory()
    {
        string dir = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Removes leftovers of interrupted installs.</summary>
    public void CleanStaging()
    {
        try
        {
            FileUtil.DeleteDirectory(StagingRoot);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // In use by another launcher instance, try again next time.
        }
    }

    /// <summary>Moves a fully prepared package folder into a build, replacing any previous copy.</summary>
    public InstalledBuild CommitPackage(string tag, string? commit, DateTimeOffset? publishedAt, string packageId,
        string stagedDir, string? patchedFrom, IEnumerable<string>? components = null)
    {
        var build = Find(tag) ?? new InstalledBuild { Tag = tag, Directory = BuildDirectory(tag) };
        build.Commit = commit ?? build.Commit;
        build.PublishedAt = publishedAt ?? build.PublishedAt;
        System.IO.Directory.CreateDirectory(build.Directory);

        string dest = build.PackageDirectory(packageId);
        FileUtil.DeleteDirectory(dest);
        System.IO.Directory.Move(stagedDir, dest);

        build.Packages[packageId] = new InstalledPackage
        {
            InstalledAt = DateTimeOffset.UtcNow,
            Executable = packageId == PackageKinds.ExportTemplates ? null : EditorLocator.Find(dest, packageId),
            PatchedFrom = patchedFrom,
            Components = components?.Order().ToList(),
        };
        Save(build);
        return build;
    }

    public void RemovePackage(InstalledBuild build, string packageId)
    {
        build.Packages.Remove(packageId);
        Save(build);
        FileUtil.DeleteDirectory(build.PackageDirectory(packageId));
    }

    public void Remove(InstalledBuild build)
    {
        // Delete the metadata first so a partially deleted build isn't picked up as installed.
        File.Delete(Path.Combine(build.Directory, InstalledBuild.FileName));
        FileUtil.DeleteDirectory(build.Directory);
    }

    /// <summary>Extracts a release zip, keeping the Unix permissions stored in it.</summary>
    /// <param name="include">Which files (by path in the zip) to extract; all when null.</param>
    public static void Extract(string archivePath, string destination, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default, Func<string, bool>? include = null)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries.Where(e => include is null || e.FullName.EndsWith('/') || include(e.FullName)).ToList();
        long total = Math.Max(1, entries.Sum(e => e.Length));
        long done = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = FileUtil.ResolveUnder(destination, entry.FullName);
            if (entry.FullName.EndsWith('/'))
            {
                System.IO.Directory.CreateDirectory(path);
                continue;
            }
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: true);
            int mode = (entry.ExternalAttributes >> 16) & 0x1FF;
            FileUtil.SetMode(path, mode == 0 ? 0x1A4 : mode);
            done += entry.Length;
            progress?.Report((double)done / total);
        }
    }
}
