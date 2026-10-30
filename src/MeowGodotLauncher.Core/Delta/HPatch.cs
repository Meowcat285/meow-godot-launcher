using SharpHDiffPatch.Core;

namespace MeowGodotLauncher.Core.Delta;

/// <summary>
/// Applies HDiffPatch patches (`HDIFF13`, zstd-compressed), written by `hdiffz` in `generate_delta_patches.py`,
/// using SharpHDiffPatch.
/// </summary>
public static class HPatch
{
    /// <summary>Writes the patched file to <paramref name="output"/>. Stops with an exception on malformed patches.</summary>
    public static void Apply(Stream patch, string oldPath, Stream output, long newSize, CancellationToken cancellationToken = default)
    {
        // SharpHDiffPatch reads the patch and writes the new file by path.
        string? patchPath = null;
        string? newPath = null;
        try
        {
            patchPath = Path.GetTempFileName();
            using (var patchFile = File.Create(patchPath))
                patch.CopyTo(patchFile);
            // Checked before patching, so a bad patch can't write a huge file.
            if (HDiffPatch.GetHDiffNewSize(patchPath) != newSize)
                throw new InvalidDataException("Patch doesn't produce a file of the expected size.");

            newPath = Path.GetTempFileName();
            var patcher = new HDiffPatch();
            patcher.Initialize(patchPath);
            patcher.Patch(oldPath, newPath, useBufferedPatch: true, cancellationToken, useFullBuffer: false, useFastBuffer: true);

            using var newFile = File.OpenRead(newPath);
            newFile.CopyTo(output);
        }
        finally
        {
            if (patchPath is not null)
                File.Delete(patchPath);
            if (newPath is not null)
                File.Delete(newPath);
        }
    }
}
