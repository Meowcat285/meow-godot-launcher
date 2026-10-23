using System.IO.Compression;
using MeowGodotLauncher.Core.Remote;

using static MeowGodotLauncher.Core.Tests.TestUtil;

namespace MeowGodotLauncher.Core.Tests;

public sealed class RemoteZipTests
{
    private const string Url = "https://dl/archive.zip";

    private static (FakeGitHub Server, HttpClient Http) Serve(byte[] data)
    {
        var server = new FakeGitHub();
        server.Files[Url] = data;
        return (server, new HttpClient(server));
    }

    [Fact]
    public async Task ExtractsEntriesWithRangeRequests()
    {
        var data = File.ReadAllBytes(Fixture("templates-new.zip"));
        var (server, http) = Serve(data);

        var zip = await RemoteZip.OpenAsync(http, Url, data.Length);

        using var local = ZipFile.OpenRead(Fixture("templates-new.zip"));
        Assert.Equal(local.Entries.Select(e => e.FullName).Order(), zip.Entries.Keys.Order());
        foreach (var entry in local.Entries.Where(e => !e.FullName.EndsWith('/')))
        {
            using var expected = new MemoryStream();
            using (var stream = entry.Open())
                stream.CopyTo(expected);
            using var actual = new MemoryStream();
            await zip.ExtractAsync(zip.Entries[entry.FullName], actual);
            Assert.Equal(expected.ToArray(), actual.ToArray());
        }
        Assert.All(server.Requested, r => Assert.Contains(' ', r));
    }

    [Fact]
    public async Task ReadsLargeCentralDirectories()
    {
        // More entries than fit in the tail that's fetched first.
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            for (int i = 0; i < 2000; i++)
                using (var writer = new StreamWriter(archive.CreateEntry($"some/fairly/long/folder/name/file-{i:D5}.txt").Open()))
                    writer.Write($"file {i}");
        var (_, http) = Serve(buffer.ToArray());

        var zip = await RemoteZip.OpenAsync(http, Url, buffer.Length);

        Assert.Equal(2000, zip.Entries.Count);
        using var output = new MemoryStream();
        await zip.ExtractAsync(zip.Entries["some/fairly/long/folder/name/file-01234.txt"], output);
        Assert.Equal("file 1234", System.Text.Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task DetectsCorruptData()
    {
        var data = File.ReadAllBytes(Fixture("templates-new.zip"));
        var zip = await RemoteZip.OpenAsync(Serve(data).Http, Url, data.Length);
        var entry = zip.Entries["templates/linux_debug.x86_64"];
        data[entry.LocalHeaderOffset + 30 + entry.Name.Length + 100] ^= 0xFF;

        await Assert.ThrowsAnyAsync<InvalidDataException>(() => zip.ExtractAsync(entry, new MemoryStream()));
    }

    [Fact]
    public async Task ReportsMissingRangeSupport()
    {
        var data = File.ReadAllBytes(Fixture("templates-new.zip"));
        var (server, http) = Serve(data);
        server.SupportsRanges = false;

        await Assert.ThrowsAsync<RangeNotSupportedException>(() => RemoteZip.OpenAsync(http, Url, data.Length));
    }
}
