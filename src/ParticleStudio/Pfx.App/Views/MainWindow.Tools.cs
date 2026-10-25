using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Pfx.App.Core;
using Pfx.App.Model;
using Pfx.App.Viewport;
using Pfx.Core.Editing;
using Pfx.Core.Model;
using Pfx.Core.Text;
using Pfx.Render;

namespace Pfx.App.Views;

// The viewport tools of P8 (spec 5.1): V move, R rotate, S scale, C shape and cone, P draw path; their options bar
// and the orientation presets of spec 3.5. The tools draw into the viewport's overlay and line pass; their values go
// through the documents like any inspector edit.
public partial class MainWindow : IToolHost
{
    // Crossed planes: 2 to 4 emitters at even yaw (spec 3.5); three by default, the usual "volumetric" cross.
    private const int CrossMin = 2, CrossMax = 4;
    private static readonly Dictionary<string, ToolKind> ToolKinds = new()
    {
        ["select"] = ToolKind.Move, ["rotate"] = ToolKind.Rotate, ["scale"] = ToolKind.Scale, ["shape"] = ToolKind.Shape, ["path"] = ToolKind.Path,
    };
    private static readonly Dictionary<int, string> ToolKeys = new()
    {
        ['V'] = "select", ['R'] = "rotate", ['S'] = "scale", ['C'] = "shape", ['P'] = "path",
    };

    private GizmoTool _gizmo = null!;
    private PathTool _pathTool = null!;
    private string _toolTag = "select";
    private int _crossCount = 3;
    private readonly List<LineVertex> _toolLineList = new();

    public GizmoTool Gizmo => _gizmo;
    public PathTool PathDrawing => _pathTool;

    // ---- IToolHost ----

    public IReadOnlyList<EmitterView> SelectedEmitters => _partSelected ? Array.Empty<EmitterView>() : SelectedViews();
    public EffectPreview? PreviewOf(EffectDoc doc) => _pipeline.PreviewOf(doc);
    public PreviewSettings PreviewSettings => _pipeline.Settings;
    public void SelectEmitters(EffectDoc doc, IReadOnlyList<EnfBlock> blocks) => Select(doc, blocks, false);

    private void InitTools()
    {
        _gizmo = new GizmoTool(this);
        _pathTool = new PathTool(this);
        _pathTool.Finished += OnPathFinished;
        _viewport.ComposeLines = lines =>
        {
            lines.AddRange(_toolLineList);
            ComposeProjectLines(lines);
        };
        _viewport.KeyPressed += OnViewportKey;
        foreach (var use in Enum.GetValues<PathUse>())
        {
            var item = new ComboBoxItem { Tag = use };
            item.SetResourceReference(ContentProperty, "Use." + use);
            item.SetResourceReference(ToolTipProperty, "Tip.Use." + use);
            UseBox.Items.Add(item);
        }
        UseBox.SelectedIndex = (int)_pathTool.Use;
        SetTool("select");
    }

    public void SetTool(string tag)
    {
        // The ready-solutions tool (Shift+R) is a panel, not a viewport tool: it opens the panel and the viewport keeps
        // its tool.
        if (tag == "recipe")
        {
            foreach (var b in ToolStrip.Children.OfType<ToggleButton>())
                b.IsChecked = (string)b.Tag == _toolTag;
            ShowPane("recipes");
            return;
        }
        if (!ToolKinds.TryGetValue(tag, out var kind))
            return;
        _toolTag = tag;
        foreach (var b in ToolStrip.Children.OfType<ToggleButton>())
            b.IsChecked = (string)b.Tag == tag;
        _gizmo.Kind = kind;
        _viewport.Tool = kind == ToolKind.Path ? _pathTool : _gizmo;
        CircleButton.IsChecked = _pipeline.Settings.Motion == MotionKind.Circle;
        ShowOptions(tag);
    }

    // Tool shortcuts and undo also work while the viewport window has the keyboard (WPF does not see its keys).
    private void OnViewportKey(int vk, bool ctrl, bool shift)
    {
        if (ctrl && vk == 'Z')
            UndoActive();
        else if (ctrl && vk == 'Y')
            RedoActive();
        else if (!ctrl && shift && vk == 'R')
            SetTool("recipe");
        else if (!ctrl && ToolKeys.TryGetValue(vk, out var tag))
            SetTool(tag);
        else if (!ctrl && vk == ' ')
            TogglePlay();
    }

