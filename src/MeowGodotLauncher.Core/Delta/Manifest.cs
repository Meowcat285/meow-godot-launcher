using System.Text.Json.Serialization;

namespace MeowGodotLauncher.Core.Delta;

// These types mirror the JSON written by `misc/scripts/generate_delta_patches.py` in the Godot fork.
// Bump `Format` on both sides when making incompatible changes.

/// <summary>
/// Published as the <see cref="FileName"/> asset of a release. Describes the contents of each package
/// (release asset) of the release, and the delta patches that can be used to build it from older releases.
/// </summary>
public sealed class ReleaseManifest
{
    public const string FileName = "meow-launcher-manifest.json";
    public const int CurrentFormat = 1;

    [JsonPropertyName("format")]
    public int Format { get; set; }

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "";

    [JsonPropertyName("commit")]
    public string Commit { get; set; } = "";

    [JsonPropertyName("packages")]
    public List<PackageManifest> Packages { get; set; } = [];

    public PackageManifest? FindPackage(string id) => Packages.FirstOrDefault(p => p.Id == id);
}

public sealed class PackageManifest
{
    /// <summary>One of the ids in <see cref="Library.PackageKinds"/>.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Name of the full release asset (a zip archive) for this package.</summary>
    [JsonPropertyName("asset")]
    public string Asset { get; set; } = "";

    [JsonPropertyName("asset_size")]
    public long AssetSize { get; set; }

    [JsonPropertyName("asset_sha256")]
    public string AssetSha256 { get; set; } = "";

    /// <summary>Files of the extracted package, relative to the extraction root, using '/' separators.</summary>
    [JsonPropertyName("files")]
    public List<FileEntry> Files { get; set; } = [];

    [JsonPropertyName("deltas")]
    public List<DeltaEntry> Deltas { get; set; } = [];
}

public sealed class FileEntry
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    /// <summary>Unix permission bits (e.g. 0o755 for executables).</summary>
    [JsonPropertyName("mode")]
    public int Mode { get; set; } = 0x1A4; // 0o644
}

public sealed class DeltaEntry
{
    /// <summary>Tag of the release this delta upgrades from.</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    [JsonPropertyName("asset")]
    public string Asset { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";
}

/// <summary>
/// The <see cref="EntryName"/> entry of a delta archive. The archive is an uncompressed zip that also holds
/// one zstd stream per <see cref="DeltaOperation"/> that needs data.
/// </summary>
public sealed class DeltaDescriptor
{
    public const string EntryName = "delta.json";

    [JsonPropertyName("format")]
    public int Format { get; set; }

    [JsonPropertyName("package")]
    public string Package { get; set; } = "";

    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    [JsonPropertyName("to")]
    public string To { get; set; } = "";

    /// <summary>One operation per file of the target package. Files of the source that aren't listed are dropped.</summary>
    [JsonPropertyName("operations")]
    public List<DeltaOperation> Operations { get; set; } = [];
}

public static class DeltaOps
{
    /// <summary>Copy <see cref="DeltaOperation.Source"/> unchanged.</summary>
    public const string Copy = "copy";
    /// <summary>Decompress <see cref="DeltaOperation.Entry"/> using <see cref="DeltaOperation.Source"/> as reference (bsdiff in a zstd stream, see <see cref="BsPatch"/>).</summary>
    public const string Patch = "patch";
    /// <summary>Apply the HDiffPatch patch in <see cref="DeltaOperation.Entry"/> to <see cref="DeltaOperation.Source"/> (see <see cref="HPatch"/>).</summary>
    public const string HPatch = "hpatch";
    /// <summary>Decompress <see cref="DeltaOperation.Entry"/> as a regular zstd stream.</summary>
    public const string Add = "add";
    /// <summary>
    /// Rebuild a zip archive from the <see cref="DeltaOperation.Entries"/> operations, applied to the entries of the
    /// <see cref="DeltaOperation.Source"/> archive, which can themselves be archive operations. The result isn't
    /// byte-identical to the original, so only its entries are verified.
    /// </summary>
    public const string Archive = "archive";
    /// <summary>A folder entry of an archive.</summary>
    public const string Dir = "dir";
}

public sealed class DeltaOperation
{
    [JsonPropertyName("op")]
    public string Op { get; set; } = "";

    /// <summary>Path of the target file.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("mode")]
    public int Mode { get; set; } = 0x1A4;

    /// <summary>Path of the source file, for <see cref="DeltaOps.Copy"/>, <see cref="DeltaOps.Patch"/> and <see cref="DeltaOps.HPatch"/>.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("source_sha256")]
    public string? SourceSha256 { get; set; }

    /// <summary>Name of the archive entry holding the data, for <see cref="DeltaOps.Patch"/>, <see cref="DeltaOps.HPatch"/> and <see cref="DeltaOps.Add"/>.</summary>
    [JsonPropertyName("entry")]
    public string? Entry { get; set; }

    /// <summary>For <see cref="DeltaOps.Archive"/>, the operations producing each entry, with entry names as paths.</summary>
    [JsonPropertyName("entries")]
    public List<DeltaOperation>? Entries { get; set; }

    /// <summary>For archive entries, whether to deflate the entry rather than store it.</summary>
    [JsonPropertyName("compressed")]
    public bool Compressed { get; set; }
}
