using System.Diagnostics;
using System.Globalization;

namespace Pfx.Link;

// One game client = one mission start of the link mod (its session id), with what it reported in its hello.
public sealed record LinkClient(string Session, string Name, bool FilePatching, bool AutoTest, int Width, int Height, string Profiles)
{
    // The game's EmitorParam names in enum order (its "params" line): the number a parameter is sent under.
    public IReadOnlyList<string> Params { get; init; } = Array.Empty<string>();

    public int ParamIndex(string name)
    {
        for (int i = 0; i < Params.Count; i++)
            if (Params[i] == name)
                return i;
        return -1;
    }
}

public enum LinkState { Off, Waiting, Connected, Lost }

// The transport of the live link. The game polls: each request carries "c <session> <applied>" (the newest command it
// has applied) and its event lines (events that come up while a poll is held go at once as "e <session>" requests,
// answered empty); the answer carries every queued command it has not applied, "<seq> <command>".
// With nothing queued the answer is held until a command arrives or the hold ends (a long poll), so a command reaches
// the game as soon as it is queued, without a polling period on either side. A command stays queued until the game
// reports it applied, so an answer lost on the way is sent again. The newest client that says hello is the one in use
// (a known client also takes over when the one in use has not polled for LostAfter); others are held and get nothing.
public sealed class LinkHub : IDisposable
{
    // Half of RestApi's default read timeout (10 s, ERESTOPTION_READOPERATION in 3_game/http/restapi.c), so a held
    // request is always answered before the engine gives up on it.
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(5);
    // The answer when there is nothing to send. Never empty: RestApi reports an answer without data as a timeout
    // (OnTimeout, 3_game/http/restapi.c), which the mod would treat as a lost link. Sequence 0 is never applied.
    public const string Nothing = "0 ok";
    // The link mod's POLYLAB_PFXLINK_PORT (a client started with -polylabPfxLink=<port> uses that one).
    public const int DefaultPort = 18150;
    // A client in use polls again right after each answer; two holds without a poll means it is gone.
    public static readonly TimeSpan LostAfter = Hold + Hold;

    private sealed class Pending
    {
        public int Seq;
        public string Text = "";
        public string? Key;
        public bool Sent;
    }

    private readonly object _gate = new();
    private readonly List<Pending> _queue = new();
    private readonly Dictionary<string, LinkClient> _clients = new();
    private readonly SynchronizationContext? _ui;
    private readonly LinkServer _server;
    private readonly Timer _lostTimer;
    private TaskCompletionSource _wake = NewWake();
    private int _seq;
    private string? _active;
    private DateTime _lastPoll;
    private LinkState _state = LinkState.Off;

    // ui: where events are raised (the WPF dispatcher); null raises them on the server's threads (tests).
    public LinkHub(SynchronizationContext? ui)
    {
        _ui = ui;
        _server = new LinkServer(Exchange);
        _server.Rejected += r => Raise(() => Rejected?.Invoke(r));
        _lostTimer = new Timer(_ => OnLost());
    }

    public event Action<LinkState>? StateChanged;
    // A client became the one in use (its hello); the scene is pushed to it again.
    public event Action<LinkClient>? ClientChanged;
    // An event line of the client in use, split into tokens.
    public event Action<string[]>? Message;
    public event Action<string>? Rejected;

    public LinkState State => _state;
    public int Port => _server.Port;
    public LinkClient? Client
    {
        get
        {
            lock (_gate)
                return _active != null && _clients.TryGetValue(_active, out var c) ? c : null;
        }
    }

    public IReadOnlyList<LinkClient> Clients
    {
        get
        {
            lock (_gate)
                return _clients.Values.ToList();
        }
    }

    public void Start(int port)
    {
        _server.Start(port);
        SetState(LinkState.Waiting);
    }

    public void Stop()
    {
        _server.Stop();
        _lostTimer.Change(Timeout.Infinite, Timeout.Infinite);
        lock (_gate)
        {
            _queue.Clear();
            _clients.Clear();
            _active = null;
        }
        SetState(LinkState.Off);
    }

    public void Dispose()
    {
        Stop();
        _lostTimer.Dispose();
    }

    // Queues a command for the client in use; returns its sequence number. A command with a key replaces a queued one
    // with the same key that has not gone out yet (a drag sends only its newest value).
    public int Send(string command, string? key = null)
    {
        TaskCompletionSource wake;
        int seq;
        lock (_gate)
        {
            if (key != null && _queue.FirstOrDefault(p => p.Key == key && !p.Sent) is { } same)
            {
                same.Text = command;
                return same.Seq;
            }
            seq = ++_seq;
            _queue.Add(new Pending { Seq = seq, Text = command, Key = key });
            wake = _wake;
            _wake = NewWake();
        }
        wake.TrySetResult();
        return seq;
    }

