using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace BamCam.App.Services;

/// <summary>A release on GitHub.</summary>
public sealed record ReleaseInfo(string Version, string PageUrl, string Notes, DateTimeOffset? Published, bool Prerelease);

/// <summary>
/// Looks up the newest BamCam release on GitHub. Only the release list is requested: nothing about the user or the
/// drawings is sent (GitHub sees the request with the user agent "BamCam/&lt;version&gt;").
/// </summary>
public static class UpdateCheck
{
    public const string ReleasesApi = "https://api.github.com/repos/mottobug/BamCam/releases?per_page=20";

    /// <summary>The newest release (prereleases only when wanted), or null; throws on network or format errors.</summary>
    public static async Task<ReleaseInfo?> FetchNewestAsync(string currentVersion, bool includePrereleases, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BamCam", currentVersion));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        var json = await http.GetStringAsync(ReleasesApi);
        return Newest(ParseReleases(json), includePrereleases);
    }

    /// <summary>Releases from GitHub's JSON list (drafts left out).</summary>
    public static List<ReleaseInfo> ParseReleases(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<ReleaseInfo>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            if (SemanticVersion.TryParse(tag, out _) == false) continue;
            DateTimeOffset? published = r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(p.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            result.Add(new ReleaseInfo(
                tag.TrimStart('v', 'V'),
                r.TryGetProperty("html_url", out var url) ? url.GetString() ?? "" : "",
                r.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" : "",
                published,
                r.TryGetProperty("prerelease", out var pre) && pre.GetBoolean() || tag.Contains('-')));
        }
        return result;
    }

    /// <summary>The highest version among the releases (prereleases only when wanted).</summary>
    public static ReleaseInfo? Newest(IEnumerable<ReleaseInfo> releases, bool includePrereleases) =>
        releases.Where(r => includePrereleases || !r.Prerelease)
            .OrderByDescending(r => SemanticVersion.Parse(r.Version))
            .FirstOrDefault();

    /// <summary>True when <paramref name="release"/> is newer than the running version and not skipped by the user.</summary>
    public static bool IsUpdate(ReleaseInfo release, string currentVersion, string? skippedVersion) =>
        SemanticVersion.Parse(release.Version).CompareTo(SemanticVersion.Parse(currentVersion)) > 0
        && !string.Equals(release.Version, skippedVersion, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Version "1.2.3" or "1.2.3-beta.1" compared by Semantic Versioning rules (build metadata ignored).</summary>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string Prerelease) : IComparable<SemanticVersion>
{
    public static SemanticVersion Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"Not a version: '{text}'.");

    public static bool TryParse(string text, out SemanticVersion version)
    {
        version = default;
        var s = text.Trim().TrimStart('v', 'V');
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var dash = s.IndexOf('-');
        var pre = dash >= 0 ? s[(dash + 1)..] : "";
        var parts = (dash >= 0 ? s[..dash] : s).Split('.');
        if (parts.Length is < 1 or > 4) return false;
        var numbers = new int[3];
        for (var i = 0; i < Math.Min(parts.Length, 3); i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        version = new SemanticVersion(numbers[0], numbers[1], numbers[2], pre);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        // A release is newer than its prereleases.
        if (Prerelease.Length == 0 && other.Prerelease.Length == 0) return 0;
        if (Prerelease.Length == 0) return 1;
        if (other.Prerelease.Length == 0) return -1;
        var a = Prerelease.Split('.');
        var b = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var na = int.TryParse(a[i], out var ia);
            var nb = int.TryParse(b[i], out var ib);
            c = na && nb ? ia.CompareTo(ib) : na ? -1 : nb ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Prerelease.Length > 0 ? "-" + Prerelease : "");
}
