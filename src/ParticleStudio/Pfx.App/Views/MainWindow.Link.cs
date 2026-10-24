using System.Globalization;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pfx.App.Inspector;
using Pfx.App.Model;
using Pfx.Link;
using Pfx.Sim;

namespace Pfx.App.Views;

// The live link to a running DayZDiag (spec 3.12): every open part is mirrored in the game while the link is on. Each
// change of a part goes to LiveLink with the part's new text (a set for live parameters, a reload otherwise); the
// title row shows the link's state, the "Link" menu and the game panel steer it.
public partial class MainWindow : IGameHost
{
    private LinkHub _hub = null!;
    private LiveLink _live = null!;

    public LiveLink Live => _live;
    public GameView GamePanelView => GamePanel;
    public bool IsAutomated => _launch.Automated;

    private void InitLink()
    {
        _hub = new LinkHub(SynchronizationContext.Current);
        _live = new LiveLink(_hub);
        _hub.StateChanged += _ => ShowLinkState();
        _hub.ClientChanged += c => { ShowLinkState(); GamePanel.Note(Loc.F("Game.Hello", c.Name), "ok"); };
        _hub.Rejected += r => GamePanel.Note(Loc.F("Game.Rejected", r), "warn");
        _live.PartChanged += _ => GamePanel.Refresh();
        _live.Timed += t => GamePanel.Timed(t);
        _live.ShotTaken += s => GamePanel.Shot(s);
        _live.Failed += (what, why) => GamePanel.Note(Loc.F("Game.Failed", what, why), "err");
        _live.Originals += (_, rows) =>
        {
            int bad = rows.Count(r => !r.Matches);
            GamePanel.Note(Loc.F("Game.Originals", rows.Count - bad, rows.Count), bad == 0 ? "ok" : "warn");
        };
        LinkButton.IsEnabled = true;
        LinkButton.Click += (_, _) => ShowLinkMenu();
        InspectorView.PushToGame = PushField;
        InspectorView.CanPushToGame = () => _hub.State == LinkState.Connected && _active != null && _live.PartOf(_active) is { InGame: true };
        GamePanel.Init(this);
        ShowLinkState();
    }

    // The settings folder when it holds a game, else the Steam library that does (nothing assumed about this PC).
    public string? GameFolder => GameFolders.IsGame(Settings.GameFolder) ? Settings.GameFolder : GameFolders.Detect();

    public void StartLink()
    {
        if (GameFolder is not { } folder)
        {
            Toast(Loc.T("Toast.Game.NoFolder"), "warn");
            ShowGamePane();
            return;
        }
        try
        {
            _live.Start(folder, Settings.LinkPort);
        }
        catch (SocketException e)
        {
            Toast(Loc.F("Toast.Game.Port", Settings.LinkPort, e.Message), "err");
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Toast(Loc.F("Toast.Game.FolderLocked", folder, e.Message), "err");
            return;
        }
        long now = LinkHub.Now;
        foreach (var d in _docs)
            PushPart(d, now);
        ShowLinkState();
        GamePanel.Note(Loc.F("Game.Listening", _hub.Port), "text");
    }

    public void StopLink()
    {
        _live.Stop();
        ShowLinkState();
    }

