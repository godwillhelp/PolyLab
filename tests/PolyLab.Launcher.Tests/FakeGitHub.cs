using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PolyLab.Launcher.Tests;

// Local stand-in for api.github.com: the release list of one repo (with ETag / 304 and rate-limit headers), the
// "release by tag" call of a GRAFT repo, and asset downloads. JSON is written with GitHub's own field names here,
// not through the launcher's DTO, so a wrong DTO name would fail the tests instead of agreeing with itself.
public sealed class FakeGitHub : IAsyncDisposable
{
    public const string ListPath = "/repos/test/polylab/releases";
    public const string GraftPath = "/repos/test/graft";

    private readonly WebApplication _app;
    private readonly List<object> _releases = new();
    private readonly Dictionary<string, object> _graft = new();
    private readonly Dictionary<string, byte[]> _files = new();
    private long _nextId = 1000;

    public string Base { get; }
    public string ListUrl => Base + ListPath + "?per_page=100";
    public string GraftApi => Base + GraftPath;
    public int ListCalls { get; private set; }
    public int NotModified { get; private set; }
    public bool RateLimited { get; set; }

    private FakeGitHub(WebApplication app)
    {
        _app = app;
        Base = app.Urls.First().TrimEnd('/');
    }

    public static async Task<FakeGitHub> StartAsync()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Logging.ClearProviders();
        var app = b.Build();
        FakeGitHub? fake = null;
        app.Run(ctx => fake!.Handle(ctx));
        await app.StartAsync();
        fake = new FakeGitHub(app);
        return fake;
    }

    public static string Digest(byte[] bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // Newest first, as GitHub lists them. digest: null = the real one, "" = none (asset uploaded before digests existed).
    public void Release(string tag, byte[]? zip = null, string? asset = null, bool prerelease = false, bool draft = false,
        string? digest = null, string body = "")
    {
        var assets = zip == null ? Array.Empty<object>() : [Asset(asset!, zip, digest)];
        var id = _nextId++;
        _releases.Insert(0, new
        {
            id,
            tag_name = tag,
            name = tag,
            body,
            draft,
            prerelease,
            published_at = draft ? null : (DateTimeOffset?)new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).AddHours(id - 1000),
            html_url = Base + "/releases/tag/" + tag,
            assets,
        });
    }

    public void GraftRelease(string tag, byte[] zip) =>
        _graft[tag] = new
        {
            id = _nextId++,
            tag_name = tag,
            name = tag,
            body = "",
            draft = false,
            prerelease = false,
            published_at = DateTimeOffset.UtcNow,
            html_url = Base + "/graft/" + tag,
            assets = new[] { Asset($"graft-{tag}-windows-x64.zip", zip, null) },
        };

    private object Asset(string name, byte[] bytes, string? digest)
    {
        _files[name] = bytes;
        return new
        {
            name,
            size = bytes.LongLength,
            browser_download_url = Base + "/dl/" + name,
            digest = digest == "" ? null : digest ?? Digest(bytes),
            content_type = "application/zip",
        };
    }

    private async Task Handle(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        var resp = ctx.Response;
        if (path == ListPath)
        {
            ListCalls++;
            resp.Headers["x-ratelimit-limit"] = "60";
            resp.Headers["x-ratelimit-reset"] = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds().ToString();
            if (RateLimited)
            {
                resp.Headers["x-ratelimit-remaining"] = "0";
                resp.StatusCode = 403;
                await resp.WriteAsync("{\"message\":\"API rate limit exceeded\"}");
                return;
            }
            resp.Headers["x-ratelimit-remaining"] = (60 - ListCalls).ToString();
            var body = JsonSerializer.Serialize(_releases);
            var etag = "W/\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..16] + "\"";
            if (ctx.Request.Headers.IfNoneMatch == etag)
            {
                NotModified++;
                resp.StatusCode = 304;
                return;
            }
            resp.Headers.ETag = etag;
            resp.ContentType = "application/json; charset=utf-8";
            await resp.WriteAsync(body);
            return;
        }
        if (path.StartsWith(GraftPath + "/releases/tags/") && _graft.TryGetValue(Uri.UnescapeDataString(path[(GraftPath.Length + 15)..]), out var rel))
        {
            resp.ContentType = "application/json; charset=utf-8";
            await resp.WriteAsync(JsonSerializer.Serialize(rel));
            return;
        }
        if (path.StartsWith("/dl/") && _files.TryGetValue(path[4..], out var file))
        {
            resp.ContentType = "application/octet-stream";
            resp.ContentLength = file.Length;
            await resp.Body.WriteAsync(file);
            return;
        }
        resp.StatusCode = 404;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
