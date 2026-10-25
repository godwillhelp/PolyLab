using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace PolyLab.Launcher.Core;

// The fields of a GitHub REST release the launcher reads (snake_case on the wire).
public sealed record GhAsset(string Name, long Size, string BrowserDownloadUrl, string? Digest);

public sealed record GhRelease(long Id, string TagName, string? Name, string? Body, bool Draft, bool Prerelease,
    DateTimeOffset? PublishedAt, string HtmlUrl, GhAsset[] Assets);

// major.minor.patch with an optional -prerelease; build metadata is ignored. A leading v is accepted (GRAFT tags).
public readonly record struct SemVer(Version Core, string Pre) : IComparable<SemVer>
{
    public static bool TryParse(string? text, out SemVer v)
    {
        v = default;
        var s = (text ?? "").Trim().TrimStart('v', 'V');
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var dash = s.IndexOf('-');
        var core = dash >= 0 ? s[..dash] : s;
        if (!Version.TryParse(core, out var ver) || ver.Build < 0 || ver.Revision >= 0) return false;
        v = new SemVer(ver, dash >= 0 ? s[(dash + 1)..] : "");
        return true;
    }

    // SemVer precedence: a prerelease sorts below its release; dot-separated identifiers compare numerically
    // when both are numbers (beta.10 > beta.2).
    public int CompareTo(SemVer o)
    {
        var c = Core.CompareTo(o.Core);
        if (c != 0) return c;
        c = (Pre.Length == 0).CompareTo(o.Pre.Length == 0);
        if (c != 0 || Pre.Length == 0) return c;
        var a = Pre.Split('.');
        var b = o.Pre.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            c = long.TryParse(a[i], out var x) && long.TryParse(b[i], out var y) ? x.CompareTo(y) : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    public override string ToString() => Core.ToString(3) + (Pre.Length > 0 ? "-" + Pre : "");
}

public sealed record Offer(string Id, SemVer Version, GhRelease Release)
{
    public bool Beta => Release.Prerelease;
}

public static class Feed
{
    // <id>-v<semver>: pbog-v0.2.0, pfx-v0.2.0-beta.1, launcher-v1.0.0.
    public static bool TryParseTag(string tag, out string id, out SemVer version)
    {
        id = "";
        version = default;
        var cut = tag.IndexOf("-v", StringComparison.Ordinal);
        if (cut <= 0) return false;
        id = tag[..cut];
        return SemVer.TryParse(tag[(cut + 2)..], out version);
    }

    // Newest non-draft release per id; a prerelease is offered too and shown as Beta.
    public static Dictionary<string, Offer> Latest(IEnumerable<GhRelease> releases)
    {
        var best = new Dictionary<string, Offer>();
        foreach (var r in releases)
        {
            if (r.Draft || !TryParseTag(r.TagName, out var id, out var v)) continue;
            if (!best.TryGetValue(id, out var cur) || v.CompareTo(cur.Version) > 0) best[id] = new Offer(id, v, r);
        }
        return best;
    }

    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static List<GhRelease> Parse(string json) => JsonSerializer.Deserialize<List<GhRelease>>(json, Json) ?? [];
}

public enum FeedState { Fresh, NotModified, Offline, RateLimited, Failed }

// Releases as last known; Fetched is when that list came from GitHub (null: never).
public sealed record FeedResult(FeedState State, IReadOnlyList<GhRelease> Releases, DateTimeOffset? Fetched,
    int? Remaining = null, DateTimeOffset? Reset = null, int Code = 0, string Error = "")
{
    public bool Live => State is FeedState.Fresh or FeedState.NotModified;
}

public sealed record FeedCache(string ETag, DateTimeOffset Fetched, string Body);

// One GET of the repo's release list. The last body and its ETag are kept on disk: the next request is
// conditional, a 304 (free against GitHub's 60-per-hour anonymous limit) reuses the body, and every failure
// still returns the last known list.
public sealed class ReleasesClient(HttpClient http, string url, string cacheFile)
{
    public async Task<FeedResult> FetchAsync(CancellationToken ct = default)
    {
        var (cache, cached) = LoadCache();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (cache is { ETag.Length: > 0 }) req.Headers.TryAddWithoutValidation("If-None-Match", cache.ETag);
        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, ct);
        }
        catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new(FeedState.Offline, cached, cache?.Fetched, Error: e.Message);
        }
        using (resp)
        {
            int? remaining = Header(resp, "x-ratelimit-remaining") is { } r && int.TryParse(r, out var n) ? n : null;
            DateTimeOffset? reset = Header(resp, "x-ratelimit-reset") is { } s && long.TryParse(s, out var t)
                ? DateTimeOffset.FromUnixTimeSeconds(t) : null;
            var code = (int)resp.StatusCode;
            var now = DateTimeOffset.UtcNow;
            if (resp.StatusCode == HttpStatusCode.NotModified && cache != null)
            {
                Save(cache with { Fetched = now });
                return new(FeedState.NotModified, cached, now, remaining, reset, code);
            }
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                List<GhRelease> list;
                try { list = Feed.Parse(body); }
                catch (JsonException e) { return new(FeedState.Failed, cached, cache?.Fetched, remaining, reset, code, e.Message); }
                Save(new FeedCache(resp.Headers.ETag?.ToString() ?? "", now, body));
                return new(FeedState.Fresh, list, now, remaining, reset, code);
            }
            var state = remaining == 0 && resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                ? FeedState.RateLimited : FeedState.Failed;
            return new(state, cached, cache?.Fetched, remaining, reset, code, "HTTP " + code);
        }
    }

    private static string? Header(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;

    private (FeedCache?, IReadOnlyList<GhRelease>) LoadCache()
    {
        try
        {
            if (File.Exists(cacheFile) && JsonSerializer.Deserialize<FeedCache>(File.ReadAllText(cacheFile)) is { } c)
                return (c, Feed.Parse(c.Body));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return (null, []);
    }

    private void Save(FeedCache c)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            File.WriteAllText(cacheFile, JsonSerializer.Serialize(c));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
