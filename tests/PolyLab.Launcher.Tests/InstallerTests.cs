using System.Diagnostics;
using PolyLab.Launcher.Core;

namespace PolyLab.Launcher.Tests;

// Release zips of the real tools, built once per test run from the solution's build output.
public sealed class RealBuilds
{
    private readonly Dictionary<string, byte[]> _zips = new();

    public byte[] Pbog(string version) => Zip("pbog", version, "src/PBOGPacker", "PBOG_Packer.exe", "v0.4.0");
    public byte[] Pfx(string version) => Zip("pfx", version, "src/ParticleStudio/Pfx.App", "Particle_Studio.exe", null);

    private byte[] Zip(string id, string version, string project, string exe, string? graft)
    {
        lock (_zips)
        {
            var key = id + version;
            if (!_zips.TryGetValue(key, out var z))
                _zips[key] = z = Packages.FromFolder(Packages.BuildOutput(project, exe), Packages.Manifest(id, version, exe, graft));
            return z;
        }
    }
}

public sealed class Collect : IProgress<double>
{
    public readonly List<double> Values = new();
    public void Report(double value) => Values.Add(value);
}

public class InstallerTests(RealBuilds real) : IClassFixture<RealBuilds>
{
    private static readonly ToolInfo Pbog = Catalog.Find("pbog")!;
    private static readonly ToolInfo Pfx = Catalog.Find("pfx")!;

    private static async Task<Offer> OfferAsync(FakeGitHub gh, Scratch s, string id) =>
        Feed.Latest((await new ReleasesClient(Net.Http, gh.ListUrl, Path.Combine(s.Dir, "feed.json")).FetchAsync()).Releases)[id];

    private static byte[] Synthetic(string version, string id = "pbog", string exe = "PBOG_Packer.exe", byte[]? exeBytes = null) =>
        Packages.FromFiles(("tool.json", Packages.Text(Packages.Manifest(id, version, exe))), ("PBOG_Packer.exe", exeBytes ?? Packages.Text("MZ")));

    private static void AssertOnlyInstalled(string root, params string[] ids) =>
        Assert.Equal(ids.Order(), Directory.GetFileSystemEntries(Path.Combine(root, "tools")).Select(Path.GetFileName).Order());

    [Fact]
    public async Task InstallsAndUpdatesRealPbog()
    {
        await using var gh = await FakeGitHub.StartAsync();
        using var s = new Scratch();
        var root = s.Sub("root");
        var inst = new Installer(Net.Http, root);
        gh.Release("pbog-v0.2.0", real.Pbog("0.2.0"), "PBOG_Packer-0.2.0-win-x64.zip");
        var progress = new Collect();

        var r = await inst.InstallToolAsync(Pbog, await OfferAsync(gh, s, "pbog"), progress);

        Assert.True(r.Ok, r.Error + " " + r.Detail);
        Assert.Equal(new ToolManifest("pbog", "0.2.0", "PBOG_Packer.exe", "v0.4.0"), inst.Installed("pbog"));
        Assert.True(File.Exists(Path.Combine(root, "tools", "pbog", "PBOG_Packer.exe")));
        Assert.Equal(1, progress.Values[^1]);

        gh.Release("pbog-v0.3.0", real.Pbog("0.3.0"), "PBOG_Packer-0.3.0-win-x64.zip");
        r = await inst.InstallToolAsync(Pbog, await OfferAsync(gh, s, "pbog"));

        Assert.True(r.Ok, r.Error + " " + r.Detail);
        Assert.Equal("0.3.0", inst.Installed("pbog")?.Version);
        AssertOnlyInstalled(root, "pbog");
    }

    [Fact]
    public async Task InstallsRealParticleStudio()
    {
        await using var gh = await FakeGitHub.StartAsync();
        using var s = new Scratch();
        var root = s.Sub("root");
        var inst = new Installer(Net.Http, root);
        gh.Release("pfx-v0.1.0", real.Pfx("0.1.0"), "Particle_Studio-0.1.0-win-x64.zip");

        var r = await inst.InstallToolAsync(Pfx, await OfferAsync(gh, s, "pfx"));

        Assert.True(r.Ok, r.Error + " " + r.Detail);
        Assert.Equal("0.1.0", inst.Installed("pfx")?.Version);
        Assert.Null(inst.Installed("pfx")?.Graft);
        Assert.True(File.Exists(inst.ExePath(Pfx)));
        AssertOnlyInstalled(root, "pfx");
    }

