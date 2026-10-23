using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace MeowGodotLauncher.Core.Remote;

public sealed class RangeNotSupportedException(string message) : IOException(message);

/// <param name="ExternalAttributes">Holds the Unix permissions in its upper 16 bits for zips made on Unix.</param>
public sealed record RemoteZipEntry(string Name, int Method, uint Crc32, long CompressedSize, long Size, long LocalHeaderOffset,
    int ExternalAttributes = 0)
{
    public bool IsDirectory => Name.EndsWith('/');
}

/// <summary>
/// Reads single entries of a zip file over HTTP with range requests, without downloading the whole file. This is
/// how the editor's export template manager downloads templates for one platform out of the `.tpz`, and the
/// launcher does the same, plus reading only the parts of a delta it needs.
/// </summary>
public sealed class RemoteZip
{
    private const int TailSize = 1 << 16;
    // Room for local headers whose extra field is larger than the central directory's copy.
    private const int LocalHeaderSlack = 4096;

    private readonly HttpClient _http;

    private RemoteZip(HttpClient http, string url, long length, Dictionary<string, RemoteZipEntry> entries)
    {
        _http = http;
        Url = url;
        Length = length;
        Entries = entries;
    }

    public string Url { get; }

    public long Length { get; }

    public IReadOnlyDictionary<string, RemoteZipEntry> Entries { get; }

