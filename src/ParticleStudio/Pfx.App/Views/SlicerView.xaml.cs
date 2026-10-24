using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Pfx.App.Core;
using Pfx.App.Textures;

namespace Pfx.App.Views;

// The frame slicer panel (board 12): SlicerModel holds the state, this view lays it out and passes gestures and typed
// values on. Fields and handles show the same document values (spec 1.1 rule 4).
public partial class SlicerView : UserControl
{
    private const double SampleSide = 40;

    private readonly List<Border> _samples = new();

    public SlicerView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => Refresh();
    }

    public SlicerModel? Model { get; private set; }
    public SpriteCanvas SpritePanel => Sprite;
    public SheetCanvas SheetPanel => Sheet;

    public void Init(SlicerModel model)
    {
        Model = model;
        Sheet.Model = model;
        Sprite.Model = model;
        model.Changed += Refresh;
        model.Ticked += OnTick;
        for (int k = 0; k < SlicerModel.Samples; k++)
        {
            var b = new Border { Width = SampleSide, Height = SampleSide, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 4, 0), BorderThickness = new Thickness(1) };
            b.SetResourceReference(Border.BorderBrushProperty, "B.Line");
            _samples.Add(b);
            SampleRow.Children.Add(b);
        }
        foreach (double s in SlicerModel.Speeds)
        {
            var seg = new ToggleButton { Style = (Style)FindResource("Btn.Seg"), Tag = s, Margin = new Thickness(0, 0, -1, 0) };
            double speed = s;
            seg.Click += (_, _) => model.Speed = speed;
            SpeedRow.Children.Add(seg);
        }
        Refresh();
    }

    // Only while shown (a hidden panel refreshes when it comes into view): the model changes with every edit.
    public void Refresh()
    {
        var m = Model;
        if (m == null || !IsVisible)
            return;
        var em = m.Emitter;
        bool hasTexture = m.Levels != null;
        TargetText.Text = em == null ? Loc.T("Slicer.NoEmitter")
            : (m.TexturePath != null ? Path.GetFileName(m.TexturePath) : m.TextureGamePath ?? Loc.T("Slicer.NoTexture"))
              + (m.SelectedEmitters > 1 ? Loc.F("Slicer.OfMany", em.Name, m.SelectedEmitters) : " · " + em.Name);
        TargetText.ToolTip = em == null ? Loc.T("Tip.SlicerNoEmitter") : Loc.F("Tip.SlicerTarget", m.TextureGamePath ?? Loc.T("Val.None"),
            m.Material?.DiskPath ?? Loc.T("Val.None"), em.Name);
        EmptyText.Text = em == null ? Loc.T("Slicer.EmptyEmitter") : m.Material == null ? Loc.T("Slicer.EmptyMaterial")
            : m.TextureGamePath == null ? Loc.T("Slicer.EmptyTexture") : m.LoadError ?? (m.Loading ? Loc.T("Slicer.Loading") : "");
        EmptyText.Visibility = hasTexture && m.LoadError == null ? Visibility.Collapsed : Visibility.Visible;
        ViewChecker.IsChecked = m.View == SheetView.Checker;
        ViewBlack.IsChecked = m.View == SheetView.Black;
        ViewAlpha.IsChecked = m.View == SheetView.Alpha;
        NumbersButton.IsChecked = m.Numbers;
        SetBox(TileUBox, m.TileU.ToString(CultureInfo.InvariantCulture));
        SetBox(TileVBox, m.TileV.ToString(CultureInfo.InvariantCulture));
        TileUBox.IsEnabled = TileVBox.IsEnabled = m.MaterialEditable;
        if (m.Summary is { } sum)
        {
            double fw = (double)sum.Width / m.TileU, fh = (double)sum.Height / m.TileV;
            bool whole = sum.Width % m.TileU == 0 && sum.Height % m.TileV == 0;
            FramePx.Text = Loc.F("Slicer.FramePx", Loc.Num(Math.Round(fw, 2)), Loc.Num(Math.Round(fh, 2)));
            FramePx.SetResourceReference(TextBlock.ForegroundProperty, whole ? "B.TextDim" : "B.Warn");
            FramePx.ToolTip = Loc.T(whole ? "Tip.SlicerFramePx" : "Tip.SlicerFramePxFraction");
        }
        else
        {
            FramePx.Text = "";
        }
        FillMips(m);
        var p = m.Params;
        bool editable = m.Editable;
        PlayButton.IsChecked = m.Playing;
        PlayButton.Content = Loc.T(m.Playing ? "Slicer.Pause" : "Slicer.Play");
        SetBox(FpsBox, Loc.Num(em?.Number("AnimFPS") ?? 0));
        FpsBox.IsEnabled = editable;
        OnceButton.IsChecked = p?.AnimOnce == true;
        RandomButton.IsChecked = p?.RandomFrame == true;
        ByAnimButton.IsChecked = p?.LifetimeByAnim == true;
        foreach (var b in new[] { OnceButton, RandomButton, ByAnimButton })
            b.IsEnabled = editable;
        foreach (ToggleButton seg in SpeedRow.Children)
        {
            double s = (double)seg.Tag;
            seg.Content = Loc.T("Slicer.Speed." + s.ToString("0.##", CultureInfo.InvariantCulture));
            seg.ToolTip = Loc.T("Tip.SlicerSpeed." + s.ToString("0.##", CultureInfo.InvariantCulture));
            seg.IsChecked = Math.Abs(m.Speed - s) < 1e-9;
        }
        SetBox(CenterXBox, Loc.Num(m.CenterX));
        SetBox(CenterYBox, Loc.Num(m.CenterY));
        SetBox(ScaleXBox, Loc.Num(m.ScaleX));
        SetBox(ScaleYBox, Loc.Num(m.ScaleY));
        foreach (var box in new[] { CenterXBox, CenterYBox, ScaleXBox, ScaleYBox })
            box.IsEnabled = editable;
        FileButton.IsEnabled = DefaultButton.IsEnabled = editable;
        MetresText.Text = em == null ? "" : Loc.F("Slicer.Metres", Loc.Num(Math.Round(m.SizeMultiplier * m.ScaleX, 3)), Loc.Num(Math.Round(m.SizeMultiplier * m.ScaleY, 3)));
        PlayInfo.Text = PlayText(m);
        PlayInfo.ToolTip = string.Join(Environment.NewLine, PlayInfo.Text, Loc.T("Slicer.Order"), Loc.T("Tip.SlicerOrder"));
        OnTick();
    }

    private static string PlayText(SlicerModel m)
    {
        if (m.Params is not { } p)
            return "";
        string frames = Loc.F("Slicer.Info.Frames", m.Frames);
        float rate = Pfx.Sim.Flipbook.Rate(p);
        string fps = Loc.F(p.AnimFps > 0 ? "Slicer.Info.Fps" : "Slicer.Info.Zero", Loc.Num(rate), Loc.Num(Math.Round(Pfx.Sim.Flipbook.PassSeconds(p), 3)));
        string life = p.LifetimeByAnim && p.AnimFps > 0 ? Loc.F("Slicer.Info.LifeByAnim", Loc.Num(Math.Round(p.BaseLifeTime, 3)))
            : Loc.F("Slicer.Info.Life", Loc.Num(p.LifeTime), Loc.Num(p.LifeTimeRnd));
        string passes = "";
        if (!p.AnimOnce && !(p.LifetimeByAnim && p.AnimFps > 0))
            passes = Loc.F("Slicer.Info.Passes", Loc.Num(Math.Round(p.LifeTime * rate / m.Frames, 2)),
                Loc.Num(Math.Round((p.LifeTime + p.LifeTimeRnd) * rate / m.Frames, 2)));
        else if (p.AnimOnce && p.LifeTime > Pfx.Sim.Flipbook.PassSeconds(p) && !p.LifetimeByAnim)
            passes = Loc.F("Slicer.Info.Holds", Loc.Num(Math.Round(p.LifeTime - Pfx.Sim.Flipbook.PassSeconds(p), 3)));
        return string.Join(" · ", new[] { frames, fps, life, passes }.Where(s => s.Length > 0));
    }

    private void FillMips(SlicerModel m)
    {
        var levels = m.Levels;
        int count = levels?.Length ?? 0;
        if (MipBox.Items.Count != count || (count > 0 && !Equals(((ComboBoxItem)MipBox.Items[0]).Tag, levels![0])))
        {
            MipBox.Items.Clear();
            for (int i = 0; i < count; i++)
                MipBox.Items.Add(new ComboBoxItem
                {
                    Content = Loc.F("Slicer.MipItem", i, levels![i].Width, levels[i].Height),
                    Tag = i == 0 ? levels[0] : null,
                    ToolTip = Loc.T("Tip.SlicerMip"),
                });
            if (count > 0)
                MipBox.SelectedIndex = 0;
        }
        MipBox.IsEnabled = count > 1;
    }

    private void OnTick()
    {
        var m = Model;
        if (m == null || !IsVisible)
            return;
        var samples = m.SampleParticles();
        for (int k = 0; k < _samples.Count; k++)
        {
            var b = _samples[k];
            if (k >= samples.Count || m.Levels is not { Length: > 0 } levels)
            {
                b.Background = null;
                b.ToolTip = Loc.T("Tip.SlicerSample");
                continue;
            }
            var s = samples[k];
            if (b.Background is not ImageBrush brush || brush.ImageSource != SheetBitmaps.Of(levels[0], m.View) || !Equals(b.Tag, s.Frame))
            {
                b.Background = new ImageBrush(SheetBitmaps.Of(levels[0], m.View))
                {
                    ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                    Viewbox = new Rect((double)(s.Frame % m.TileU) / m.TileU, (double)(s.Frame / m.TileU) / m.TileV, 1.0 / m.TileU, 1.0 / m.TileV),
                };
                b.Tag = s.Frame;
            }
            b.ToolTip = Loc.F(s.Held ? "Tip.SlicerSampleHeld" : "Tip.SlicerSampleAt", k + 1, Loc.Num(Math.Round(s.Age, 3)), s.Frame, s.Start);
        }
        Sheet.InvalidateVisual();
    }

    private static void SetBox(TextBox box, string text)
    {
        if (!box.IsKeyboardFocusWithin)
            box.Text = text;
    }

    private void OnView(object sender, RoutedEventArgs e)
    {
        if (Model != null)
            Model.View = Enum.Parse<SheetView>((string)((FrameworkElement)sender).Tag);
    }

    private void OnNumbers(object sender, RoutedEventArgs e)
    {
        if (Model != null)
            Model.Numbers = NumbersButton.IsChecked == true;
    }

    private void OnMip(object sender, SelectionChangedEventArgs e)
    {
        Sheet.Mip = Math.Max(0, MipBox.SelectedIndex);
        Sheet.InvalidateVisual();
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (Model != null)
            Model.Playing = !Model.Playing;
    }

    private void OnFlag(object sender, RoutedEventArgs e)
    {
        Model?.Toggle((string)((FrameworkElement)sender).Tag);
        Refresh();
    }

    private void OnPivotFile(object sender, RoutedEventArgs e) => Model?.ResetPivotToFile();

    private void OnPivotDefault(object sender, RoutedEventArgs e) => Model?.DefaultPivot();

    private void OnFieldKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Commit((TextBox)sender);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Keyboard.ClearFocus();
            Refresh();
            e.Handled = true;
        }
    }

    private void OnFieldLost(object sender, KeyboardFocusChangedEventArgs e) => Commit((TextBox)sender);

    // Typed values take expressions (0.5*2, +=0.1); text left as it was writes nothing.
    public void Commit(TextBox box)
    {
        var m = Model;
        if (m?.Emitter == null)
            return;
        double now = box == TileUBox ? m.TileU : box == TileVBox ? m.TileV : box == FpsBox ? m.Emitter.Number("AnimFPS") ?? 0
            : box == CenterXBox ? m.CenterX : box == CenterYBox ? m.CenterY : box == ScaleXBox ? m.ScaleX : m.ScaleY;
        if (box.Text.Trim() == Loc.Num(now) || !Expr.TryApply(box.Text, now, out double v) || !double.IsFinite(v))
        {
            box.Text = Loc.Num(now);
            return;
        }
        if (box == TileUBox)
            m.SetTiles((int)Math.Round(v), m.TileV);
        else if (box == TileVBox)
            m.SetTiles(m.TileU, (int)Math.Round(v));
        else if (box == FpsBox)
            m.SetFps(v);
        else if (box == CenterXBox)
            m.SetPivot(v, m.CenterY, false);
        else if (box == CenterYBox)
            m.SetPivot(m.CenterX, v, false);
        else if (box == ScaleXBox)
            m.SetScale(v, m.ScaleY, false);
        else
            m.SetScale(m.ScaleX, v, false);
        Refresh();
    }

    public TextBox TileUField => TileUBox;
    public TextBox TileVField => TileVBox;
    public TextBox FpsField => FpsBox;
    public TextBox CenterXField => CenterXBox;
}
