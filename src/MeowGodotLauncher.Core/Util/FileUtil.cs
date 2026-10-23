using System.Security.Cryptography;

namespace MeowGodotLauncher.Core.Util;

public static class FileUtil
{
    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    /// <summary>
    /// Resolves an archive-relative path (with '/' separators) under <paramref name="root"/>, refusing paths
    /// that would escape it.
    /// </summary>
    public static string ResolveUnder(string root, string relativePath)
    {
        string fullRoot = Path.GetFullPath(root);
        string full = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException($"Path \"{relativePath}\" escapes its destination folder.");
        return full;
    }

    /// <summary>Applies Unix permission bits (ignored on Windows).</summary>
    public static void SetMode(string path, int mode)
    {
        if (OperatingSystem.IsWindows() || mode == 0)
            return;
        File.SetUnixFileMode(path, (UnixFileMode)(mode & 0x1FF));
    }

    public static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;
        // Read-only files can't be deleted on Windows.
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    /// <summary>Tags are used as folder names, so only accept ones that are safe as such.</summary>
    public static bool IsSafeName(string name) =>
        name.Length > 0 && name != "." && name != ".." && name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0
        && !name.Any(char.IsControl);

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KiB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MiB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GiB",
    };
}

/// <summary>A write-only stream that hashes and counts everything written through it.</summary>
public sealed class HashingStream(Stream inner) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public long BytesWritten { get; private set; }

    public string GetHashHex() => Convert.ToHexStringLower(_hash.GetHashAndReset());

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _hash.AppendData(buffer);
        inner.Write(buffer);
        BytesWritten += buffer.Length;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _hash.AppendData(buffer.Span);
        await inner.WriteAsync(buffer, cancellationToken);
        BytesWritten += buffer.Length;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => BytesWritten;
    public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _hash.Dispose();
        base.Dispose(disposing);
    }
}
