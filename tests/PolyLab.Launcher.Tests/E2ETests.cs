using System.Diagnostics;
using PolyLab.Launcher.Core;

namespace PolyLab.Launcher.Tests;

// The built PolyLab.exe against the fake release list: installs both real tools, takes GRAFT from the real upstream
// (pinned by PBOG Packer's tool.json), launches PBOG Packer's doctor through the launcher, updates PBOG Packer and
// rejects a release with a wrong digest. Opens windows; POLYLAB_E2E_SHOTS keeps the launcher shots.
public class E2ETests(RealBuilds real) : IClassFixture<RealBuilds>
{
    [EnvFact("POLYLAB_E2E")]
    public async Task LauncherInstallsUpdatesRejectsAndLaunches()
    {
        await using var gh = await FakeGitHub.StartAsync();
        using var s = new Scratch();
        string root = s.Sub("root"), data = s.Sub("data"), state = s.Sub("pbog-state");
        var shots = Environment.GetEnvironmentVariable("POLYLAB_E2E_SHOTS") is { Length: > 0 } keep ? keep : s.Sub("shots");
        var inst = new Installer(Net.Http, root);
        gh.Release("pfx-v0.1.0", real.Pfx("0.1.0"), "Particle_Studio-0.1.0-win-x64.zip", body: "First build.");
        gh.Release("pbog-v0.2.0", real.Pbog("0.2.0"), "PBOG_Packer-0.2.0-win-x64.zip", body: "## Changes\n- Batch packing");
        var doctor = Path.Combine(shots, "e2e_pbog_doctor.txt");
        File.Delete(doctor);

        await RunAsync(gh, root, data, state, Path.Combine(shots, "e2e_1_install.png"),
            "--install", "pbog,pfx", "--launch", "pbog", "--", "--doctor", doctor);

        Assert.Equal("0.2.0", inst.Installed("pbog")?.Version);
        Assert.Equal("0.1.0", inst.Installed("pfx")?.Version);
        Assert.Equal("v0.4.0", inst.InstalledGraft());
        var waited = Stopwatch.StartNew();
        while (!File.Exists(doctor) && waited.Elapsed < TimeSpan.FromMinutes(1)) await Task.Delay(200);
        var graftLine = File.ReadAllLines(doctor).Single(l => l.StartsWith("graft "));
        Assert.Contains("v0.4.0", graftLine);
        Assert.EndsWith(Path.Combine(root, "graft", "graft.exe"), graftLine);

        gh.Release("pbog-v0.3.0", real.Pbog("0.3.0"), "PBOG_Packer-0.3.0-win-x64.zip", body: "Fixes.");
        await RunAsync(gh, root, data, state, Path.Combine(shots, "e2e_2_update.png"), "--install", "pbog");
        Assert.Equal("0.3.0", inst.Installed("pbog")?.Version);

        gh.Release("pbog-v0.4.0", real.Pbog("0.4.0"), "PBOG_Packer-0.4.0-win-x64.zip", digest: FakeGitHub.Digest(Packages.Text("tampered")));
        await RunAsync(gh, root, data, state, Path.Combine(shots, "e2e_3_digest.png"), "--install", "pbog");
        Assert.Equal("0.3.0", inst.Installed("pbog")?.Version);
        Assert.Equal(new[] { "pbog", "pfx" }, Directory.GetFileSystemEntries(Path.Combine(root, "tools")).Select(Path.GetFileName).Order());
    }

    private static async Task RunAsync(FakeGitHub gh, string root, string data, string pbogState, string shot, params string[] extra)
    {
        var psi = new ProcessStartInfo(Path.Combine(Packages.BuildOutput("src/Launcher", "PolyLab.exe"), "PolyLab.exe")) { UseShellExecute = false };
        foreach (var a in new[] { "--root", root, "--data", data, "--shot", shot, "--size", "1920x1080", "--live" }.Concat(extra))
            psi.ArgumentList.Add(a);
        psi.Environment[Catalog.ReleasesUrlEnv] = gh.ListUrl;
        // PBOG Packer started through the launcher must not touch the user's own settings folder.
        psi.Environment["POLYLAB_STATE_DIR"] = pbogState;
        using var p = Process.Start(psi)!;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await p.WaitForExitAsync(cts.Token);
        Assert.Equal(0, p.ExitCode);
        Assert.True(File.Exists(shot));
    }
}