    private void ComposeTools(OverlayBuilder o)
    {
        _toolLineList.Clear();
        if (_gizmo.Kind == ToolKind.Path)
            _pathTool.Compose(o, _toolLineList);
        else
            _gizmo.Compose(o, _toolLineList);
    }

    // ---- options bar ----

    private void ShowToolOptions(string what)
    {
        bool gizmo = what is "select" or "rotate" or "scale" or "shape";
        GizmoOptions.Visibility = gizmo ? Visibility.Visible : Visibility.Collapsed;
        PathOptions.Visibility = what == "path" ? Visibility.Visible : Visibility.Collapsed;
        SpaceOptions.Visibility = what == "select" ? Visibility.Visible : Visibility.Collapsed;
        StepOptions.Visibility = what is "select" or "scale" or "shape" ? Visibility.Visible : Visibility.Collapsed;
        AngleOptions.Visibility = what is "rotate" or "shape" ? Visibility.Visible : Visibility.Collapsed;
        PresetOptions.Visibility = what == "rotate" ? Visibility.Visible : Visibility.Collapsed;
        SpaceWorld.IsChecked = _gizmo.Space == MoveSpace.World;
        SpaceSource.IsChecked = _gizmo.Space == MoveSpace.Source;
        SnapButton.IsChecked = _gizmo.Snap;
        StepMetresBox.Text = Loc.Num(_gizmo.StepMetres);
        StepDegreesBox.Text = Loc.Num(_gizmo.StepDegrees);
        CrossBox.Text = _crossCount.ToString(CultureInfo.InvariantCulture);
        PlaneGround.IsChecked = _pathTool.Plane == PathPlane.Ground;
        PlaneWall.IsChecked = _pathTool.Plane == PathPlane.Wall;
        PlaneFree.IsChecked = _pathTool.Plane == PathPlane.Free;
        ChainOptions.Visibility = _pathTool.Use == PathUse.Chain ? Visibility.Visible : Visibility.Collapsed;
        YawAlongButton.Visibility = _pathTool.Use == PathUse.Source ? Visibility.Visible : Visibility.Collapsed;
        YawAlongButton.IsChecked = _pathTool.YawAlongPath;
        ChainBox.Text = _pathTool.ChainCount.ToString(CultureInfo.InvariantCulture);
        ShowPathReadout();
    }

    private void ShowPathReadout()
    {
        var r = _pathTool.Result;
        PathReadout.Text = _pathTool.Busy ? Loc.T("Path.Busy") : r?.Readout ?? Loc.T("Path.Hint." + _pathTool.Use);
        PathReadout.ToolTip = r == null ? Loc.T("Tip.PathReadout")
            : r.Readout + "\n" + Loc.F("Path.PerPoint", string.Join("  ", r.Errors.Select(e => Loc.Num(Math.Round(e, 3)))));
    }

    private void OnPathFinished(PathResult? result)
    {
        ShowPathReadout();
        PathResultReady?.Invoke(result);
    }

    public event Action<PathResult?>? PathResultReady;

    private void OnSpace(object sender, RoutedEventArgs e)
    {
        _gizmo.Space = sender == SpaceSource ? MoveSpace.Source : MoveSpace.World;
        ShowOptions(_options);
    }

    private void OnSnap(object sender, RoutedEventArgs e)
    {
        _gizmo.Snap = SnapButton.IsChecked == true;
        ShowOptions(_options);
    }

    public void SetPathPlane(PathPlane plane)
    {
        _pathTool.Plane = plane;
        ShowOptions(_options);
    }

    private void OnPlane(object sender, RoutedEventArgs e) => SetPathPlane(Enum.Parse<PathPlane>((string)((FrameworkElement)sender).Tag));

    public void SetPathUse(PathUse use)
    {
        if (UseBox.SelectedIndex != (int)use)
        {
            UseBox.SelectedIndex = (int)use;
            return;
        }
        if (_pathTool.Use == use)
            return;
        _pathTool.Use = use;
        _pathTool.Rerun();
        ShowOptions(_options);
    }

