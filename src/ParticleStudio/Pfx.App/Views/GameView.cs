using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using Pfx.App.Core;
using Pfx.App.Textures;
using Pfx.Link;
using Pfx.Textures;

namespace Pfx.App.Views;

public interface IGameHost
{
    AppSettings Settings { get; }
    LiveLink Live { get; }
    string? GameFolder { get; }
    void StartLink();
    void StopLink();
    void GameShot(string label);
    void GameSeries(int count, bool baseline);
    IReadOnlyList<(string Emitter, string Flag)> KnownDifferences();
    void Toast(string text, string kind);
}

// The "game reference" panel (spec 3.12): the link's state and its settings, what the game shows of the scene, a shot
// from the game with the viewport's camera beside the viewport's own frame of the same request, shot series with the
// blink measure against an empty baseline, the known differences of the active part with a check each, and the
// measured round trips of the last edits.
public sealed class GameView : UserControl
{
    private const int LogShown = 8;
    // Default length of a shot series: 30 frames hold a 0.5 s blink at 60 fps; MaxSeries is the hard cap (spec 2.11).
    private const int DefaultSeries = 30;

    private IGameHost? _host;
    private readonly Button _start = new();
    private readonly Ellipse _dot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _state = new();
    private readonly WrapPanel _actions = new() { Margin = new Thickness(0, 0, 0, 2) };
    private readonly TextBox _folder = new();
    private readonly TextBox _port = new();
    private readonly TextBox _series = new();
    private readonly TextBlock _hint = new();
    private readonly Image _gameImage = new() { Stretch = Stretch.Uniform };
    private readonly Image _viewImage = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _gameCaption = new();
    private readonly TextBlock _viewCaption = new();
    private readonly StackPanel _parts = new();
    private readonly StackPanel _diffs = new();
    private readonly StackPanel _log = new();
    private readonly List<(string Text, string Kind)> _lines = new();
    private readonly Dictionary<string, (RgbaImage? Frame, float? Age)> _viewFrames = new();
    private readonly Dictionary<string, List<BmpFrame>> _seriesFrames = new();
    private readonly Dictionary<string, int> _seriesWanted = new();
    private SeriesStats? _baseline;

