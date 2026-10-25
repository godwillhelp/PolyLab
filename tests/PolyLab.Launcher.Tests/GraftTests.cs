using PolyLab.Launcher.Core;

namespace PolyLab.Launcher.Tests;

public class GraftTests
{
    private static byte[] GraftZip(bool withHost = true) => withHost
        ? Packages.FromFiles(("graft.exe", Packages.Text("MZ")), ("hid.dll", Packages.Text("MZ")), ("LICENSE", Packages.Text("GPL")))
        : Packages.FromFiles(("graft.exe", Packages.Text("MZ")), ("LICENSE", Packages.Text("GPL")));

    [Fact]
    public async Task InstallsThePinnedReleaseAndSwitchesVersions()
    {
        await using var gh = await FakeGitHub.StartAsync();
        using var s = new Scratch();
        var root = s.Sub("root");
        var inst = new Installer(Net.Http, root);
        gh.GraftRelease("v0.4.0", GraftZip());
        gh.GraftRelease("v0.4.1", GraftZip());

        var r = await inst.InstallGraftAsync(gh.GraftApi, "v0.4.0");

        Assert.True(r.Ok, r.Error + " " + r.Detail);
        Assert.Equal("v0.4.0", inst.InstalledGraft());
        Assert.True(File.Exists(Path.Combine(root, "graft", "GRAFT_SOURCE.txt")));
        Assert.True(File.Exists(Path.Combine(root, "graft", "LICENSE")));

        Assert.True((await inst.InstallGraftAsync(gh.GraftApi, "v0.4.1")).Ok);
        Assert.Equal("v0.4.1", inst.InstalledGraft());
        Assert.Equal(new[] { "graft" }, Directory.GetFileSystemEntries(root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task UnknownTagIsNotFound()
    {
        await using var gh = await FakeGitHub.StartAsync();
        using var s = new Scratch();
        var inst = new Installer(Net.Http, s.Sub("root"));

        var r = await inst.InstallGraftAsync(gh.GraftApi, "v9.9.9");

        Assert.Equal(InstallError.NotFound, r.Error);
        Assert.Null(inst.InstalledGraft());
    }

    [Fact]
    public async Task ZipWithoutTheHostDllIsRefused()
    {
        await using var gh = await FakeGitHub.StartAsync();
        using var s = new Scratch();
        var inst = new Installer(Net.Http, s.Sub("root"));
        gh.GraftRelease("v0.4.0", GraftZip(withHost: false));

        var r = await inst.InstallGraftAsync(gh.GraftApi, "v0.4.0");

        Assert.Equal(InstallError.BadPackage, r.Error);
        Assert.Null(inst.InstalledGraft());
    }

    [EnvFact("POLYLAB_NET")]
    public async Task RealUpstreamRelease()
    {
        using var s = new Scratch();
        var root = s.Sub("root");
        var inst = new Installer(Net.Http, root);

        var r = await inst.InstallGraftAsync(Catalog.GraftApi, "v0.4.0");

        Assert.True(r.Ok, r.Error + " " + r.Detail);
        Assert.Equal("v0.4.0", inst.InstalledGraft());
        Assert.Equal(865792, new FileInfo(Path.Combine(root, "graft", "graft.exe")).Length);
        Assert.True(File.Exists(Path.Combine(root, "graft", "hid.dll")));
        Assert.True(File.Exists(Path.Combine(root, "graft", "LICENSE-EXCEPTION")));
    }
}