    [Fact]
    public async Task DigestMismatchKeepsTheInstalledVersion()
    {
        await using var gh = await FakeGitHub.StartAsync();
        using var s = new Scratch();
        var root = s.Sub("root");
        var inst = new Installer(Net.Http, root);
        gh.Release("pbog-v0.2.0", Synthetic("0.2.0"), "PBOG_Packer-0.2.0-win-x64.zip");
        Assert.True((await inst.InstallToolAsync(Pbog, await OfferAsync(gh, s, "pbog"))).Ok);
        gh.Release("pbog-v0.3.0", Synthetic("0.3.0"), "PBOG_Packer-0.3.0-win-x64.zip", digest: FakeGitHub.Digest(Packages.Text("other bytes")));

        var r = await inst.InstallToolAsync(Pbog, await OfferAsync(gh, s, "pbog"));

        Assert.Equal(InstallError.Digest, r.Error);
        Assert.Equal("0.2.0", inst.Installed("pbog")?.Version);
        AssertOnlyInstalled(root, "pbog");
    }

    [Fact]
    public async Task AssetWithoutDigestIsRefused()
    {
        await using var gh = await FakeGitHub.StartAsync();
        using var s = new Scratch();
        var inst = new Installer(Net.Http, s.Sub("root"));
        gh.Release("pbog-v0.2.0", Synthetic("0.2.0"), "PBOG_Packer-0.2.0-win-x64.zip", digest: "");

        var r = await inst.InstallToolAsync(Pbog, await OfferAsync(gh, s, "pbog"));

        Assert.Equal(InstallError.NoDigest, r.Error);
        Assert.Null(inst.Installed("pbog"));
    }

    [Fact]
    public async Task ReleaseWithoutThePackageIsReported()
    {
        await using var gh = await FakeGitHub.StartAsync();
        using var s = new Scratch();
        var inst = new Installer(Net.Http, s.Sub("root"));
        gh.Release("pbog-v0.2.0", Synthetic("0.2.0"), "pbog.zip");

        var r = await inst.InstallToolAsync(Pbog, await OfferAsync(gh, s, "pbog"));

        Assert.Equal(InstallError.NoPackage, r.Error);
        Assert.Equal("PBOG_Packer-0.2.0-win-x64.zip", r.Detail);
    }

    [Theory]
    [InlineData("pfx", "0.2.0", "PBOG_Packer.exe")]
    [InlineData("pbog", "0.2.1", "PBOG_Packer.exe")]
    [InlineData("pbog", "0.2.0", "Other.exe")]
    [InlineData("pbog", "0.2.0", "..\\PBOG_Packer.exe")]
    public async Task WrongManifestIsRefused(string id, string version, string exe)
    {
        await using var gh = await FakeGitHub.StartAsync();
        using var s = new Scratch();
        var root = s.Sub("root");
        var inst = new Installer(Net.Http, root);
        gh.Release("pbog-v0.2.0", Synthetic(version, id, exe), "PBOG_Packer-0.2.0-win-x64.zip");

        var r = await inst.InstallToolAsync(Pbog, await OfferAsync(gh, s, "pbog"));

        Assert.Equal(InstallError.BadPackage, r.Error);
        Assert.Null(inst.Installed("pbog"));
        AssertOnlyInstalled(root);
    }

    // A copy of PING.EXE named like the tool stands in for a running tool; it runs with its working directory
    // elsewhere, so only the process check (not a folder lock) can stop the swap.
    [Fact]
    public async Task RunningToolBlocksUpdateAndRemove()
    {
        await using var gh = await FakeGitHub.StartAsync();
        using var s = new Scratch();
        var root = s.Sub("root");
        var inst = new Installer(Net.Http, root);
        var ping = File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "PING.EXE"));
        gh.Release("pbog-v0.2.0", Synthetic("0.2.0", exeBytes: ping), "PBOG_Packer-0.2.0-win-x64.zip");
        Assert.True((await inst.InstallToolAsync(Pbog, await OfferAsync(gh, s, "pbog"))).Ok);
        gh.Release("pbog-v0.3.0", Synthetic("0.3.0", exeBytes: ping), "PBOG_Packer-0.3.0-win-x64.zip");

        var psi = new ProcessStartInfo(inst.ExePath(Pbog), "-n 60 127.0.0.1") { WorkingDirectory = s.Dir, CreateNoWindow = true, UseShellExecute = false };
        using (var p = Process.Start(psi)!)
        {
            try
            {
                var seen = Stopwatch.StartNew();
                while (!Installer.IsRunning(inst.ExePath(Pbog)) && seen.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
                Assert.True(Installer.IsRunning(inst.ExePath(Pbog)));

                Assert.Equal(InstallError.Running, (await inst.InstallToolAsync(Pbog, await OfferAsync(gh, s, "pbog"))).Error);
                Assert.Equal(InstallError.Running, inst.Uninstall(Pbog).Error);
                Assert.Equal("0.2.0", inst.Installed("pbog")?.Version);
            }
            finally
            {
                p.Kill();
                await p.WaitForExitAsync();
            }
        }

        Assert.True(inst.Uninstall(Pbog).Ok);
        Assert.Null(inst.Installed("pbog"));
        AssertOnlyInstalled(root);
    }
}