    /// <summary>Reads the central directory of the zip at <paramref name="url"/>.</summary>
    /// <param name="length">The file size if known (GitHub lists it with each asset), saving a request.</param>
    public static async Task<RemoteZip> OpenAsync(HttpClient http, string url, long? length = null, CancellationToken cancellationToken = default)
    {
        // GitHub's asset storage rejects suffix ranges ("bytes=-N"), so the size is needed to read the end.
        long size = length ?? (await FetchRangeAsync(http, url, 0, 0, cancellationToken)).Total;
        long tailStart = Math.Max(0, size - TailSize);
        var tail = (await FetchRangeAsync(http, url, tailStart, size - 1, cancellationToken)).Data;

        int eocd = FindBackwards(tail, 0x06054b50, 22);
        if (eocd < 0)
            throw new InvalidDataException("Not a zip file: end of central directory not found.");
        long entryCount = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10));
        long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12));
        long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16));

        if (eocd >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd - 20)) == 0x07064b50)
        {
            // Zip64: the real values are in the zip64 end of central directory record.
            long zip64Offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(eocd - 20 + 8));
            byte[] record = zip64Offset >= tailStart
                ? tail[(int)(zip64Offset - tailStart)..]
                : (await FetchRangeAsync(http, url, zip64Offset, zip64Offset + 55, cancellationToken)).Data;
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x06064b50)
                throw new InvalidDataException("Corrupt zip64 end of central directory.");
            entryCount = (long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(32));
            cdSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(40));
            cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(48));
        }

        byte[] cd = cdOffset >= tailStart && cdOffset + cdSize <= size
            ? tail[(int)(cdOffset - tailStart)..(int)(cdOffset - tailStart + cdSize)]
            : (await FetchRangeAsync(http, url, cdOffset, cdOffset + cdSize - 1, cancellationToken)).Data;

        return new RemoteZip(http, url, size, ParseCentralDirectory(cd, entryCount));
    }

    internal static Dictionary<string, RemoteZipEntry> ParseCentralDirectory(byte[] cd, long entryCount)
    {
        var entries = new Dictionary<string, RemoteZipEntry>();
        int pos = 0;
        for (long i = 0; i < entryCount; i++)
        {
            var record = cd.AsSpan(pos);
            if (record.Length < 46 || BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x02014b50)
                throw new InvalidDataException("Corrupt zip central directory.");
            int method = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
            long compressed = BinaryPrimitives.ReadUInt32LittleEndian(record[20..]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(record[24..]);
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[28..]);
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(record[30..]);
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(record[32..]);
            int attributes = BinaryPrimitives.ReadInt32LittleEndian(record[38..]);
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(record[42..]);
            string name = Encoding.UTF8.GetString(record.Slice(46, nameLength));

            // Zip64 extended information replaces the fields that overflowed, in this order.
            var extra = record.Slice(46 + nameLength, extraLength);
            while (extra.Length >= 4)
            {
                int id = BinaryPrimitives.ReadUInt16LittleEndian(extra);
                int length = BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]);
                if (id == 0x0001)
                {
                    var data = extra.Slice(4, length);
                    if (size == 0xFFFFFFFF) { size = (long)BinaryPrimitives.ReadUInt64LittleEndian(data); data = data[8..]; }
                    if (compressed == 0xFFFFFFFF) { compressed = (long)BinaryPrimitives.ReadUInt64LittleEndian(data); data = data[8..]; }
                    if (offset == 0xFFFFFFFF) offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(data);
                }
                extra = extra[Math.Min(extra.Length, 4 + length)..];
            }

            entries[name] = new RemoteZipEntry(name, method, crc, compressed, size, offset, attributes);
            pos += 46 + nameLength + extraLength + commentLength;
        }
        return entries;
    }

    /// <summary>Downloads and decompresses one entry into <paramref name="output"/>, checking its CRC.</summary>
    /// <param name="progress">Receives the number of compressed bytes received.</param>
    public async Task ExtractAsync(RemoteZipEntry entry, Stream output, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (entry.Method is not (0 or 8))
            throw new InvalidDataException($"Unsupported compression method {entry.Method} for \"{entry.Name}\".");
        long end = Math.Min(Length - 1, entry.LocalHeaderOffset + 30 + Encoding.UTF8.GetByteCount(entry.Name) + LocalHeaderSlack + entry.CompressedSize);

        using var response = await SendRangeAsync(_http, Url, entry.LocalHeaderOffset, end, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);

        var header = new byte[30];
        await body.ReadExactlyAsync(header, cancellationToken);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x04034b50)
            throw new InvalidDataException($"Corrupt local header for \"{entry.Name}\".");
        int skip = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26)) + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
        if (30 + skip > 30 + Encoding.UTF8.GetByteCount(entry.Name) + LocalHeaderSlack)
            throw new InvalidDataException($"Local header of \"{entry.Name}\" is unexpectedly large.");
        await body.ReadExactlyAsync(new byte[skip], cancellationToken);

        await using var compressed = new CountingReadStream(body, entry.CompressedSize, progress);
        await using var data = entry.Method == 8 ? new DeflateStream(compressed, CompressionMode.Decompress, leaveOpen: true) : (Stream)compressed;
        var crc = new Crc32();
        var buffer = new byte[1 << 16];
        long written = 0;
        int read;
        while ((read = await data.ReadAsync(buffer, cancellationToken)) > 0)
        {
            crc.Append(buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            written += read;
        }
        if (written != entry.Size || crc.GetCurrentHashAsUInt32() != entry.Crc32)
            throw new InvalidDataException($"\"{entry.Name}\" doesn't match its checksum after downloading.");
    }

    public async Task ExtractToFileAsync(RemoteZipEntry entry, string path, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var file = new FileStream(path + ".part", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            await ExtractAsync(entry, file, progress, cancellationToken);
        File.Move(path + ".part", path, overwrite: true);
    }

    private static async Task<HttpResponseMessage> SendRangeAsync(HttpClient http, string url, long from, long to,
        HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(from, to);
        var response = await http.SendAsync(request, completion, cancellationToken);
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            var status = response.StatusCode;
            response.Dispose();
            if (status == HttpStatusCode.OK)
                throw new RangeNotSupportedException("The server doesn't support downloading part of a file.");
            throw new HttpRequestException($"Range request failed with {(int)status} {status}.", null, status);
        }
        return response;
    }

    private static async Task<(byte[] Data, long Total)> FetchRangeAsync(HttpClient http, string url, long from, long to, CancellationToken cancellationToken)
    {
        using var response = await SendRangeAsync(http, url, from, to, HttpCompletionOption.ResponseContentRead, cancellationToken);
        long total = response.Content.Headers.ContentRange?.Length ?? throw new InvalidDataException("Missing Content-Range header.");
        return (await response.Content.ReadAsByteArrayAsync(cancellationToken), total);
    }

    private static int FindBackwards(byte[] data, uint signature, int minLength)
    {
        for (int i = data.Length - minLength; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)) == signature)
                return i;
        return -1;
    }

    /// <summary>Reads at most a given number of bytes from a stream, reporting progress.</summary>
    private sealed class CountingReadStream(Stream inner, long limit, IProgress<long>? progress) : Stream
    {
        private long _read;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = inner.Read(buffer[..(int)Math.Min(buffer.Length, limit - _read)]);
            Advance(n);
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int n = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, limit - _read)], cancellationToken);
            Advance(n);
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private void Advance(int n)
        {
            _read += n;
            if (n > 0)
                progress?.Report(_read);
            else if (_read < limit)
                throw new EndOfStreamException("Download ended early.");
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => limit;
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
