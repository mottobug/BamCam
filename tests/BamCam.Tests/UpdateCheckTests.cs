using BamCam.App.Services;

namespace BamCam.Tests;

public class UpdateCheckTests
{
    [Theory]
    [InlineData("0.6.0", "0.6.1", -1)]
    [InlineData("0.6.1", "0.7.0", -1)]
    [InlineData("v0.10.0", "0.9.9", 1)]
    [InlineData("1.0.0-beta.1", "1.0.0", -1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]
    [InlineData("0.6.0.0", "0.6.0", 0)]
    [InlineData("0.6.0+abc123", "0.6.0", 0)]
    public void Versions_compare_by_semantic_versioning(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(SemanticVersion.Parse(a).CompareTo(SemanticVersion.Parse(b))));
    }

    private const string Json = """
        [
          { "tag_name": "v0.8.0-beta.1", "html_url": "https://x/b", "body": "beta", "prerelease": true, "draft": false, "published_at": "2026-11-10T10:00:00Z" },
          { "tag_name": "v0.9.0", "html_url": "https://x/draft", "body": "", "prerelease": false, "draft": true },
          { "tag_name": "v0.7.0", "html_url": "https://x/7", "body": "## New\n- things", "prerelease": false, "draft": false, "published_at": "2026-11-05T10:00:00Z" },
          { "tag_name": "v0.6.0", "html_url": "https://x/6", "body": "", "prerelease": false, "draft": false, "published_at": "2026-11-01T11:14:57Z" },
          { "tag_name": "nightly", "html_url": "https://x/n", "body": "", "prerelease": false, "draft": false }
        ]
        """;

    [Fact]
    public void Newest_release_skips_drafts_and_prereleases_unless_wanted()
    {
        var releases = UpdateCheck.ParseReleases(Json);
        Assert.Equal(["0.8.0-beta.1", "0.7.0", "0.6.0"], releases.Select(r => r.Version)); // draft and "nightly" left out
        var stable = UpdateCheck.Newest(releases, includePrereleases: false)!;
        Assert.Equal(("0.7.0", "https://x/7"), (stable.Version, stable.PageUrl));
        Assert.Contains("things", stable.Notes);
        Assert.Equal("0.8.0-beta.1", UpdateCheck.Newest(releases, includePrereleases: true)!.Version);
    }

    [Fact]
    public void Update_only_when_newer_and_not_skipped()
    {
        var release = new ReleaseInfo("0.7.0", "", "", null, false);
        Assert.True(UpdateCheck.IsUpdate(release, "0.6.0", null));
        Assert.False(UpdateCheck.IsUpdate(release, "0.7.0", null));
        Assert.False(UpdateCheck.IsUpdate(release, "0.8.0", null));
        Assert.False(UpdateCheck.IsUpdate(release, "0.6.0", "0.7.0"));
        Assert.True(UpdateCheck.IsUpdate(release, "0.6.0", "0.6.5"));
    }
}
