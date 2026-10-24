namespace NDZ.PBOGPacker.Core;

public enum StepStatus { Idle, Running, Done, Error, Skipped, Off }
public enum LogLevel { Info, Cmd, Ok, Warn, Err }
public enum CheckState { Ok, Warn, Err }

public static class Steps
{
    public static readonly string[] Ids = { "env", "build", "protogen", "pack", "sign", "assemble", "deploy", "host" };
}

public sealed record BuildState(string State, DateTime? At = null, long Ms = 0, string? Step = null);
public sealed record SignState(string State, string? Key = null);
public sealed record DeployState(string State, IReadOnlyList<string> Servers, DateTime? At = null);

public static class ModKinds
{
    public const string Client = "client";
    public const string Server = "server";
    public const string Grafted = "grafted";
}

public sealed record ModInfo(
    string Id, string Name, string Version, string Path, IReadOnlyList<string> Modules, string Key,
    BuildState Build, SignState Sign, DeployState Deploy, string? Logo = null,
    string Kind = ModKinds.Grafted, string? Out = null, string? Cpp = null);

// State: "ok" — the tool's own host, "other" — a GRAFT host of another build, "foreign" — a hid.dll that
// is not GRAFT, "none" — no hid.dll (null: not checked yet, older state files).
public sealed record HostInfo(bool Installed, string? Version, string? State = null);

// One line of the GRAFT check of a server. Key picks the texts Gc.<Key>.T / Gc.<Key>.D, Args fill them;
// Action is what its button does: install, uninstall, mods, open (Target is the path).
public sealed record GraftCheck(string Key, string State, IReadOnlyList<string> Args, string? Action = null, string? Target = null);
public sealed record GraftReport(ServerInfo Server, IReadOnlyList<GraftCheck> Items, DateTime At);
public sealed record ServerInfo(string Id, string Name, string Path, string Build, int VerifySignatures, HostInfo Host,
    string? StartBat = null);
public sealed record ServerLaunched(ServerInfo Server, int ScriptPid);
// Scripts: start scripts closed; Servers: server processes found; Forced: one had to be killed.
public sealed record ServerStopped(ServerInfo Server, int Scripts, int Servers, bool Forced);
public sealed record KeyInfo(string Tag, string Fingerprint, string Created, string Path);

public sealed record DoctorItem(
    string Id, string? Name, string? NameKey, CheckState State, string Version, string Path,
    string? FixKey = null, string? FixCmd = null, bool FixIsLink = false);

public sealed class ToolPaths
{
    public string CMake { get; set; } = "";
    public string PboProject { get; set; } = "";
    public string Graft { get; set; } = "";
    public string HostDll { get; set; } = "";
    public string KeyDir { get; set; } = "";
}

public sealed class BuildDefaults
{
    public string Preset { get; set; } = "release";
    public bool Sign { get; set; } = true;
    public string Key { get; set; } = "CG";
    public string Server { get; set; } = "";
}

public sealed class PackSettings
{
    public string ClientOut { get; set; } = "";
    public string ServerOut { get; set; } = "";
    public string GraftedOut { get; set; } = "";
    public bool Rapify { get; set; } = true;
    // A server mod that gets added (or switched from client to server) gets its C++ project at once.
    public bool AutoCpp { get; set; } = true;
}

public sealed class AppSettings
{
    public ToolPaths Tools { get; set; } = new();
    public BuildDefaults Defaults { get; set; } = new();
    public PackSettings Pack { get; set; } = new();
    public string WorkshopDir { get; set; } = "";
    public string Language { get; set; } = "ru";
    public bool CopyLaunchLine { get; set; } = true;
}

public sealed record PipelineOptions(
    string Preset, bool Sign, bool Deploy, bool Host, string Server,
    string? FromStep = null, IReadOnlyList<string>? Only = null);

public sealed record LogEntry(LogLevel Level, string Line);

public sealed record PipelineStarted(string RunId, IReadOnlyList<string> Steps);
public sealed record PipelineStep(string RunId, string Step, StepStatus Status, long Ms);
public sealed record PipelineLog(string RunId, string Step, string Line, LogLevel Level);
public sealed record PipelineDone(string RunId, string ModLine, string CmdLine, long PboBytes, string ModDir, string ServerDir, long Ms,
    IReadOnlyList<string>? Duplicates = null);
public sealed record PipelineError(string RunId, string Step, string Message, string? Where, IReadOnlyList<LogEntry> LogTail, string? Hint, string? FixCmd);
public sealed record PipelineCancelled(string RunId);

public interface IGraftBackend
{
    Task<IReadOnlyList<ModInfo>> ListModsAsync();
    Task<ModInfo> AddModAsync(string? path);
    Task RemoveModAsync(string id);
    Task<ModInfo> SetModLogoAsync(string id, string? imagePath);
    Task<ModInfo> SetModPackAsync(string id, string kind, string key, string? outDir);
    Task<ModInfo> CreateCppProjectAsync(string id);

    Task<IReadOnlyList<ServerInfo>> ListServersAsync();
    Task<ServerInfo?> AddServerAsync(string path);
    Task<ServerInfo> InstallHostAsync(string serverId);
    Task<ServerInfo> UninstallHostAsync(string serverId);
    Task<string> InstallPluginAsync(string serverId, string dll);
    Task<GraftReport> CheckGraftAsync(string serverId, string? bat);
    Task<ServerLaunched> LaunchServerAsync(string serverId, string? bat);
    Task<ServerStopped> StopServerAsync(string serverId, int scriptPid);
    Task<ModsSetup> GetServerModsAsync(string serverId, string bat);
    Task<ModsSaved> SaveServerModsAsync(string serverId, string bat, IReadOnlyList<ModPick> mods, IReadOnlyList<ModPick> serverMods);

    Task<IReadOnlyList<KeyInfo>> ListKeysAsync();
    Task<KeyInfo> CreateKeyAsync(string tag);
    Task<string> ExportKeyAsync(string tag);

    Task<IReadOnlyList<DoctorItem>> DoctorAsync();
    Task<AppSettings> GetSettingsAsync();
    Task SaveSettingsAsync(AppSettings settings);
    Task OpenPathAsync(string path);

    Task<string> RunPipelineAsync(string modId, PipelineOptions options);
    Task CancelAsync(string runId);

    event Action<PipelineStarted>? Started;
    event Action<PipelineStep>? StepChanged;
    event Action<PipelineLog>? Log;
    event Action<PipelineDone>? Done;
    event Action<PipelineError>? Failed;
    event Action<PipelineCancelled>? Cancelled;
}
