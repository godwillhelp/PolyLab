using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pfx.App.Model;
using Pfx.App.Preview;
using Pfx.App.Timeline;
using Pfx.Core.Text;
using Pfx.Sim;

namespace Pfx.App.Views;

public interface ITimelineHost
{
    PreviewPipeline Pipeline { get; }
    IReadOnlyList<EffectDoc> Docs { get; }
    IReadOnlyList<EmitterParams> ParamsOf(EffectDoc doc);
    void TogglePlay();
    void RestartPreview();
    void StepPreview();
    void PickTrack(EffectDoc doc, EnfBlock? emitter);
    // The open project's states while the preview plays them, else null (the sample cycle is offered instead).
    ProjectTimeline? ProjectTimeline { get; }
}

// The timeline panel: TimelineModel builds the tracks from the open parts, TimelineCanvas draws them; this view keeps
// the shown time range, the work area and the loop, and turns scrubbing into exact seeks of the pipeline. While the
// user scrubs, playback pauses (as in After Effects) and resumes when it was on.
public partial class TimelineView : UserControl
{
    private (double From, double To)? _area;
    private bool _resume;
    private bool _scrubbing;
    private bool _fitted;
    private bool _queued;

    public TimelineView()
    {
        InitializeComponent();
        Canvas.Scrubbed = Scrub;
        Canvas.ScrubEnded = EndScrub;
        Canvas.AreaSet = (from, to) => SetArea(from, to, true);
        Canvas.TrackPicked = t => { if (t.Doc != null) Host?.PickTrack(t.Doc, t.Emitter); };
        IsVisibleChanged += (_, _) => { if (IsVisible && _queued) Rebuild(); };
    }

    // Documents changed: the tracks are made again once the edits of this dispatcher turn are done (a drag sends many),
    // and only while the timeline is on screen.
    public void Invalidate()
    {
        if (_queued)
            return;
        _queued = true;
        Dispatcher.BeginInvoke(() => { if (_queued && IsVisible) Rebuild(); }, System.Windows.Threading.DispatcherPriority.Background);
    }

    public ITimelineHost? Host { get; set; }
    public IReadOnlyList<TimelineTrack> Tracks => Canvas.Tracks;
    public StateSample? Sample => SampleBox.SelectedItem is ComboBoxItem { Tag: StateSample s } ? s : null;

    public void Init()
    {
        SampleBox.Items.Clear();
        var none = new ComboBoxItem { Tag = null };
        none.SetResourceReference(ContentProperty, "Sample.none");
        none.SetResourceReference(ToolTipProperty, "Tip.StateSample");
        SampleBox.Items.Add(none);
        foreach (var s in StateSample.All)
        {
            var item = new ComboBoxItem { Tag = s };
            item.SetResourceReference(ContentProperty, "Sample." + s.Id);
            item.SetResourceReference(ToolTipProperty, "Tip.StateSample");
            SampleBox.Items.Add(item);
        }
        SampleBox.SelectedIndex = 0;
    }

    private List<(EffectDoc, IReadOnlyList<EmitterParams>)> Parts() =>
        Host == null ? new() : Host.Docs.Select(d => (d, Host.ParamsOf(d))).ToList();

    // The whole scene (or the project's state cycle) in view again, e.g. when the project's states start playing.
    public void FitNow()
    {
        Fit(Parts());
        Rebuild();
    }

    // The documents or the sample changed: tracks again, and the shown range fitted the first time there is a scene.
    public void Rebuild()
    {
        _queued = false;
        var parts = Parts();
        if (!_fitted && parts.Count > 0)
        {
            Fit(parts);
            _fitted = true;
        }
        var project = Host?.ProjectTimeline;
        Canvas.Tracks = TimelineModel.Build(parts, project == null ? Sample : null, Canvas.ViewTo, project);
        SampleBox.IsEnabled = project == null;
        CycleButton.IsEnabled = Sample != null || project != null;
        UpdateArea();
        Canvas.InvalidateVisual();
    }

    private void Fit(List<(EffectDoc, IReadOnlyList<EmitterParams>)> parts)
    {
        double length = TimelineModel.SceneLength(parts);
        if (Host?.ProjectTimeline is { } project)
            length = Math.Max(length, project.Length);
        else if (Sample is { } s)
            length = Math.Max(length, TimelineModel.Cycle(s, TimelineModel.SceneLoop(parts))[^1].To);
        Canvas.ViewFrom = 0;
        Canvas.ViewTo = length * 1.05;
    }

