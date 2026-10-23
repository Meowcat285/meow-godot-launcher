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

    private static void ApplyOperation(ZipArchive delta, DeltaOperation op, string sourceDir, string target, CancellationToken cancellationToken)
    {
        if (op.Op == DeltaOps.Archive)
        {
            ApplyArchive(delta, op, sourceDir, target, cancellationToken);
            return;
        }
        using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        WriteFile(delta, op, new DirectorySource(sourceDir), file, cancellationToken);
    }

    private static void ApplyArchive(ZipArchive delta, DeltaOperation op, string sourceDir, string target, CancellationToken cancellationToken)
    {
        if (op.Entries is null)
            throw new DeltaException($"Archive operation for \"{op.Path}\" has no entries.");
        using var sourceArchive = ZipFile.OpenRead(SourcePath(sourceDir, op));
        using var source = new ArchiveSource(sourceArchive);
        using var file = new FileStream(target, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);

        foreach (var entryOp in op.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entryOp.Op == DeltaOps.Dir)
            {
                var dir = archive.CreateEntry(entryOp.Path.EndsWith('/') ? entryOp.Path : entryOp.Path + "/");
                dir.ExternalAttributes = (entryOp.Mode | 0x4000) << 16; // S_IFDIR
                continue;
            }
            var entry = archive.CreateEntry(entryOp.Path, entryOp.Compressed ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
            entry.ExternalAttributes = (entryOp.Mode | 0x8000) << 16; // S_IFREG
            using var stream = entry.Open();
            WriteFile(delta, entryOp, source, stream, cancellationToken);
        }
    }

    /// <summary>Writes the output of a copy, patch or add operation, and checks it.</summary>
    private static void WriteFile(ZipArchive delta, DeltaOperation op, ISource source, Stream destination, CancellationToken cancellationToken)
    {
        using var output = new HashingStream(destination);
        switch (op.Op)
        {
            case DeltaOps.Copy:
            {
                using var input = source.Open(op);
                input.CopyTo(output);
                break;
            }
            case DeltaOps.Patch:
            {
                using var patch = OpenEntry(delta, op);
                BsPatch.Apply(patch, source.GetFile(op), output, op.Size, cancellationToken);
                break;
            }
            case DeltaOps.Add:
            {
                using var entry = OpenEntry(delta, op);
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

    /// <summary>Where the source files of operations come from.</summary>
    private interface ISource
    {
        Stream Open(DeltaOperation op);

        /// <summary>A file on disk with the source's content, since patching needs random access.</summary>
        string GetFile(DeltaOperation op);
    }

    private sealed class DirectorySource(string dir) : ISource
    {
        public Stream Open(DeltaOperation op) => File.OpenRead(GetFile(op));
        public string GetFile(DeltaOperation op) => SourcePath(dir, op);
    }

    /// <summary>Entries of an installed archive, extracted to temporary files when they need patching.</summary>
    private sealed class ArchiveSource(ZipArchive archive) : ISource, IDisposable
    {
        private readonly string _temp = Path.Combine(Path.GetTempPath(), "meow-launcher-" + Guid.NewGuid().ToString("N"));
        private int _count;

        private ZipArchiveEntry Find(DeltaOperation op) =>
            (op.Source is null ? null : archive.GetEntry(op.Source))
            ?? throw new DeltaException($"Entry \"{op.Source}\" is missing from the installed archive.");

        public Stream Open(DeltaOperation op) => Find(op).Open();

        public string GetFile(DeltaOperation op)
        {
            Directory.CreateDirectory(_temp);
            string path = Path.Combine(_temp, (_count++).ToString());
            Find(op).ExtractToFile(path);
            return path;
        }

        public void Dispose()
        {
            try
            {
                FileUtil.DeleteDirectory(_temp);
            }
            catch (IOException)
            {
            }
        }
    }
}
