using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MeowGodotLauncher.Core.Delta;
using MeowGodotLauncher.Core.Util;

namespace MeowGodotLauncher.Core.GitHub;

public sealed record ReleaseList(IReadOnlyList<ReleaseInfo> Releases, bool FromCache, string? Warning);

/// <summary>
/// Reads releases from the GitHub REST API and downloads their assets. Responses are cached on disk so the
/// launcher keeps working offline, and revalidated with ETags, which don't count against the API rate limit.
/// Asset downloads go through github.com rather than the API, so they aren't rate limited either.
/// </summary>
public sealed class GitHubReleaseClient
{
    private readonly HttpClient _http;
    private readonly string _owner;
    private readonly string _repo;
    private readonly string? _token;
    private readonly string _cacheDir;

    public GitHubReleaseClient(HttpClient http, string owner, string repo, string? token, string cacheDir)
    {
        _http = http;
        _owner = owner;
        _repo = repo;
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        _cacheDir = cacheDir;
    }

    public string Repository => $"{_owner}/{_repo}";

    private string ReleasesCachePath => Path.Combine(_cacheDir, $"releases-{_owner}-{_repo}.json");
    private string ManifestCacheDir => Path.Combine(_cacheDir, "manifests");

    public static HttpClient CreateHttpClient()
    {
        var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All });
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MeowGodotLauncher", "0.1"));
        http.Timeout = Timeout.InfiniteTimeSpan; // Large downloads; cancellation is handled by callers.
        return http;
    }

    /// <summary>Lists published releases, newest first.</summary>
    public async Task<ReleaseList> GetReleasesAsync(int maxCount, CancellationToken cancellationToken = default)
    {
        var cached = await ReadCacheAsync(cancellationToken);
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{_owner}/{_repo}/releases?per_page={Math.Clamp(maxCount, 1, 100)}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (_token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (cached?.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var parsed))
            request.Headers.IfNoneMatch.Add(parsed);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
                return new ReleaseList(Filter(cached.Releases), false, null);
            if (!response.IsSuccessStatusCode)
            {
                string reason = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                    ? "GitHub API rate limit reached, add a token in Settings to raise it"
                    : $"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}";
                return Fallback(cached, reason);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var releases = JsonSerializer.Deserialize(json, LauncherJsonContext.Default.ListReleaseInfo) ?? [];
            await WriteCacheAsync(new ReleaseCache { ETag = response.Headers.ETag?.ToString(), Releases = releases }, cancellationToken);
            return new ReleaseList(Filter(releases), false, null);
        }
        catch (HttpRequestException e)
        {
            return Fallback(cached, $"Couldn't reach GitHub ({e.Message})");
        }
    }

    private static ReleaseList Fallback(ReleaseCache? cached, string reason)
    {
        if (cached is null)
            throw new HttpRequestException(reason + ".");
        return new ReleaseList(Filter(cached.Releases), true, reason + ", showing cached releases.");
    }

    private static List<ReleaseInfo> Filter(List<ReleaseInfo> releases) =>
        releases.Where(r => !r.Draft && FileUtil.IsSafeName(r.Tag))
            .OrderByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue)
            .ToList();

    /// <summary>Gets the delta manifest of a release, or null for releases published without one.</summary>
    public async Task<ReleaseManifest?> GetManifestAsync(ReleaseInfo release, CancellationToken cancellationToken = default)
    {
        var asset = release.FindAsset(ReleaseManifest.FileName);
        if (asset is null)
            return null;

        // The asset id changes when the manifest is re-uploaded, so it makes a good cache key.
        string cachePath = Path.Combine(ManifestCacheDir, $"{asset.Id}.json");
        string json;
        if (File.Exists(cachePath))
        {
            json = await File.ReadAllTextAsync(cachePath, cancellationToken);
        }
        else
        {
            json = await _http.GetStringAsync(asset.DownloadUrl, cancellationToken);
            Directory.CreateDirectory(ManifestCacheDir);
            await File.WriteAllTextAsync(cachePath, json, cancellationToken);
        }

        var manifest = JsonSerializer.Deserialize(json, LauncherJsonContext.Default.ReleaseManifest);
        return manifest is { Format: ReleaseManifest.CurrentFormat } && manifest.Tag == release.Tag ? manifest : null;
    }

    /// <summary>Downloads a file, reporting the number of bytes received, and checks its size and checksum.</summary>
    public async Task DownloadAsync(string url, string destination, long expectedSize, string? expectedSha256,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string partial = destination + ".part";

        using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            await using var output = new HashingStream(file);

            var buffer = new byte[1 << 16];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                progress?.Report(output.BytesWritten);
            }

            if (expectedSize > 0 && output.BytesWritten != expectedSize)
                throw new IOException($"Download of {Path.GetFileName(destination)} is incomplete ({output.BytesWritten} of {expectedSize} bytes).");
            if (!string.IsNullOrEmpty(expectedSha256) && !string.Equals(output.GetHashHex(), expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Download of {Path.GetFileName(destination)} doesn't match its checksum.");
        }
        File.Move(partial, destination, overwrite: true);
    }

    public string AssetUrl(ReleaseInfo release, string assetName) =>
        release.FindAsset(assetName)?.DownloadUrl
        ?? $"https://github.com/{_owner}/{_repo}/releases/download/{Uri.EscapeDataString(release.Tag)}/{Uri.EscapeDataString(assetName)}";

    private async Task<ReleaseCache?> ReadCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(ReleasesCachePath))
                return null;
            await using var stream = File.OpenRead(ReleasesCachePath);
            return await JsonSerializer.DeserializeAsync(stream, LauncherJsonContext.Default.ReleaseCache, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task WriteCacheAsync(ReleaseCache cache, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheDir);
        await using var stream = File.Create(ReleasesCachePath);
        await JsonSerializer.SerializeAsync(stream, cache, LauncherJsonContext.Default.ReleaseCache, cancellationToken);
    }
}

public sealed class ReleaseCache
{
    public string? ETag { get; set; }
    public List<ReleaseInfo> Releases { get; set; } = [];
}