    // After every presented frame: the playhead, the time field, the play button.
    public void Tick()
    {
        if (Host == null)
            return;
        double t = Host.Pipeline.Time;
        if (Math.Abs(Canvas.Playhead - t) > 1e-4)
            Canvas.Playhead = t;
        if (!TimeBox.IsKeyboardFocusWithin)
            TimeBox.Text = t.ToString("0.00", CultureInfo.InvariantCulture);
        bool playing = Host.Pipeline.Playing;
        PlayIcon.Data = (Geometry)FindResource(playing ? "Ic.Pause" : "Ic.Play");
        PlayButton.ToolTip = Loc.T(playing ? "Tip.PauseTimeline" : "Tip.Play");
        if (playing && t > Canvas.ViewTo && !_scrubbing)
        {
            double span = Canvas.ViewTo - Canvas.ViewFrom;
            Canvas.ViewFrom = Math.Max(0, t - span * 0.1);
            Canvas.ViewTo = Canvas.ViewFrom + span;
            Rebuild();
        }
    }

    private void Scrub(double t)
    {
        if (Host == null)
            return;
        if (!_scrubbing)
        {
            _scrubbing = true;
            _resume = Host.Pipeline.Playing;
            Host.Pipeline.Playing = false;
        }
        Host.Pipeline.Seek((float)t);
        Canvas.Playhead = t;
    }

    private void EndScrub()
    {
        _scrubbing = false;
        if (_resume && Host != null)
            Host.Pipeline.Playing = true;
        _resume = false;
    }

    private void SetArea(double from, double to, bool loop)
    {
        _area = to > from ? (from, to) : null;
        if (loop && _area != null)
            LoopButton.IsChecked = true;
        UpdateArea();
    }

    private void UpdateArea()
    {
        Canvas.Area = _area;
        Canvas.Looping = LoopButton.IsChecked == true && _area != null;
        AreaText.Text = _area is { } a ? Loc.F("Lbl.Area", TimelineModel.Fmt(a.From), TimelineModel.Fmt(a.To)) : Loc.T("Lbl.NoArea");
        if (Host != null)
            Host.Pipeline.Loop = Canvas.Looping ? ((float)_area!.Value.From, (float)_area.Value.To) : null;
        Canvas.InvalidateVisual();
    }

    private void OnPlay(object sender, RoutedEventArgs e) => Host?.TogglePlay();

    private void OnRestart(object sender, RoutedEventArgs e) => Host?.RestartPreview();

    private void OnStep(object sender, RoutedEventArgs e) => Host?.StepPreview();

    private void OnTimeKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Host == null)
            return;
        if (Core.Expr.TryApply(TimeBox.Text, Host.Pipeline.Time, out double t) && double.IsFinite(t))
        {
            Host.Pipeline.Playing = false;
            Host.Pipeline.Seek((float)Math.Max(0, t));
        }
        Keyboard.ClearFocus();
    }

    private void OnLoop(object sender, RoutedEventArgs e)
    {
        if (LoopButton.IsChecked == true && _area == null)
            _area = (0, TimelineModel.SceneLength(Parts()));
        UpdateArea();
    }

    private void OnMarkIn(object sender, RoutedEventArgs e)
    {
        if (Host == null)
            return;
        double t = Host.Pipeline.Time;
        double to = _area is { } a && a.To > t ? a.To : Math.Max(t + 0.1, TimelineModel.SceneLength(Parts()));
        SetArea(t, to, false);
    }

    private void OnMarkOut(object sender, RoutedEventArgs e)
    {
        if (Host == null)
            return;
        double t = Host.Pipeline.Time;
        double from = _area is { } a && a.From < t ? a.From : 0;
        SetArea(from, t, false);
    }

    private void OnFit(object sender, RoutedEventArgs e)
    {
        Fit(Parts());
        Rebuild();
    }

    private void OnSample(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            Fit(Parts());
            Rebuild();
        }
    }

    // The whole sample cycle as the work area, looping, from its start (spec 3.6: preview plays a whole cycle).
    private void OnCycle(object sender, RoutedEventArgs e)
    {
        if (Host == null)
            return;
        if (Host.ProjectTimeline is { } project)
            SetArea(0, project.Length, true);
        else if (Sample is { } s)
            SetArea(0, TimelineModel.Cycle(s, TimelineModel.SceneLoop(Parts()))[^1].To, true);
        else
            return;
        Fit(Parts());
        Rebuild();
        Host.Pipeline.Seek(0);
        Host.Pipeline.Playing = true;
    }
}