    public GameView()
    {
        Background = Res<Brush>("B.BgPanel");
        var top = new WrapPanel { Margin = new Thickness(8, 5, 8, 0) };
        _start.Style = Res<Style>("Btn.Accent");
        _start.Margin = new Thickness(0, 0, 10, 4);
        _start.Click += (_, _) =>
        {
            if (_host == null)
                return;
            if (_host.Live.Hub.State == LinkState.Off)
                _host.StartLink();
            else
                _host.StopLink();
        };
        top.Children.Add(_start);
        var state = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 4), VerticalAlignment = VerticalAlignment.Center };
        state.Children.Add(_dot);
        _state.Style = Res<Style>("T.Hi");
        _state.VerticalAlignment = VerticalAlignment.Center;
        state.Children.Add(_state);
        top.Children.Add(state);
        top.Children.Add(_actions);

        var setup = new WrapPanel { Margin = new Thickness(8, 0, 8, 2) };
        setup.Children.Add(Label("Game.Folder"));
        _folder.Width = 260;
        Field(_folder, "Tip.Game.Folder", () =>
        {
            _host!.Settings.GameFolder = _folder.Text.Trim();
            Save();
        });
        setup.Children.Add(_folder);
        var browse = new Button { Content = "…", Margin = new Thickness(0, 0, 12, 4), ToolTip = Loc.T("Tip.Browse") };
        browse.Click += (_, _) => BrowseFolder();
        setup.Children.Add(browse);
        setup.Children.Add(Label("Game.Port"));
        _port.Width = 56;
        Field(_port, "Tip.Game.Port", () =>
        {
            if (int.TryParse(_port.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) && p is > 0 and < 65536)
                _host!.Settings.LinkPort = p;
            _port.Text = _host!.Settings.LinkPort.ToString(CultureInfo.InvariantCulture);
            Save();
        });
        setup.Children.Add(_port);
        var copy = new Button { Margin = new Thickness(0, 0, 0, 4) };
        copy.Click += (_, _) =>
        {
            Clipboard.SetText(LaunchLine());
            _host?.Toast(Loc.T("Toast.Game.Copied"), "ok");
        };
        Localized(copy, "Game.CopyLaunch");
        setup.Children.Add(copy);
        _hint.Style = Res<Style>("T.Dim");
        _hint.TextWrapping = TextWrapping.Wrap;
        _hint.Margin = new Thickness(8, 0, 8, 4);

        var images = new Grid { Margin = new Thickness(8, 0, 4, 6) };
        images.ColumnDefinitions.Add(new ColumnDefinition());
        images.ColumnDefinitions.Add(new ColumnDefinition());
        images.Children.Add(Picture(_gameImage, _gameCaption, "Tip.Game.GameImage", 0));
        images.Children.Add(Picture(_viewImage, _viewCaption, "Tip.Game.ViewImage", 1));

        var side = new StackPanel { Margin = new Thickness(4, 0, 8, 6) };
        side.Children.Add(Header("Game.InGame"));
        side.Children.Add(_parts);
        side.Children.Add(Header("Game.Differences"));
        side.Children.Add(_diffs);
        side.Children.Add(Header("Game.Log"));
        side.Children.Add(_log);
        var sideScroll = new ScrollViewer { Content = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });
        body.Children.Add(images);
        Grid.SetColumn(sideScroll, 1);
        body.Children.Add(sideScroll);

        var dock = new DockPanel();
        foreach (var e in new FrameworkElement[] { top, setup, _hint })
        {
            DockPanel.SetDock(e, Dock.Top);
            dock.Children.Add(e);
        }
        dock.Children.Add(body);
        Content = dock;
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
    }

    public void Init(IGameHost host)
    {
        _host = host;
        Refresh();
    }

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    private void Save()
    {
        if (MainWindow.Current?.IsAutomated != true)
            _host?.Settings.Save();
        Refresh();
    }

    private static TextBlock Label(string key) => new()
    {
        Text = Loc.T(key),
        Style = Res<Style>("T.Dim"),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 6, 4),
        ToolTip = Loc.T("Tip." + key),
    };

    private static TextBlock Header(string key) => new()
    {
        Text = Loc.T(key),
        Style = Res<Style>("T.Hi"),
        Margin = new Thickness(0, 6, 0, 2),
        ToolTip = Loc.T("Tip." + key),
    };

    private static void Localized(ButtonBase b, string key)
    {
        b.Content = Loc.T(key);
        b.ToolTip = Loc.T("Tip." + key);
    }

    private static void Field(TextBox box, string tip, Action commit)
    {
        box.Style = Res<Style>("Box.Num");
        box.Margin = new Thickness(0, 0, 4, 4);
        box.ToolTip = Loc.T(tip);
        box.LostKeyboardFocus += (_, _) => commit();
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) commit(); };
    }

    private static FrameworkElement Picture(Image image, TextBlock caption, string tip, int column)
    {
        caption.Style = Res<Style>("T.Dim");
        caption.TextTrimming = TextTrimming.CharacterEllipsis;
        caption.Margin = new Thickness(0, 0, 0, 2);
        var frame = new Border
        {
            Background = Res<Brush>("B.BgDeep"),
            BorderBrush = Res<Brush>("B.LineSubtle"),
            BorderThickness = new Thickness(1),
            Child = image,
            ToolTip = Loc.T(tip),
            MinHeight = 60,
        };
        var panel = new DockPanel { Margin = new Thickness(column == 0 ? 0 : 4, 0, column == 0 ? 4 : 0, 0) };
        DockPanel.SetDock(caption, Dock.Top);
        panel.Children.Add(caption);
        panel.Children.Add(frame);
        Grid.SetColumn(panel, column);
        return panel;
    }

    private void BrowseFolder()
    {
        if (_host == null)
            return;
        var dialog = new OpenFolderDialog { Title = Loc.T("Game.Folder") };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        _host.Settings.GameFolder = dialog.FolderName;
        Save();
    }

    // The command line that starts DayZDiag with the link mod next to the game (spec 2.12: -filePatching for the loose
    // versions, -autotest for MakeScreenshot); the server needs allowFilePatching = 1.
    public string LaunchLine()
    {
        string folder = _host?.GameFolder ?? "<DayZ>";
        Payload.Ensure(Payload.LinkPbo);
        string mod = Payload.LinkMod;
        string port = _host == null || _host.Settings.LinkPort == LinkHub.DefaultPort ? "" : " -polylabPfxLink=" + _host.Settings.LinkPort.ToString(CultureInfo.InvariantCulture);
        return $"\"{System.IO.Path.Combine(folder, GameFolders.Exes[0])}\" \"-mod={mod}\" -filePatching -autotest -connect=<server> -port=<port>{port}";
    }

    public void Refresh()
    {
        if (_host == null)
            return;
        var live = _host.Live;
        var state = live.Hub.State;
        var client = live.Hub.Client;
        Localized(_start, state == LinkState.Off ? "Game.Start" : "Game.Stop");
        _dot.Fill = Res<Brush>(state switch { LinkState.Connected => "B.Ok", LinkState.Waiting => "B.Warn", LinkState.Lost => "B.Err", _ => "B.TextOff" });
        _state.Text = StateText(state, client);
        _state.ToolTip = client == null ? Loc.T("Tip.Game.State") : Loc.F("Tip.Game.Client", client.Name, client.Width, client.Height,
            Loc.T(client.FilePatching ? "Game.Yes" : "Game.No"), Loc.T(client.AutoTest ? "Game.Yes" : "Game.No"), client.Profiles);
        if (!_folder.IsKeyboardFocused)
            _folder.Text = _host.Settings.GameFolder.Length > 0 ? _host.Settings.GameFolder : _host.GameFolder ?? "";
        if (!_port.IsKeyboardFocused)
            _port.Text = _host.Settings.LinkPort.ToString(CultureInfo.InvariantCulture);
        if (_series.Text.Length == 0)
            _series.Text = DefaultSeries.ToString(CultureInfo.InvariantCulture);
        _hint.Text = Hint(state, client);
        _hint.Visibility = _hint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildActions(state == LinkState.Connected, client);
        ShowParts();
        ShowDifferences(state == LinkState.Connected && client?.AutoTest == true);
        ShowLog();
    }

    public static string StateText(LinkState state, LinkClient? client) => state switch
    {
        LinkState.Connected when client != null => Loc.F("Lbl.LinkOn", client.Name),
        LinkState.Waiting => Loc.T("Lbl.LinkWaiting"),
        LinkState.Lost => Loc.T("Lbl.LinkLost"),
        _ => Loc.T("Lbl.LinkOff"),
    };

    private string Hint(LinkState state, LinkClient? client)
    {
        if (_host!.GameFolder == null)
            return Loc.T("Game.Hint.NoFolder");
        if (state == LinkState.Off)
            return Loc.T("Game.Hint.Off");
        if (state == LinkState.Waiting)
            return Loc.F("Game.Hint.Waiting", _host.Settings.LinkPort);
        if (client is { FilePatching: false })
            return Loc.T("Game.Hint.NoFilePatching");
        if (client is { AutoTest: false })
            return Loc.T("Game.Hint.NoAutotest");
        return "";
    }

    private void BuildActions(bool on, LinkClient? client)
    {
        _actions.Children.Clear();
        Button Act(string key, Action run, bool enabled)
        {
            var b = new Button { Style = Res<Style>("Btn.Base"), Margin = new Thickness(0, 0, 6, 4), IsEnabled = enabled };
            Localized(b, key);
            b.Click += (_, _) => run();
            _actions.Children.Add(b);
            return b;
        }
        bool shots = on && client?.AutoTest == true;
        Act("Game.Resync", () => _host!.Live.Resync(), on);
        Act("Game.Mark", () => _host!.Live.MarkInFront(_host.Settings.LinkMark), on);
        Act("Game.Clear", () => _host!.Live.ClearGame(), on);
        Act("Game.Shot", () => _host!.GameShot("shot"), shots);
        var seriesLabel = Label("Game.SeriesCount");
        _actions.Children.Add(seriesLabel);
        _series.Width = 40;
        if (_series.Style == null)
            Field(_series, "Tip.Game.SeriesCount", () => _series.Text = SeriesCount().ToString(CultureInfo.InvariantCulture));
        _actions.Children.Add(_series);
        Act("Game.Series", () => _host!.GameSeries(SeriesCount(), false), shots);
        Act("Game.Baseline", () => _host!.GameSeries(SeriesCount(), true), shots);
        Act("Game.PlayerCamera", () => _host!.Live.CameraOff(), on);
    }

    public int SeriesCount() =>
        int.TryParse(_series.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? Math.Clamp(n, 2, LiveLink.MaxSeries) : DefaultSeries;

    private void ShowParts()
    {
        _parts.Children.Clear();
        var parts = _host!.Live.Parts.ToList();
        if (parts.Count == 0)
            _parts.Children.Add(Line(Loc.T("Game.NoParts"), "dim"));
        foreach (var p in parts)
        {
            string text = p.Error != null ? Loc.F("Game.PartError", p.Name, p.Error)
                : p.Loading ? Loc.F("Game.PartLoading", p.Name)
                : p.InGame ? Loc.F("Game.PartIn", p.Name, p.GameEmitters.Count)
                : Loc.F("Game.PartOut", p.Name);
            _parts.Children.Add(Line(text, p.Error != null ? "warn" : p.InGame ? "ok" : "dim", p.GamePath));
        }
    }

    private void ShowDifferences(bool canShoot)
    {
        _diffs.Children.Clear();
        var list = _host!.KnownDifferences();
        if (list.Count == 0)
            _diffs.Children.Add(Line(Loc.T("Game.NoDifferences"), "dim"));
        foreach (var (emitter, flag) in list)
        {
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            var check = new Button { Style = Res<Style>("Btn.Base"), IsEnabled = canShoot, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Top };
            Localized(check, "Game.Check");
            check.Click += (_, _) => _host!.GameShot("diff_" + Pfx.Core.Model.ProjectNames.Id(emitter + "_" + flag));
            DockPanel.SetDock(check, Dock.Left);
            row.Children.Add(check);
            row.Children.Add(Line(emitter + ": " + Loc.T("Flag." + flag), "text", Loc.T("Tip.Game.Difference")));
            _diffs.Children.Add(row);
        }
    }

    private void ShowLog()
    {
        _log.Children.Clear();
        if (_lines.Count == 0)
            _log.Children.Add(Line(Loc.T("Game.NoLog"), "dim"));
        foreach (var (text, kind) in _lines)
            _log.Children.Add(Line(text, kind));
    }

    private static TextBlock Line(string text, string kind, string? tip = null) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Res<Brush>(kind switch { "ok" => "B.Ok", "warn" => "B.Warn", "err" => "B.Err", "dim" => "B.TextDim", _ => "B.Text" }),
        Margin = new Thickness(0, 1, 0, 1),
        ToolTip = tip,
    };

    public IReadOnlyList<string> LogLines => _lines.Select(l => l.Text).ToList();

    public void Note(string text, string kind)
    {
        _lines.Insert(0, (text, kind));
        if (_lines.Count > LogShown)
            _lines.RemoveAt(_lines.Count - 1);
        if (IsVisible)
            ShowLog();
    }

    public void Timed(LinkTiming t)
    {
        string ms = t.Ms.ToString("0", CultureInfo.InvariantCulture);
        switch (t.Kind)
        {
            case TimingKind.Set:
                Note(Loc.F(t.Matches ? "Game.SetDone" : "Game.SetDiffers", t.What, ms), t.Matches ? "ok" : "warn");
                break;
            case TimingKind.Reload:
                Note(Loc.F("Game.ReloadDone", t.What, ms), "ok");
                break;
        }
    }

    // A viewport frame taken when a shot was asked for, shown beside the game's frame when it arrives.
    public void Expect(string name, RgbaImage? viewFrame, float? age) => _viewFrames[name] = (viewFrame, age);

    // The effect time of the latest viewport frame shown beside a game frame (null: the preview's own time).
    public float? LastViewAge { get; private set; }

    public void ExpectSeries(string prefix, int count, bool baseline)
    {
        _seriesFrames[prefix] = new List<BmpFrame>();
        _seriesWanted[prefix] = count;
        Note(Loc.F(baseline ? "Game.BaselineStarted" : "Game.SeriesStarted", count), "text");
    }

    public BitmapSource? GameBitmap { get; private set; }
    public GameShot? LastShot { get; private set; }
    public SeriesStats? LastSeries { get; private set; }

    public void Shot(GameShot shot)
    {
        if (!File.Exists(shot.File))
        {
            Note(Loc.F("Game.ShotMissing", shot.File), "err");
            return;
        }
        string prefix = shot.Name.Contains('_') ? shot.Name[..shot.Name.LastIndexOf('_')] : shot.Name;
        if (_seriesFrames.TryGetValue(prefix, out var frames) && !_viewFrames.ContainsKey(shot.Name))
        {
            try
            {
                frames.Add(BmpFrame.Read(shot.File));
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                Note(e.Message, "err");
            }
            if (frames.Count >= _seriesWanted[prefix])
                SeriesDone(prefix, frames);
            return;
        }
        LastShot = shot;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(shot.File);
            bmp.EndInit();
            bmp.Freeze();
            GameBitmap = bmp;
            _gameImage.Source = bmp;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException)
        {
            Note(e.Message, "err");
            return;
        }
        _gameCaption.Text = Loc.F("Game.GameCaption", shot.Name, (shot.GameTime / 1000.0).ToString("0.00", CultureInfo.InvariantCulture),
            shot.FrameMs.ToString("0.0", CultureInfo.InvariantCulture), shot.RoundTripMs.ToString("0", CultureInfo.InvariantCulture),
            (shot.Fov * 180 / Math.PI).ToString("0.0", CultureInfo.InvariantCulture));
        if (_viewFrames.Remove(shot.Name, out var view) && view.Frame != null)
        {
            LastViewAge = view.Age;
            _viewImage.Source = SheetBitmaps.Of(MainWindow.Opaque(view.Frame), SheetView.Checker);
            _viewCaption.Text = view.Age is { } age
                ? Loc.F("Game.ViewCaptionAge", view.Frame.Width, view.Frame.Height, age.ToString("0.00", CultureInfo.InvariantCulture))
                : Loc.F("Game.ViewCaption", view.Frame.Width, view.Frame.Height);
        }
        Note(Loc.F("Game.ShotDone", shot.Name, shot.RoundTripMs.ToString("0", CultureInfo.InvariantCulture)), "ok");
    }

    private void SeriesDone(string prefix, List<BmpFrame> frames)
    {
        _seriesFrames.Remove(prefix);
        var stats = SeriesStats.Of(frames);
        LastSeries = stats;
        string mean = stats.MeanDiff.ToString("0.00", CultureInfo.InvariantCulture);
        string jump = stats.JumpPercent.ToString("0.000", CultureInfo.InvariantCulture);
        string max = stats.MaxJumpPercent.ToString("0.000", CultureInfo.InvariantCulture);
        if (prefix.StartsWith("base", StringComparison.Ordinal))
        {
            _baseline = stats;
            Note(Loc.F("Game.BaselineDone", stats.Frames, mean, jump, max), "ok");
            return;
        }
        string against = _baseline == null ? Loc.T("Game.NoBaseline")
            : Loc.F("Game.AgainstBaseline", _baseline.MeanDiff.ToString("0.00", CultureInfo.InvariantCulture), _baseline.JumpPercent.ToString("0.000", CultureInfo.InvariantCulture));
        Note(Loc.F("Game.SeriesDone", stats.Frames, mean, jump, max, against), "ok");
    }
}
