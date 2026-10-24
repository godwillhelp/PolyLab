using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;
using Pfx.App.Core;
using Pfx.App.Textures;
using Pfx.Textures;

namespace Pfx.App.Views;

public interface IAtlasHost
{
    SlicerModel Slicer { get; }
    // texconv for DXT, or null with the reason (no path in the settings, file missing).
    Texconv? TexconvTool(out string? why);
    CpuUpscaler? UpscalerTool(out string? why);
    string? GamePathOf(string file);
    // A texture file was written: decoded copies and GPU views of it are stale.
    void TextureWritten(string path);
    void Toast(string text, string kind);
}

// The atlas builder panel (spec 3.2, board 4): AtlasBuilder holds frames, options and the built result; this view lays
// it out. Pickers are the OS file dialogs (the only dialogs the tool opens).
public partial class AtlasView : UserControl
{
    private const string Pictures = "*.png;*.tga;*.dds;*.edds;*.paa";
    private bool _loading;

    public AtlasView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => Refresh();
    }

    public AtlasBuilder Builder { get; } = new();
    public IAtlasHost? Host { get; set; }
    public SheetCanvas PreviewPanel => Preview;

    public void Init()
    {
        Preview.Model = Builder;
        Builder.Changed += Refresh;
        Refresh();
    }

    // Only while shown (a hidden panel refreshes when it comes into view): the shell calls this on every document change.
    public void Refresh()
    {
        if (!IsVisible)
            return;
        _loading = true;
        var b = Builder;
        var r = b.Result;
        FrameList.ItemsSource = b.Frames.Select((f, i) => new ListBoxItem
        {
            Content = $"{i + 1}. {f.Name}",
            ToolTip = Loc.F("Tip.AtlasFrame", f.Name, f.Image.Width, f.Image.Height),
        }).ToList();
        FramesEmpty.Visibility = b.Frames.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = b.Frames.Count == 0 ? "" : Loc.F("Atlas.Count", b.Frames.Count, b.Frames[0].Image.Width, b.Frames[0].Image.Height);
        RemoveButton.IsEnabled = ClearButton.IsEnabled = b.Frames.Count > 0;
        FromSheetButton.IsEnabled = Host?.Slicer.Levels != null;
        Dxt1Button.IsChecked = b.Format == EddsFormat.Dxt1;
        Dxt5Button.IsChecked = b.Format == EddsFormat.Dxt5;
        RgbaButton.IsChecked = b.Format == EddsFormat.Rgba8;
        FillSides(WidthBox, b.Width, b.MaxSide, true);
        FillSides(HeightBox, b.Height, b.MaxSide, true);
        FillSides(MaxBox, b.MaxSide, 4096, false);
        if (!GammaBox.IsKeyboardFocusWithin)
            GammaBox.Text = Loc.Num(b.Gamma);
        if (!FpsBox.IsKeyboardFocusWithin)
            FpsBox.Text = Loc.Num(b.Fps);
        BleedButton.IsChecked = b.Bleed;
        PackButton.IsChecked = b.PackIntensity;
        PackButton.IsEnabled = b.Format == EddsFormat.Dxt5;
        MatchButton.IsChecked = b.MatchMean;
        UpscaleButton.Content = Loc.T(b.Upscaling ? "Atlas.UpscaleCancel" : "Atlas.Upscale");
        UpscaleButton.IsEnabled = b.Frames.Count > 0;
        DropUpscaleButton.IsEnabled = b.HasUpscaled && !b.Upscaling;
        UpscaleText.Text = b.Upscaling ? Loc.F("Atlas.Upscaling", b.UpscaleProgress.Done, b.UpscaleProgress.Total)
            : b.HasUpscaled ? Loc.T("Atlas.Upscaled") : "";
        string? why = null;
        bool dxt = b.Format != EddsFormat.Rgba8;
        bool canExport = r != null && !b.Building && (!dxt || Host?.TexconvTool(out why) != null);
        ExportButton.IsEnabled = canExport;
        ExportButton.ToolTip = r == null ? Loc.T("Tip.AtlasExportEmpty") : why != null ? Loc.F("Tip.AtlasExportNoTexconv", why) : Loc.T("Tip.AtlasExport");
        var slicer = Host?.Slicer;
        string? applyWhy = r == null ? Loc.T("Tip.AtlasApplyEmpty") : b.LastExport == null ? Loc.T("Tip.AtlasApplyExportFirst")
            : slicer?.Emitter == null ? Loc.T("Tip.AtlasApplyNoEmitter") : slicer.Material == null ? Loc.T("Tip.AtlasApplyNoMaterial")
            : !slicer.Editable ? Loc.T("Tip.AtlasApplyLocked") : Host?.GamePathOf(b.LastExport) == null ? Loc.T("Tip.AtlasApplyOutside") : null;
        ApplyButton.IsEnabled = applyWhy == null;
        ApplyButton.ToolTip = applyWhy ?? Loc.F("Tip.AtlasApply", slicer?.Emitter?.Name, Path.GetFileName(slicer?.Material?.DiskPath));
        ExportText.Text = b.LastExport != null ? Loc.F("Atlas.LastExport", Path.GetFileName(b.LastExport)) : "";
        ExportText.ToolTip = b.LastExport;
        ViewChecker.IsChecked = b.View == SheetView.Checker;
        ViewBlack.IsChecked = b.View == SheetView.Black;
        ViewAlpha.IsChecked = b.View == SheetView.Alpha;
        MipBox.Items.Clear();
        if (r != null)
            for (int i = 0; i < r.Levels.Length; i++)
                MipBox.Items.Add(new ComboBoxItem { Content = Loc.F("Slicer.MipItem", i, r.Levels[i].Width, r.Levels[i].Height), ToolTip = Loc.T("Tip.AtlasMip") });
        MipBox.SelectedIndex = r != null ? Math.Min(Preview.Mip, r.Levels.Length - 1) : -1;
        MipBox.IsEnabled = r != null;
        MemoryText.Text = r == null ? "" : Loc.F("Atlas.Memory", r.Format.ToString().ToUpperInvariant(), r.Levels[0].Width, r.Levels[0].Height,
            r.Levels.Length, Loc.Num(Math.Round(r.GpuBytes / 1048576.0, 2)));
        NoteList.ItemsSource = null;
        NoteList.ItemsSource = b.Error != null ? new[] { b.Error } : r?.Notes;
        PreviewText.Text = b.Building ? Loc.T("Atlas.Building") : b.Frames.Count == 0 ? Loc.T("Atlas.PreviewEmpty") : "";
        PreviewText.Visibility = PreviewText.Text.Length > 0 && r == null ? Visibility.Visible : Visibility.Collapsed;
        Preview.InvalidateVisual();
        _loading = false;
    }

    // Sides: "auto" (the nearest power of two of the frame grid, 0) and the powers of two up to the limit.
    private static void FillSides(ComboBox box, int value, int limit, bool auto)
    {
        box.Items.Clear();
        if (auto)
            box.Items.Add(new ComboBoxItem { Content = Loc.T("Atlas.Auto"), Tag = 0, ToolTip = box.ToolTip });
        foreach (int s in AtlasBuilder.Sides.Where(s => s <= limit))
            box.Items.Add(new ComboBoxItem { Content = s.ToString(CultureInfo.InvariantCulture), Tag = s, ToolTip = box.ToolTip });
        box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == value) ?? box.Items[0];
    }

    public void Tick(double seconds)
    {
        if (!IsVisible)
            return;
        Builder.Tick(seconds);
        if (Builder.Result != null)
            Preview.InvalidateVisual();
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Filter = Loc.F("Filter.Pictures", Pictures) };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            Builder.AddFiles(dialog.FileNames);
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Atlas.AddFolder") };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        var files = Directory.EnumerateFiles(dialog.FolderName)
            .Where(f => Pictures.Split(';').Any(p => f.EndsWith(p[1..], StringComparison.OrdinalIgnoreCase))).ToList();
        if (files.Count == 0)
            Host?.Toast(Loc.F("Toast.FolderNoPictures", dialog.FolderName), "warn");
        else
            Builder.AddFiles(files);
    }

    private void OnFromSheet(object sender, RoutedEventArgs e)
    {
        var s = Host?.Slicer;
        if (s?.Levels is not { Length: > 0 } levels || s.TexturePath == null)
            return;
        Builder.AddSheet(Path.GetFileNameWithoutExtension(s.TexturePath), levels[0], s.TileU, s.TileV);
        if (s.Params is { AnimFps: > 0 } p)
            Builder.Fps = p.AnimFps;
    }

    private void OnFramePicked(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && FrameList.SelectedIndex >= 0)
            Builder.Selected = FrameList.SelectedIndex;
    }

    private void OnRemove(object sender, RoutedEventArgs e) => Builder.Remove(FrameList.SelectedIndex >= 0 ? FrameList.SelectedIndex : Builder.Frames.Count - 1);

    private void OnClear(object sender, RoutedEventArgs e) => Builder.Clear();

    private void OnFormat(object sender, RoutedEventArgs e)
    {
        Builder.Format = Enum.Parse<EddsFormat>((string)((FrameworkElement)sender).Tag);
        Refresh();
    }

    private void OnSize(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ((ComboBox)sender).SelectedItem is not ComboBoxItem { Tag: int side })
            return;
        if (sender == WidthBox)
            Builder.Width = side;
        else
            Builder.Height = side;
    }

    private void OnMax(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && MaxBox.SelectedItem is ComboBoxItem { Tag: int side })
            Builder.MaxSide = side;
    }

    private void OnStep(object sender, RoutedEventArgs e)
    {
        if (sender == BleedButton)
            Builder.Bleed = BleedButton.IsChecked == true;
        else if (sender == PackButton)
            Builder.PackIntensity = PackButton.IsChecked == true;
        else
            Builder.MatchMean = MatchButton.IsChecked == true;
    }

    private void OnBoxKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            CommitBox((TextBox)sender);
    }

    private void OnBoxLost(object sender, KeyboardFocusChangedEventArgs e) => CommitBox((TextBox)sender);

    private void CommitBox(TextBox box)
    {
        double now = box == GammaBox ? Builder.Gamma : Builder.Fps;
        if (Expr.TryApply(box.Text, now, out double v) && double.IsFinite(v))
        {
            if (box == GammaBox)
                Builder.Gamma = v;
            else
                Builder.Fps = v;
        }
        box.Text = Loc.Num(box == GammaBox ? Builder.Gamma : Builder.Fps);
    }

    private void OnUpscale(object sender, RoutedEventArgs e)
    {
        if (Builder.Upscaling)
        {
            Builder.CancelUpscale();
            return;
        }
        string? why = Loc.T("Why.NoUpscaler");
        if (Host?.UpscalerTool(out why) is { } up)
            Builder.StartUpscale(up);
        else
            Host?.Toast(Loc.F("Toast.NoUpscaler", why), "warn");
    }

    private void OnDropUpscale(object sender, RoutedEventArgs e) => Builder.DropUpscale();

    private void OnView(object sender, RoutedEventArgs e) => Builder.View = Enum.Parse<SheetView>((string)((FrameworkElement)sender).Tag);

    private void OnMip(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        Preview.Mip = Math.Max(0, MipBox.SelectedIndex);
        Preview.InvalidateVisual();
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (Builder.Result == null || Host == null)
            return;
        var slicer = Host.Slicer;
        string? folder = slicer.TexturePath != null ? Path.GetDirectoryName(slicer.TexturePath) : null;
        var dialog = new SaveFileDialog
        {
            Filter = Loc.T("Filter.Edds"),
            FileName = Builder.Frames.Count > 0 ? NameOf(Builder.Frames[0].Name) + ".edds" : "atlas.edds",
            InitialDirectory = folder,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            ExportTo(dialog.FileName);
    }

    // Also what the shell checks call (no dialog).
    public void ExportTo(string path)
    {
        var texconv = Host?.TexconvTool(out _);
        Builder.Export(path, texconv, written => Host?.TextureWritten(written));
    }

    private static string NameOf(string frame)
    {
        string name = Path.GetFileNameWithoutExtension(frame.Split(" #")[0]);
        return name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '_', '-', ' ') is { Length: > 0 } t ? t + "_atlas" : "atlas";
    }

    private void OnApply(object sender, RoutedEventArgs e) => ApplyToEmitter();

    public bool ApplyToEmitter()
    {
        var s = Host?.Slicer;
        if (s?.Doc == null || s.Emitter == null || s.Material == null || Builder.LastExport == null || Host?.GamePathOf(Builder.LastExport) is not { } game)
            return false;
        return Builder.Apply(s.Doc, s.Emitter, s.Material, game);
    }
}
