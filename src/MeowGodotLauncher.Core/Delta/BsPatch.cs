using System.Buffers.Binary;
using System.Numerics;
using Microsoft.Win32.SafeHandles;
using ZstdSharp;

namespace MeowGodotLauncher.Core.Delta;

/// <summary>
/// Applies patches in the `MGBSDF01` format written by `generate_delta_patches.py`: the magic, followed by a
/// zstd stream of bsdiff records. Each record is a header of three little-endian int64 (diff length, extra length,
/// seek), then `diff length` bytes to add to the old file at the current position, then `extra length` bytes to
/// copy as-is. The old file position then moves by the diff length plus `seek`.
/// </summary>
public static class BsPatch
{
    public static ReadOnlySpan<byte> Magic => "MGBSDF01"u8;

    private const int BufferSize = 1 << 20;

    /// <summary>Writes the patched file to <paramref name="output"/>. Stops with an exception on malformed patches.</summary>
    public static void Apply(Stream patch, string oldPath, Stream output, long newSize, CancellationToken cancellationToken = default)
    {
        Span<byte> magic = stackalloc byte[8];
        patch.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic))
            throw new InvalidDataException("Not a MGBSDF01 patch.");

        using var old = File.OpenHandle(oldPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long oldSize = RandomAccess.GetLength(old);
        using var data = new DecompressionStream(patch, leaveOpen: true);

        var header = new byte[24];
        var buffer = new byte[BufferSize];
        var oldBuffer = new byte[BufferSize];
        long oldPos = 0;
        long newPos = 0;

        while (newPos < newSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            data.ReadExactly(header);
            long diffLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0));
            long extraLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
            long seek = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16));
            if (diffLength < 0 || extraLength < 0 || diffLength > newSize - newPos || extraLength > newSize - newPos - diffLength)
                throw new InvalidDataException("Corrupt patch: record exceeds the new file size.");

            for (long done = 0; done < diffLength;)
            {
                int chunk = (int)Math.Min(BufferSize, diffLength - done);
                var diff = buffer.AsSpan(0, chunk);
                data.ReadExactly(diff);
                var oldChunk = oldBuffer.AsSpan(0, chunk);
                ReadOld(old, oldSize, oldPos + done, oldChunk);
                AddInPlace(diff, oldChunk);
                output.Write(diff);
                done += chunk;
            }
            newPos += diffLength;
            oldPos += diffLength;

            for (long done = 0; done < extraLength;)
            {
                int chunk = (int)Math.Min(BufferSize, extraLength - done);
                data.ReadExactly(buffer.AsSpan(0, chunk));
                output.Write(buffer.AsSpan(0, chunk));
                done += chunk;
            }
            newPos += extraLength;
            oldPos += seek;
        }
    }

    /// <summary>Reads old file bytes at <paramref name="offset"/>, using zeros outside of the file like bspatch does.</summary>
    private static void ReadOld(SafeFileHandle old, long oldSize, long offset, Span<byte> destination)
    {
        destination.Clear();
        long start = Math.Max(offset, 0);
        long end = Math.Min(offset + destination.Length, oldSize);
        while (start < end)
        {
            int read = RandomAccess.Read(old, destination.Slice((int)(start - offset), (int)(end - start)), start);
            if (read <= 0)
                throw new EndOfStreamException("Old file is shorter than expected.");
            start += read;
        }
    }

    private static void AddInPlace(Span<byte> diff, ReadOnlySpan<byte> old)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i <= diff.Length - Vector<byte>.Count; i += Vector<byte>.Count)
            {
                var sum = new Vector<byte>(diff[i..]) + new Vector<byte>(old[i..]);
                sum.CopyTo(diff[i..]);
            }
        }
        for (; i < diff.Length; i++)
            diff[i] += old[i];
    }
}
