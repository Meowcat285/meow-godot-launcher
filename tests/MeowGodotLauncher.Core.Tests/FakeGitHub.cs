using System.Net;
using System.Net.Http.Headers;

namespace MeowGodotLauncher.Core.Tests;

/// <summary>Serves files from memory like GitHub's release downloads, including explicit range requests.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    public Dictionary<string, byte[]> Files { get; } = [];

    /// <summary>Requested URLs, with the requested range after a space for range requests.</summary>
    public List<string> Requested { get; } = [];

    public bool Offline { get; set; }

    /// <summary>When false, range requests get the whole file, like servers without range support.</summary>
    public bool SupportsRanges { get; set; } = true;

    /// <summary>When set, responses wait for it, to test requests in flight.</summary>
    public TaskCompletionSource? Gate { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Gate is { } gate)
            await gate.Task.WaitAsync(cancellationToken);
        return Respond(request);
    }

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        if (Offline)
            throw new HttpRequestException("offline");
        string url = request.RequestUri!.ToString();
        var range = request.Headers.Range?.Ranges.SingleOrDefault();
        Requested.Add(range is null ? url : $"{url} {range.From}-{range.To}");
        if (!Files.TryGetValue(url, out var data))
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (range is null || !SupportsRanges)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        }
        // Like GitHub, only explicit ranges are supported.
        if (range.From is not { } from)
            return new HttpResponseMessage(HttpStatusCode.NotImplemented);
        long to = Math.Min(range.To ?? data.Length - 1, data.Length - 1);
        var content = new ByteArrayContent(data, (int)from, (int)(to - from + 1));
        content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, data.Length);
        return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
    }
}
