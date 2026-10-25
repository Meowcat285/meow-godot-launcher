using System.IO.Compression;
using System.Security.Cryptography;
using MeowGodotLauncher.Core.Util;

namespace MeowGodotLauncher.Core.Tests;

internal static class TestUtil
{
    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// <summary>
    /// Hashes of the files in a folder, by relative path. Nested zips (including APKs, AARs, and zips inside them)
    /// are expanded into their entries, since archives rebuilt from deltas only have the same content as the
    /// originals, not the same bytes.
    /// </summary>
    public static Dictionary<string, string> Snapshot(string dir)
    {
        var result = new Dictionary<string, string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            using var stream = File.OpenRead(file);
            AddToSnapshot(result, rel, stream);
        }
        return result;
    }

    private static void AddToSnapshot(Dictionary<string, string> result, string name, Stream stream)
    {
        if (!IsArchive(name))
        {
            result[name] = Convert.ToHexStringLower(SHA256.HashData(stream));
            return;
        }
        // Entry streams can't seek, which reading a zip needs.
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        buffer.Position = 0;
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            string entryName = $"{name}!{entry.FullName}";
            if (entry.FullName.EndsWith('/'))
            {
                result[entryName] = "dir";
                continue;
            }
            using var entryStream = entry.Open();
            AddToSnapshot(result, entryName, entryStream);
        }
    }

    private static bool IsArchive(string name) =>
        name.EndsWith(".zip", StringComparison.Ordinal) || name.EndsWith(".apk", StringComparison.Ordinal)
        || name.EndsWith(".aar", StringComparison.Ordinal);
}
