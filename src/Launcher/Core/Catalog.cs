using System.Net.Http;

namespace PolyLab.Launcher.Core;

public sealed record ToolInfo(string Id, string Name, string Abbrev, string Exe, string Photo);

public static class Catalog
{
    // owner/repo of the Poly Lab releases.
    public const string Repo = "godwillhelp/PolyLab";
    public const string ReleasesUrl = "https://api.github.com/repos/" + Repo + "/releases?per_page=100";
    public const string GraftApi = "https://api.github.com/repos/KRdayzmodding/KR_GRAFTED";

    // Full releases URL instead of ReleasesUrl, for tests against a local fake of the GitHub API.
    public const string ReleasesUrlEnv = "POLYLAB_RELEASES_URL";
    // Read by PBOG Packer (GraftRuntime.DirEnv).
    public const string GraftDirEnv = "POLYLAB_GRAFT_DIR";

    public const string LauncherId = "launcher";

    public static readonly ToolInfo[] Tools =
    [
        new("pbog", "PBOG Packer", "PBOG", "PBOG_Packer.exe", "st_pack.jpg"),
        new("pfx", "Particle Studio", "PFX", "Particle_Studio.exe", "bg_env.jpg"),
    ];

    public static ToolInfo? Find(string id) => Tools.FirstOrDefault(t => t.Id == id);
}

public static class Net
{
    public static readonly HttpClient Http = Create();

    private static HttpClient Create()
    {
        // The timeout covers a response's headers; downloads read their body after it (ResponseHeadersRead).
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("PolyLab/" + typeof(Net).Assembly.GetName().Version?.ToString(3));
        return h;
    }
}