    private void OnPathUse(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _pathTool == null || UseBox.SelectedItem is not ComboBoxItem { Tag: PathUse use })
            return;
        SetPathUse(use);
    }

    private void OnYawAlong(object sender, RoutedEventArgs e)
    {
        _pathTool.YawAlongPath = YawAlongButton.IsChecked == true;
        _pathTool.Rerun();
        ShowOptions(_options);
    }

    // The tool boxes of the options bar (called by CommitOption): out-of-range values are clamped and said.
    private bool CommitToolOption(TextBox box, double v, Func<double, double, string, float> clamp)
    {
        if (box == StepMetresBox)
            _gizmo.StepMetres = clamp(0.001, 100, "Lbl.StepMetres");
        else if (box == StepDegreesBox)
            _gizmo.StepDegrees = clamp(0.1, 90, "Lbl.StepDegrees");
        else if (box == CrossBox)
            _crossCount = (int)clamp(CrossMin, CrossMax, "Preset.cross");
        else if (box == ChainBox)
        {
            int n = (int)clamp(PathTool.MinChain, PathTool.MaxChain, "Lbl.Chain");
            if (n != _pathTool.ChainCount)
            {
                _pathTool.ChainCount = n;
                _pathTool.Rerun();
            }
        }
        else
            return false;
        return true;
    }

    // ---- orientation presets (spec 3.5): each one undo step of plain keys ----

    private void OnPreset(object sender, RoutedEventArgs e) => ApplyPreset((string)((FrameworkElement)sender).Tag);

    public void ApplyPreset(string kind)
    {
        var doc = _active;
        var views = SelectedEmitters.Where(v => doc != null && !doc.Locked.Contains(v.Block)).ToList();
        if (doc == null || views.Count == 0)
        {
            Toast(Loc.T("Toast.PresetNoEmitter"), "info");
            return;
        }
        var label = Loc.Later("Undo.Preset." + kind);
        string merge = "preset:" + Guid.NewGuid().ToString("N");
        if (kind == "cross")
        {
            var template = views[0];
            double yaw = ToolEdits.Vec(template, "Angles")[0];
            var planes = ToolEdits.CloneChain(doc, template, _crossCount, label, merge);
            for (int k = 0; k < planes.Count; k++)
                ToolEdits.Steps(doc, label, merge, new[]
                {
                    ToolEdits.SetWord(planes[k], "BillboardingType", "None"),
                    ToolEdits.SetVec(planes[k], "Angles", new[] { Math.Round(yaw + k * 180.0 / planes.Count, 6), 0, 0 }),
                });
            doc.Seal();
            Select(doc, planes.Select(p => p.Block).ToList(), false);
            return;
        }
        foreach (var v in views)
        {
            var angles = ToolEdits.Vec(v, "Angles");
            Func<IEdit?>[] makers = kind switch
            {
                // Vanilla decals lie flat with Angles 0 90 0 + BillboardingType None (prompt 2.3).
                "flat" => new[] { ToolEdits.SetWord(v, "BillboardingType", "None"), ToolEdits.SetVec(v, "Angles", new double[] { 0, 90, 0 }) },
                // None spans the emitter X and Y axes (R0): upright, turned only by the yaw it already has.
                "wall" => new[] { ToolEdits.SetWord(v, "BillboardingType", "None"), ToolEdits.SetVec(v, "Angles", new[] { angles[0], 0, 0 }) },
                // LockedAxis turns about the emitter Y axis towards the camera: an upright column.
                "beam" => new[] { ToolEdits.SetWord(v, "BillboardingType", "LockedAxis"), ToolEdits.SetVec(v, "Angles", new double[] { 0, 0, 0 }) },
                // Horizontal all around (polar 90 over the full azimuth), sprites along their flight.
                _ => new[] { ToolEdits.SetVec(v, "ConeAngle", new double[] { 360, 180, 180 }), ToolEdits.SetNum(v, "VelAngle", 1) },
            };
            ToolEdits.Steps(doc, label, merge, makers);
        }
        doc.Seal();
    }
}
