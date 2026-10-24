using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Pfx.Core.Export;
using Pfx.Core.Model;
using Pfx.Core.Text;
using Pfx.Link;

namespace Pfx.Tests;

// P11: the live link without a game — the HTTP endpoint, the long poll with acknowledged commands, the diff that picks
// "set" or "reload", and the content-named stage.
public sealed class LinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfx_link_" + Guid.NewGuid().ToString("N"));

    public LinkTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<string> Post(int port, string body, bool expectContinue = false)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}{LinkServer.Route}") { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
        if (expectContinue)
            req.Headers.ExpectContinue = true;
        var res = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task ServerEchoesBodiesWithAndWithoutExpectContinue()
    {
        using var server = new LinkServer((b, _) => Task.FromResult("echo:" + b));
        server.Start(0);
        string big = new('x', 5000);
        Assert.Equal("echo:abc", await Post(server.Port, "abc"));
        Assert.Equal("echo:" + big, await Post(server.Port, big, true));
        using var http = new HttpClient();
        var other = await http.GetAsync($"http://127.0.0.1:{server.Port}/other");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Fact]
    public async Task HubAsksUnknownClientsForHelloThenDeliversQueuedCommandsUntilApplied()
    {
        using var hub = new LinkHub(null);
        int port = FreePort();
        hub.Start(port);
        LinkClient? client = null;
        hub.ClientChanged += c => client = c;
        Assert.Equal("0 hello", await Post(port, "c s1 0\n"));
        int a = hub.Send("mark cam 6");
        int b = hub.Send("spawn 1 x.ptc");
        string first = await Post(port, "c s1 0\nhello 1 Tester 1 1 1920 1080 C:\\My Profiles\\p\n");
        Assert.NotNull(client);
        Assert.Equal("C:\\My Profiles\\p", client!.Profiles);
        Assert.True(client.FilePatching && client.AutoTest);
        Assert.Equal(LinkState.Connected, hub.State);
        // The hello made s1 the client in use and dropped what was queued before it.
        Assert.Equal(LinkHub.Nothing, first);
        await Post(port, "c s1 0\nhello 1 Tester 1 1 1920 1080 -\nparams unknown CONEANGLE VELOCITY\n");
        Assert.Equal(2, client!.ParamIndex("VELOCITY"));
        Assert.Equal(-1, client.ParamIndex("SPRING"));
        int c = hub.Send("set 1 0 2 f 3");
        int d = hub.Send("set 1 0 2 f 4", "v");
        int e = hub.Send("set 1 0 2 f 5", "v");
        Assert.Equal(d, e);
        Assert.True(a < c && b < c);
        Assert.Equal($"{c} set 1 0 2 f 3\n{d} set 1 0 2 f 5", await Post(port, "c s1 0\n"));
        // Not applied: sent again; applied: gone.
        Assert.Equal($"{d} set 1 0 2 f 5", await Post(port, $"c s1 {c}\n"));
    }

    // P11 in game: a mod that outlived an earlier tool session reports a high "applied"; a new tool's commands must be
    // numbered above it, or both sides treat them as done.
    [Fact]
    public async Task NumberingContinuesAboveWhatANewClientHasApplied()
    {
        using var hub = new LinkHub(null);
        int port = FreePort();
        hub.Start(port);
        Assert.Equal("0 hello", await Post(port, "c old 140\n"));
        hub.ClientChanged += _ => hub.Send("spawn 1 a.ptc");
        string answer = await Post(port, "c old 140\nhello 1 T 1 1 1 1 -\n");
        Assert.Equal("141 spawn 1 a.ptc", answer);
    }

    [Fact]
    public async Task AKnownClientTakesOverOnlyAfterTheActiveOneIsLost()
    {
        using var hub = new LinkHub(null);
        int port = FreePort();
        hub.Start(port);
        await Post(port, "c a 0\nhello 1 A 1 1 1 1 -\n");
        await Post(port, "c b 0\nhello 1 B 1 1 1 1 -\n");
        Assert.Equal("B", hub.Client!.Name);
        // B keeps polling (its poll is held while A asks): A is known but must not take over.
        var bPoll = Post(port, "c b 0\n");
        await Task.Delay(200);
        var clock = Stopwatch.StartNew();
        Assert.Equal(LinkHub.Nothing, await Post(port, "c a 0\n"));
        Assert.True(clock.Elapsed >= LinkHub.Hold - TimeSpan.FromMilliseconds(100));
        Assert.Equal("B", hub.Client!.Name);
        await bPoll;
    }

    [Fact]
    public async Task LongPollAnswersAsSoonAsACommandIsQueued()
    {
        using var hub = new LinkHub(null);
        int port = FreePort();
        hub.Start(port);
        await Post(port, "c s1 0\nhello 1 T 1 1 800 600 -\n");
        var clock = Stopwatch.StartNew();
        var poll = Post(port, "c s1 0\n");
        await Task.Delay(300);
        int seq = hub.Send("clear");
        Assert.Equal($"{seq} clear", await poll);
        Assert.InRange(clock.ElapsedMilliseconds, 250, (long)LinkHub.Hold.TotalMilliseconds - 1000);
    }

    [Fact]
    public async Task EventLinesReachTheToolInOrder()
    {
        using var hub = new LinkHub(null);
        int port = FreePort();
        hub.Start(port);
        var got = new List<string>();
        hub.Message += t => got.Add(string.Join(" ", t));
        hub.Send("x");
        await Post(port, "c s9 0\nhello 1 T 0 0 1 1 -\nspawned 1 3 2 a b\ngot 4 1 0 2 3.5\n");
        Assert.Equal(new[] { "spawned 1 3 2 a b", "got 4 1 0 2 3.5" }, got);
    }

    private static PtcDocument Ptc(string emitterBody, string second = "") =>
        PtcDocument.From(EnfDocument.Parse("EffectDef {\r\n Emitors {\r\n  EmitorDef a {\r\n" + emitterBody + "  }\r\n" + second + " }\r\n}\r\n"));

    [Fact]
    public void DiffSendsLiveParametersAndReloadsEverythingElse()
    {
        var live = Ptc("   Velocity 2\r\n   ConeAngle 10 0 20\r\n   Size 0 1 1 1\r\n   Wind 0\r\n");
        Assert.True(LiveDiff.Compare(live, Ptc("   Velocity  2\r\n   ConeAngle 10 0 20\r\n   Size 0 1 1 1\r\n   Wind 0\r\n")).Same);
        var d = LiveDiff.Compare(live, Ptc("   Velocity 3.5\r\n   ConeAngle 10 0 20\r\n   Size 0 1 1 1\r\n   Wind true\r\n"));
        Assert.False(d.Reload);
        Assert.Equal(new[] { "f 3.5", "b 1" }, d.Changes.Select(c => c.Arguments));
        // Engine units (P11, read back in game): the cone as (spread, polar / 2, azimuth / 2), the size halved, stretch - 1.
        Assert.Equal("v 10 22.5 0", Assert.Single(LiveDiff.Compare(live, Ptc("   Velocity 2\r\n   ConeAngle 10 0 45\r\n   Size 0 1 1 1\r\n   Wind 0\r\n")).Changes).Arguments);
        Assert.Equal("f 0.25", Assert.Single(LiveDiff.Compare(Ptc("   SizeMultiplier 1\r\n"), Ptc("   SizeMultiplier 0.5\r\n")).Changes).Arguments);
        Assert.Equal("f 0.5", Assert.Single(LiveDiff.Compare(Ptc("   StretchMultiplier 1\r\n"), Ptc("   StretchMultiplier 1.5\r\n")).Changes).Arguments);
        // A removed key goes back to the engine default (Velocity 2 in the schema).
        var removed = LiveDiff.Compare(Ptc("   Velocity 7\r\n"), Ptc(""));
        Assert.Equal("f 2", Assert.Single(removed.Changes).Arguments);
        Assert.True(LiveDiff.Compare(live, Ptc("   Velocity 2\r\n   ConeAngle 10 0 20\r\n   Size 0 1 1 0.5\r\n   Wind 0\r\n")).Reload);
        Assert.True(LiveDiff.Compare(Ptc("   ShapeSize 1 1 1\r\n"), Ptc("   ShapeSize 2 1 1\r\n")).Reload);
        Assert.True(LiveDiff.Compare(Ptc("   Velocity 2\r\n"), Ptc("   Velocity 2\r\n", "  EmitorDef b {\r\n  }\r\n")).Reload);
        Assert.True(LiveDiff.Compare(Ptc("   Velocity 2\r\n   Velocity 3\r\n"), Ptc("   Velocity 2\r\n   Velocity 4\r\n")).Reload);
        Assert.True(LiveDiff.Compare(Ptc("   Velocity 2\r\n"), Ptc("   Velocity fast\r\n")).Reload);
    }

    [Fact]
    public void ReadBackComparisonAllowsFloatPrecision()
    {
        Assert.True(LiveLink.SameNumbers("f 2.25", "2.250000"));
        Assert.False(LiveLink.SameNumbers("f 2.25", "2.5"));
        Assert.True(LiveLink.SameNumbers("b 1", "1"));
    }

    private sealed class Source : IExportSource
    {
        private readonly string _root;
        public Source(string root) => _root = root;
        public string? Resolve(string gamePath)
        {
            string p = gamePath.StartsWith('{') ? gamePath[(gamePath.IndexOf('}') + 1)..] : gamePath;
            string f = Path.Combine(_root, p.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(f) ? f : null;
        }
        public byte[] Read(string diskPath) => File.ReadAllBytes(diskPath);
        public string? GamePath(string diskFolder) => null;
    }

    [Fact]
    public void StageNamesFilesByContentAndCleansUp()
    {
        string data = Path.Combine(_dir, "data");
        Directory.CreateDirectory(Path.Combine(data, "mod", "fx"));
        File.WriteAllText(Path.Combine(data, "mod", "fx", "glow.emat"), "ParticleEffectMaterial {\r\n ColorMap \"mod/fx/glow.edds\"\r\n}\r\n", Encoding.Latin1);
        File.WriteAllBytes(Path.Combine(data, "mod", "fx", "glow.edds"), new byte[] { 1, 2, 3, 4 });
        string game = Path.Combine(_dir, "game");
        var src = new Source(data);
        byte[] v1 = Encoding.Latin1.GetBytes("EffectDef {\r\n Emitors {\r\n  EmitorDef a {\r\n   Material \"mod/fx/glow.emat\"\r\n   Velocity 1\r\n  }\r\n }\r\n}\r\n");
        byte[] v2 = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(v1).Replace("Velocity 1", "Velocity 2"));
        string folder;
        using (var stage = new LiveStage(game))
        {
            folder = stage.Folder;
            var a = stage.Stage("My Part", v1, src);
            Assert.StartsWith("NDZ_PfxLink/live/" + stage.Session + "/my_part_", a.GamePath);
            Assert.Equal(3, a.NewFiles);
            Assert.Empty(a.Missing);
            var again = stage.Stage("My Part", v1, src);
            Assert.Equal(a.GamePath, again.GamePath);
            Assert.Equal(0, again.NewFiles);
            var b = stage.Stage("My Part", v2, src);
            Assert.NotEqual(a.GamePath, b.GamePath);
            Assert.Equal(1, b.NewFiles);
            string staged = File.ReadAllText(Path.Combine(stage.Folder, b.GamePath.Split('/')[^1]), Encoding.Latin1);
            Assert.Contains("\"NDZ_PfxLink/live/" + stage.Session + "/materials/glow_", staged);
            string stamp = LiveStage.Stamp(v2, src);
            File.WriteAllBytes(Path.Combine(data, "mod", "fx", "glow.edds"), new byte[] { 9, 9, 9, 9, 9 });
            Assert.NotEqual(stamp, LiveStage.Stamp(v2, src));
            var c = stage.Stage("My Part", v2, src);
            Assert.NotEqual(b.GamePath, c.GamePath);
            Assert.Equal(3, c.NewFiles);
            Assert.Equal(0, LiveStage.RemoveStale(game, null));
        }
        Assert.False(Directory.Exists(folder));
        Directory.CreateDirectory(Path.Combine(game, "NDZ_PfxLink", "live", "s_old"));
        Assert.Equal(1, LiveStage.RemoveStale(game));
    }
}