    private void StopLinkOnExit()
    {
        try
        {
            _live.Stop();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    // A part's new text for the game. Called after every change of the part (OnDocChanged), when it opens and when the
    // link starts; `changedAt` is the moment of the edit, so the measured round trip starts there.
    private void PushPart(EffectDoc doc, long changedAt)
    {
        if (_hub.State == LinkState.Off)
            return;
        _live.Push(doc, Path.GetFileNameWithoutExtension(doc.Path), doc.Ptc.ToBytes(), TakeSnapshot(), changedAt);
    }

    private void RemovePart(EffectDoc doc)
    {
        if (_hub.State != LinkState.Off)
            _live.Remove(doc);
    }

    private void ShowLinkState()
    {
        var state = _hub.State;
        LinkDot.Fill = (Brush)FindResource(state switch { LinkState.Connected => "B.Ok", LinkState.Waiting => "B.Warn", LinkState.Lost => "B.Err", _ => "B.TextOff" });
        LinkText.Text = GameView.StateText(state, _hub.Client);
        LinkButton.ToolTip = Loc.T(state == LinkState.Off ? "Tip.Link" : "Tip.LinkOn");
        GamePanel.Refresh();
    }

    // Two 16:9 frames side by side need about this much height to be read; the bottom dock grows to it (once, the user
    // may shrink it again).
    private const double GameDockHeight = 380;

    private void ShowGamePane()
    {
        if (GamePane.Parent is AvalonDock.Layout.LayoutAnchorablePane pane && pane.DockHeight.IsAbsolute && pane.DockHeight.Value < GameDockHeight
            && !GamePane.IsSelected)
            pane.DockHeight = new GridLength(GameDockHeight);
        GamePane.Show();
        GamePane.IsSelected = true;
        GamePane.IsActive = true;
    }

    // The same items as the "Link" menu, under the title-row button.
    private void ShowLinkMenu()
    {
        var menu = new ContextMenu { PlacementTarget = LinkButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var item in LinkItems())
            menu.Items.Add(item);
        menu.IsOpen = true;
    }

    private IEnumerable<Control> LinkItems()
    {
        MenuItem Item(string key, Action run, bool enabled = true)
        {
            var m = new MenuItem { Header = Loc.T(key), IsEnabled = enabled, ToolTip = Loc.T("Tip." + key) };
            m.Click += (_, _) => run();
            return m;
        }
        bool off = _hub.State == LinkState.Off;
        bool on = _hub.State == LinkState.Connected;
        yield return off ? Item("Cmd.LinkStart", StartLink) : Item("Cmd.LinkStop", StopLink);
        yield return Item("Cmd.LinkResync", _live.Resync, on);
        yield return Item("Cmd.LinkMark", () => _live.MarkInFront(Settings.LinkMark), on);
        yield return Item("Cmd.LinkShot", () => GameShot("shot"), on && _hub.Client?.AutoTest == true);
        yield return new Separator();
        if (_hub.Client is { } c)
            yield return new MenuItem
            {
                Header = Loc.F("Cmd.LinkClient", c.Name, c.Width, c.Height),
                IsEnabled = false,
                ToolTip = Loc.F("Tip.Game.Client", c.Name, c.Width, c.Height, Loc.T(c.FilePatching ? "Game.Yes" : "Game.No"),
                    Loc.T(c.AutoTest ? "Game.Yes" : "Game.No"), c.Profiles),
            };
        yield return Item("Cmd.LinkPanel", ShowGamePane);
    }

    // The game camera at the viewport's camera, one shot, and the viewport's own frame beside it at the same effect
    // time: the preview is seeked to the age of the part's source in the game (from the moment its spawn was reported)
    // before the frame is taken, so both show the same moment of the effect, not the preview's own loop.
    public async void GameShot(string label)
    {
        if (_hub.State != LinkState.Connected)
            return;
        float? age = null;
        if (_active != null && _live.PartOf(_active) is { InGame: true } part)
        {
            age = (float)(LinkHub.Ms(part.SpawnedAt, LinkHub.Now) / 1000);
            _pipeline.Seek(age.Value);
            while (_pipeline.AnySeeking)
                await NextFrame();
            await NextFrame();
        }
        var cam = _viewport.Camera;
        _live.Camera(cam.Eye, cam.Target, cam.Fov);
        string name = label + "_" + DateTime.Now.ToString("HHmmss_fff", CultureInfo.InvariantCulture);
        GamePanel.Expect(name, _viewport.CaptureFrame(false), age);
        _live.Shot(name);
        ShowGamePane();
    }

    private Task NextFrame()
    {
        var frame = new TaskCompletionSource();
        void Once()
        {
            _viewport.Rendered -= Once;
            frame.TrySetResult();
        }
        _viewport.Rendered += Once;
        return frame.Task;
    }

    // A shot series with the viewport camera; the baseline first takes every part out (an empty scene).
    public void GameSeries(int count, bool baseline)
    {
        if (_hub.State != LinkState.Connected)
            return;
        var cam = _viewport.Camera;
        _live.Camera(cam.Eye, cam.Target, cam.Fov);
        if (baseline)
            _live.ClearGame();
        string prefix = (baseline ? "base" : "series") + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
        count = Math.Clamp(count, 2, LiveLink.MaxSeries);
        GamePanel.ExpectSeries(prefix, count, baseline);
        _live.Series(prefix, count, 1);
        ShowGamePane();
    }

    // The known differences of the active part's emitters (KnownDifference flags), each checkable with a game shot.
    public IReadOnlyList<(string Emitter, string Flag)> KnownDifferences()
    {
        var list = new List<(string, string)>();
        if (_active == null || _pipeline.PreviewOf(_active) is not { } p)
            return list;
        for (int e = 0; e < p.Params.Count; e++)
            foreach (KnownDifference d in Enum.GetValues<KnownDifference>())
                if (d != KnownDifference.None && p.Params[e].Differences.HasFlag(d))
                    list.Add((p.Params[e].Name, d.ToString()));
        return list;
    }

    // The field menu's "push to the running game now": a live parameter is set again on every selected emitter; any
    // other key reloads the part.
    private void PushField(FieldVm field)
    {
        if (_active is not { } doc || _live.PartOf(doc) is not { InGame: true })
            return;
        long now = LinkHub.Now;
        bool all = field.Owner.Scope == FieldScope.Emitter && field.Targets.Count > 0;
        foreach (var t in field.Targets)
        {
            if (field.Owner.Scope != FieldScope.Emitter || t.Block.Block.Name is not { } name || !_live.PushKey(doc, doc.Ptc, name, field.Key, now))
            {
                all = false;
                break;
            }
        }
        if (!all)
            _live.Reload(doc);
        GamePanel.Note(Loc.F(all ? "Game.Pushed" : "Game.Reloading", field.Label), "text");
    }
}
