using System.IO.Compression;
using System.Security.Cryptography;
using MeowGodotLauncher.Core.Util;

namespace MeowGodotLauncher.Core.Tests;

internal static class TestUtil
{
    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// <summary>
    /// Hashes of the files in a folder, by relative path. Nested zips are expanded into their entries, since
    /// archives rebuilt from deltas only have the same content as the originals, not the same bytes.
    /// </summary>
    public static Dictionary<string, string> Snapshot(string dir)
    {
        var result = new Dictionary<string, string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            if (!file.EndsWith(".zip", StringComparison.Ordinal))
            {
                result[rel] = FileUtil.Sha256(file);
                continue;
            }
            using var archive = ZipFile.OpenRead(file);
            foreach (var entry in archive.Entries)
            {
                using var stream = entry.Open();
                result[$"{rel}!{entry.FullName}"] = entry.FullName.EndsWith('/') ? "dir" : Convert.ToHexStringLower(SHA256.HashData(stream));
            }
        }
        return result;
    }
}
