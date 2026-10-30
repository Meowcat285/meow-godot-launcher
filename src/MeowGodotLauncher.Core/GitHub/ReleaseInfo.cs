using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MeowGodotLauncher.Core.GitHub;

/// <summary>The subset of the GitHub REST API release object the launcher uses.</summary>
public sealed partial class ReleaseInfo
{
    [JsonPropertyName("tag_name")]
    public string Tag { get; set; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("target_commitish")]
    public string? TargetCommitish { get; set; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("assets")]
    public List<ReleaseAsset> Assets { get; set; } = [];

    public ReleaseAsset? FindAsset(string name) => Assets.FirstOrDefault(a => a.Name == name);

    /// <summary>
    /// The branch a pre-release was built from, null for releases of the default branch. Pre-releases are named
    /// <c>&lt;release name&gt;-&lt;branch&gt;.&lt;run number&gt;</c>, where anything but letters, digits and dashes in the
    /// branch name is replaced by a dash (see <c>release-name</c> in the fork's <c>.github/workflows/runner.yml</c>).
    /// </summary>
    [JsonIgnore]
    public string? Branch => Prerelease && PrereleaseTag().Match(Tag) is { Success: true } match ? match.Groups["branch"].Value : null;

    // Must match `PRERELEASE_TAG` in the fork's `misc/scripts/generate_delta_patches.py`.
    [GeneratedRegex(@"^\d+\.\d+\.\d+-(?<branch>[0-9A-Za-z-]+)\.\d+$")]
    private static partial Regex PrereleaseTag();
}

public sealed class ReleaseAsset
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string DownloadUrl { get; set; } = "";

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; set; }
}
