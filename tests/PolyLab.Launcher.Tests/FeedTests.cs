using System.Net;
using System.Net.Sockets;
using PolyLab.Launcher.Core;

namespace PolyLab.Launcher.Tests;

public class FeedTests
{
    [Fact]
    public void ParsesGitHubReleaseJson()
    {
        var list = Feed.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "github_releases.json")));
        Assert.Equal(2, list.Count);
        var r = list[0];
        Assert.Equal("v0.4.0", r.TagName);
        Assert.False(r.Draft);
        Assert.False(r.Prerelease);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 16, 20, 59, TimeSpan.Zero), r.PublishedAt);
        Assert.Equal("https://github.com/KRdayzmodding/KR_GRAFTED/releases/tag/v0.4.0", r.HtmlUrl);
        var a = Assert.Single(r.Assets);
        Assert.Equal("graft-v0.4.0-windows-x64.zip", a.Name);
        Assert.Equal(790109, a.Size);
        Assert.Equal("sha256:b366881dc54b2fb9298b7265fa0aacc6541fa6ce27b4bb293a8d44af2841cad1", a.Digest);
        Assert.Equal("https://github.com/KRdayzmodding/KR_GRAFTED/releases/download/v0.4.0/graft-v0.4.0-windows-x64.zip", a.BrowserDownloadUrl);
    }

    [Theory]
    [InlineData("pbog-v0.2.0", "pbog", "0.2.0")]
    [InlineData("pfx-v0.2.0-beta.1", "pfx", "0.2.0-beta.1")]
    [InlineData("launcher-v1.0.0", "launcher", "1.0.0")]
    public void ParsesTags(string tag, string id, string version)
    {
        Assert.True(Feed.TryParseTag(tag, out var gotId, out var v));
        Assert.Equal(id, gotId);
        Assert.Equal(version, v.ToString());
    }

    [Theory]
    [InlineData("v0.4.0")]
    [InlineData("pbog-0.2.0")]
    [InlineData("pbog-v0.2")]
    [InlineData("news-october")]
    public void RejectsOtherTags(string tag) => Assert.False(Feed.TryParseTag(tag, out _, out _));

    [Fact]
    public void SemVerOrder()
    {
        string[] sorted = ["0.2.0-beta.1", "0.2.0-beta.2", "0.2.0-beta.10", "0.2.0-rc.1", "0.2.0", "0.9.0", "0.10.0", "1.0.0"];
        var parsed = sorted.Select(s => { Assert.True(SemVer.TryParse(s, out var v)); return v; }).ToList();
        for (var i = 1; i < parsed.Count; i++) Assert.True(parsed[i].CompareTo(parsed[i - 1]) > 0, $"{sorted[i]} > {sorted[i - 1]}");
        Assert.True(SemVer.TryParse("v0.4.0", out var g));
        Assert.Equal("0.4.0", g.ToString());
    }

    [Fact]
    public async Task PicksNewestNonDraftAndMarksPrerelease()
    {
        await using var gh = await FakeGitHub.StartAsync();
        gh.Release("pbog-v0.1.2");
        gh.Release("pbog-v0.2.0");
        gh.Release("pbog-v0.3.0", draft: true);
        gh.Release("pfx-v0.1.0");
        gh.Release("pfx-v0.2.0-beta.1", prerelease: true);
        gh.Release("news-october", body: "Hello");
        using var s = new Scratch();
        var r = await new ReleasesClient(Net.Http, gh.ListUrl, Path.Combine(s.Dir, "feed.json")).FetchAsync();
        Assert.Equal(FeedState.Fresh, r.State);
        Assert.Equal(6, r.Releases.Count);
        var latest = Feed.Latest(r.Releases);
        Assert.Equal("0.2.0", latest["pbog"].Version.ToString());
        Assert.False(latest["pbog"].Beta);
        Assert.Equal("0.2.0-beta.1", latest["pfx"].Version.ToString());
        Assert.True(latest["pfx"].Beta);
        Assert.Equal(2, latest.Count);
        Assert.Equal(59, r.Remaining);
    }

    [Fact]
    public async Task SecondRequestIsConditionalAndReusesTheBody()
    {
        await using var gh = await FakeGitHub.StartAsync();
        gh.Release("pbog-v0.2.0");
        using var s = new Scratch();
        var cache = Path.Combine(s.Dir, "feed.json");
        var first = await new ReleasesClient(Net.Http, gh.ListUrl, cache).FetchAsync();
        Assert.Equal(FeedState.Fresh, first.State);

        var second = await new ReleasesClient(Net.Http, gh.ListUrl, cache).FetchAsync();
        Assert.Equal(FeedState.NotModified, second.State);
        Assert.Equal(1, gh.NotModified);
        Assert.Equal("pbog-v0.2.0", Assert.Single(second.Releases).TagName);

        gh.Release("pbog-v0.3.0");
        var third = await new ReleasesClient(Net.Http, gh.ListUrl, cache).FetchAsync();
        Assert.Equal(FeedState.Fresh, third.State);
        Assert.Equal(2, third.Releases.Count);
        Assert.Equal(3, gh.ListCalls);
    }

    [Fact]
    public async Task RateLimitKeepsTheLastListAndSaysWhenItResets()
    {
        await using var gh = await FakeGitHub.StartAsync();
        gh.Release("pbog-v0.2.0");
        using var s = new Scratch();
        var client = new ReleasesClient(Net.Http, gh.ListUrl, Path.Combine(s.Dir, "feed.json"));
        var fresh = await client.FetchAsync();
        gh.RateLimited = true;
        var r = await client.FetchAsync();
        Assert.Equal(FeedState.RateLimited, r.State);
        Assert.False(r.Live);
        Assert.Equal(0, r.Remaining);
        Assert.NotNull(r.Reset);
        Assert.True(r.Reset > DateTimeOffset.UtcNow);
        Assert.Equal(fresh.Fetched, r.Fetched);
        Assert.Equal("pbog-v0.2.0", Assert.Single(r.Releases).TagName);
    }

    [Fact]
    public async Task OfflineReturnsTheLastListOrNothing()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var dead = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/repos/test/polylab/releases";
        listener.Stop();
        using var s = new Scratch();
        var cache = Path.Combine(s.Dir, "feed.json");

        var none = await new ReleasesClient(Net.Http, dead, cache).FetchAsync();
        Assert.Equal(FeedState.Offline, none.State);
        Assert.Empty(none.Releases);
        Assert.Null(none.Fetched);

        await using (var gh = await FakeGitHub.StartAsync())
        {
            gh.Release("pfx-v0.1.0");
            await new ReleasesClient(Net.Http, gh.ListUrl, cache).FetchAsync();
        }
        var cached = await new ReleasesClient(Net.Http, dead, cache).FetchAsync();
        Assert.Equal(FeedState.Offline, cached.State);
        Assert.Equal("pfx-v0.1.0", Assert.Single(cached.Releases).TagName);
        Assert.NotNull(cached.Fetched);
    }

    [Fact]
    public async Task MissingRepoIsA404Failure()
    {
        await using var gh = await FakeGitHub.StartAsync();
        using var s = new Scratch();
        var r = await new ReleasesClient(Net.Http, gh.Base + "/repos/nobody/none/releases", Path.Combine(s.Dir, "feed.json")).FetchAsync();
        Assert.Equal(FeedState.Failed, r.State);
        Assert.Equal(404, r.Code);
    }
}
