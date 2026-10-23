using System.IO.Compression;
using System.Text.Json;
using MeowGodotLauncher.Core.Util;
using ZstdSharp;

namespace MeowGodotLauncher.Core.Delta;

public sealed class DeltaException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Builds a new package folder from an installed one and a delta archive.</summary>
public static class DeltaApplier
{
    /// <param name="deltaPath">The `.mgdelta` file.</param>
    /// <param name="sourceDir">Extracted package of the release the delta upgrades from. Not modified.</param>
    /// <param name="targetDir">Empty or missing folder to write the new package to.</param>
    /// <param name="progress">Receives the fraction of the target bytes written so far.</param>
    public static DeltaDescriptor Apply(string deltaPath, string sourceDir, string targetDir,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var archive = ZipFile.OpenRead(deltaPath);
        var descriptor = ReadDescriptor(archive);
        Directory.CreateDirectory(targetDir);

        long total = Math.Max(1, descriptor.Operations.Sum(o => o.Size));
        long done = 0;
        foreach (var op in descriptor.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = FileUtil.ResolveUnder(targetDir, op.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            try
            {
                ApplyOperation(archive, op, sourceDir, target, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException and not DeltaException)
            {
                throw new DeltaException($"Failed to apply delta to \"{op.Path}\": {e.Message}", e);
            }

            FileUtil.SetMode(target, op.Mode);
            done += op.Size;
            progress?.Report((double)done / total);
        }
        return descriptor;
    }

    public static DeltaDescriptor ReadDescriptor(ZipArchive archive)
    {
        var entry = archive.GetEntry(DeltaDescriptor.EntryName) ?? throw new DeltaException("Delta archive has no descriptor.");
        using var stream = entry.Open();
        var descriptor = JsonSerializer.Deserialize(stream, LauncherJsonContext.Default.DeltaDescriptor)
            ?? throw new DeltaException("Delta descriptor is empty.");
        if (descriptor.Format != ReleaseManifest.CurrentFormat)
            throw new DeltaException($"Unsupported delta format {descriptor.Format}.");
        return descriptor;
    }

    private static void ApplyOperation(ZipArchive archive, DeltaOperation op, string sourceDir, string target, CancellationToken cancellationToken)
    {
        using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        using (var output = new HashingStream(file))
        {
            switch (op.Op)
            {
                case DeltaOps.Copy:
                {
                    using var source = File.OpenRead(SourcePath(sourceDir, op));
                    source.CopyTo(output);
                    break;
                }
                case DeltaOps.Patch:
                {
                    using var patch = OpenEntry(archive, op);
                    BsPatch.Apply(patch, SourcePath(sourceDir, op), output, op.Size, cancellationToken);
                    break;
                }
                case DeltaOps.Add:
                {
                    using var entry = OpenEntry(archive, op);
                    using var data = new DecompressionStream(entry);
                    data.CopyTo(output);
                    break;
                }
                default:
                    throw new DeltaException($"Unknown delta operation \"{op.Op}\".");
            }

            // This also catches sources that were modified after being installed, since a patch applied
            // to the wrong data doesn't produce the expected file.
            if (output.BytesWritten != op.Size || !string.Equals(output.GetHashHex(), op.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new DeltaException($"\"{op.Path}\" doesn't match the expected checksum after patching.");
        }
    }

    private static string SourcePath(string sourceDir, DeltaOperation op)
    {
        if (string.IsNullOrEmpty(op.Source))
            throw new DeltaException($"Operation for \"{op.Path}\" has no source.");
        string path = FileUtil.ResolveUnder(sourceDir, op.Source);
        if (!File.Exists(path))
            throw new DeltaException($"Source file \"{op.Source}\" is missing from the installed build.");
        return path;
    }

    private static Stream OpenEntry(ZipArchive archive, DeltaOperation op)
    {
        var entry = (op.Entry is null ? null : archive.GetEntry(op.Entry))
            ?? throw new DeltaException($"Delta archive is missing the data for \"{op.Path}\".");
        return entry.Open();
    }
}