    // Commands not yet reported applied (the link panel shows the count).
    public int Queued
    {
        get
        {
            lock (_gate)
                return _queue.Count;
        }
    }

    private async Task<string> Exchange(string body, CancellationToken ct)
    {
        var lines = body.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0)
            return Nothing;
        var head = Tokens(lines[0]);
        if (head.Length >= 2 && head[0] == "e")
        {
            if (head[1] == Active)
            {
                var reports = lines.Skip(1).Select(Tokens).ToList();
                Raise(() => { foreach (var r in reports) Message?.Invoke(r); });
            }
            return Nothing;
        }
        if (head.Length < 3 || head[0] != "c" || !int.TryParse(head[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int applied))
        {
            Raise(() => Rejected?.Invoke(lines[0]));
            return Nothing;
        }
        string session = head[1];
        LinkClient? became = null;
        bool known;
        lock (_gate)
        {
            foreach (var line in lines.Skip(1))
            {
                var t = Tokens(line);
                if (t[0] == "hello" && ParseHello(session, line) is { } c)
                {
                    _clients[session] = c;
                    Activate(session, applied);
                    became = c;
                }
                else if (t[0] == "params" && _clients.TryGetValue(session, out var said))
                {
                    _clients[session] = said with { Params = t.Skip(1).ToList() };
                    if (became != null)
                        became = _clients[session];
                }
            }
            known = _clients.ContainsKey(session);
            if (known && became == null && _active != session && (_active == null || DateTime.UtcNow - _lastPoll > LostAfter))
            {
                Activate(session, applied);
                became = _clients[session];
            }
            if (session == _active)
            {
                _queue.RemoveAll(p => p.Seq <= applied);
                _lastPoll = DateTime.UtcNow;
            }
        }
        if (!known)
            return "0 hello";
        if (session != Active)
        {
            await Task.Delay(Hold, ct);
            return Nothing;
        }
        _lostTimer.Change(LostAfter, Timeout.InfiniteTimeSpan);
        SetState(LinkState.Connected);
        if (became != null)
            Raise(() => ClientChanged?.Invoke(became));
        var events = lines.Skip(1).Select(Tokens).Where(t => t[0] is not ("hello" or "params")).ToList();
        if (events.Count > 0)
            Raise(() => { foreach (var e in events) Message?.Invoke(e); });
        string answer = Collect(applied, out var wake);
        if (answer.Length > 0)
            return answer;
        await Task.WhenAny(wake.Task, Task.Delay(Hold, ct));
        answer = session == Active ? Collect(applied, out _) : "";
        return answer.Length > 0 ? answer : Nothing;
    }

    // The client in use from now on: what was queued for another one is dropped (the scene is sent again), and the
    // numbering continues above what this client has applied (a mod that outlived an earlier tool session has applied
    // higher numbers; commands numbered below would count as done).
    private void Activate(string session, int applied)
    {
        if (_active != session)
            _queue.Clear();
        _active = session;
        _seq = Math.Max(_seq, applied);
    }

    private string? Active
    {
        get
        {
            lock (_gate)
                return _active;
        }
    }

    private string Collect(int applied, out TaskCompletionSource wake)
    {
        lock (_gate)
        {
            wake = _wake;
            var send = _queue.Where(p => p.Seq > applied).ToList();
            foreach (var p in send)
                p.Sent = true;
            return string.Join("\n", send.Select(p => p.Seq.ToString(CultureInfo.InvariantCulture) + " " + p.Text));
        }
    }

    // hello <protocol> <name> <filePatching 0|1> <autotest 0|1> <width> <height> <profiles folder, may hold spaces>
    private static LinkClient? ParseHello(string session, string line)
    {
        var t = line.Split(' ', 8);
        if (t.Length < 7)
            return null;
        int.TryParse(t[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w);
        int.TryParse(t[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h);
        return new LinkClient(session, t[2], t[3] == "1", t[4] == "1", w, h, t.Length > 7 ? t[7].Trim() : "");
    }

    public static string[] Tokens(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } t ? t : new[] { "" };

    private void OnLost()
    {
        if (_state == LinkState.Connected)
            SetState(LinkState.Lost);
    }

    private void SetState(LinkState s)
    {
        lock (_gate)
        {
            if (_state == s)
                return;
            _state = s;
        }
        Raise(() => StateChanged?.Invoke(s));
    }

    private void Raise(Action a)
    {
        if (_ui != null)
            _ui.Post(_ => a(), null);
        else
            a();
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static long Now => Stopwatch.GetTimestamp();

    public static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;
}
