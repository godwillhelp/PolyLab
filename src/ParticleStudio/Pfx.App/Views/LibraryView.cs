using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pfx.App.Core;
using Pfx.App.Library;
using Pfx.Render;

namespace Pfx.App.Views;

public interface ILibraryHost
{
    AppSettings Settings { get; }
    IReadOnlyList<LibrarySource> LibrarySources();
    IReadOnlyList<string> LibraryRoots();
    Pfx.Textures.RgbaImage? RenderThumb(EffectPreview preview, Camera camera, int width, int height);
    void OpenLibraryEffect(string path);
    void AddLibraryEmitter(string path, string emitter);
    void AddLibraryFolder();
    void Toast(string text, string kind);
}

// The library browser (spec 3.10): every effect under the library's folders, searchable by name, folder and source,
// with stars, thumbnails the viewport draws (one at a time on a worker, newest list first, cached on disk by file
// size and time) and the emitters of an effect to drag onto the layers panel (or add with a button).
public sealed class LibraryView : UserControl
{
    // Thumbnails 4:3 at 80 px: a row stays below the height of two text lines plus padding.
    public const int ThumbWidth = 80, ThumbHeight = 60;
    // Rows built per page: a list of the whole vanilla folder (600+ effects) at once would cost seconds of layout.
    private const int Page = 60;

    private ILibraryHost? _host;
    private List<LibraryEntry> _all = new();
    private string _source = "";
    private bool _starred;
    private int _shown = Page;
    private string? _open;
    private CancellationTokenSource? _scan;
    private int _thumbGeneration;
    private string _scanned = "";
    private readonly Dictionary<string, ImageSource> _thumbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Image> _thumbViews = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBox _search = new();
    private readonly WrapPanel _chips = new() { Margin = new Thickness(8, 2, 8, 4) };
    private readonly StackPanel _list = new();
    private readonly TextBlock _count = new();

    public LibraryView()
    {
        Background = Brush("B.BgPanel");
        // Sources follow the data roots, which grow as parts are opened (a root is found above an opened file): the list
        // is read again when the panel comes into view and the sources are not the ones it was read from.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && _host != null && Signature(_host.LibrarySources()) != _scanned)
                Rescan();
        };
        var top = new DockPanel { Margin = new Thickness(8, 6, 8, 2) };
        var add = new Button { Style = Find("Btn.Tiny"), Width = 22, Height = 22, Margin = new Thickness(4, 0, 0, 0) };
        add.Content = new System.Windows.Shapes.Path { Data = (Geometry)Application.Current.FindResource("Ic.Folder"), Style = Find("Ico.Small") };
        add.Click += (_, _) => _host?.AddLibraryFolder();
        add.Tag = "add";
        DockPanel.SetDock(add, Dock.Right);
        top.Children.Add(add);
        _search.TextChanged += (_, _) => { _shown = Page; Fill(); };
        top.Children.Add(_search);
        var head = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        head.Children.Add(top);
        DockPanel.SetDock(_chips, Dock.Top);
        head.Children.Add(_chips);
        _count.Style = Find("T.Dim");
        _count.FontSize = 10.5;
        _count.Margin = new Thickness(10, 0, 10, 2);
        DockPanel.SetDock(_count, Dock.Top);
        head.Children.Add(_count);
        head.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = head;
    }

    public void Init(ILibraryHost host)
    {
        _host = host;
        Rescan();
    }

    public IReadOnlyList<LibraryEntry> Entries => _all;
    public int ThumbsDrawn => _thumbs.Count;
    public bool Scanning => _scan != null;

    // Sources changed (a folder added, the roots changed): read the file names again on a worker.
    public void Rescan()
    {
        if (_host == null)
            return;
        _scan?.Cancel();
        var cancel = new CancellationTokenSource();
        _scan = cancel;
        var sources = _host.LibrarySources().ToList();
        _scanned = Signature(sources);
        Task.Run(() => LibraryIndex.Scan(sources, cancel.Token), cancel.Token).ContinueWith(t =>
        {
            if (cancel.IsCancellationRequested || t.Status != TaskStatus.RanToCompletion)
                return;
            _all = t.Result.OrderBy(e => e.Source).ThenBy(e => e.Folder).ThenBy(e => e.Name).ToList();
            // A source picked as the filter can be gone or renamed (another language): back to all.
            if (_all.All(e => e.Source != _source))
                _source = "";
            _scan = null;
            Refresh();
        }, TaskScheduler.FromCurrentSynchronizationContext());
        Refresh();
    }

    private static string Signature(IEnumerable<LibrarySource> sources) => string.Join("|", sources.Select(s => s.Label + "=" + s.Folder));

    public void Refresh()
    {
        _search.ToolTip = Loc.T("Tip.Lib.Search");
        foreach (var b in ((DockPanel)((DockPanel)Content).Children[0]).Children.OfType<Button>())
            b.ToolTip = Loc.T("Tip.Lib.AddFolder");
        _chips.Children.Clear();
        void Chip(string text, string tip, bool on, Action pick)
        {
            var chip = new ToggleButton { Style = Find("Btn.Seg"), Content = text, ToolTip = tip, IsChecked = on, Margin = new Thickness(0, 0, 4, 2) };
            chip.Click += (_, _) => { pick(); _shown = Page; Refresh(); };
            _chips.Children.Add(chip);
        }
        Chip(Loc.T("Lib.All"), Loc.T("Tip.Lib.All"), _source.Length == 0 && !_starred, () => { _source = ""; _starred = false; });
        Chip("★ " + Loc.T("Lib.Starred"), Loc.T("Tip.Lib.Starred"), _starred, () => { _starred = !_starred; });
        foreach (var src in _all.Select(e => e.Source).Distinct())
        {
            string s = src;
            Chip(s, Loc.F("Tip.Lib.Source", s, _all.Count(e => e.Source == s)), _source == s, () => _source = _source == s ? "" : s);
        }
        Fill();
    }

    private IEnumerable<LibraryEntry> Matches()
    {
        string q = _search.Text.Trim();
        var stars = _host!.Settings.LibraryFavourites.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _all.Where(e => (_source.Length == 0 || e.Source == _source) && (!_starred || stars.Contains(e.Path))
                               && (q.Length == 0 || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Folder.Contains(q, StringComparison.OrdinalIgnoreCase)
                                   || e.Source.Contains(q, StringComparison.OrdinalIgnoreCase)));
    }

    private void Fill()
    {
        if (_host == null)
            return;
        _list.Children.Clear();
        _thumbViews.Clear();
        var matches = Matches().ToList();
        _count.Text = _scan != null ? Loc.T("Lib.Scanning") : Loc.F("Lib.Count", matches.Count, _all.Count);
        if (_all.Count == 0 && _scan == null)
            _list.Children.Add(new TextBlock { Text = Loc.T("Lib.Empty"), Style = Find("T.Dim"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 4, 10, 4) });
        var stars = _host.Settings.LibraryFavourites.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var e in matches.Take(_shown))
            Row(e, stars.Contains(e.Path));
        if (matches.Count > _shown)
        {
            var more = new Button { Content = Loc.F("Lib.More", matches.Count - _shown), ToolTip = Loc.T("Tip.Lib.More"), Margin = new Thickness(10, 4, 10, 6), HorizontalAlignment = HorizontalAlignment.Left };
            more.Click += (_, _) => { _shown += Page; Fill(); };
            _list.Children.Add(more);
        }
        QueueThumbs(matches.Take(_shown).Select(m => m.Path).ToList());
    }

    private void Row(LibraryEntry e, bool starred)
    {
        var row = new DockPanel { Margin = new Thickness(6, 1, 6, 1), Background = Brush(e.Path == _open ? "B.BgSelected" : "B.Transparent"), Cursor = Cursors.Hand };
        var star = new ToggleButton { Style = Find("Btn.RowToggle"), IsChecked = starred, Content = "★", ToolTip = Loc.T("Tip.Lib.Star"), VerticalAlignment = VerticalAlignment.Center };
        star.Foreground = Brush(starred ? "B.Key" : "B.TextOff");
        star.Click += (_, _) =>
        {
            var favs = _host!.Settings.LibraryFavourites;
            if (!favs.Remove(e.Path))
                favs.Add(e.Path);
            SaveSettings();
            Fill();
        };
        DockPanel.SetDock(star, Dock.Right);
        row.Children.Add(star);
        var image = new Image { Width = ThumbWidth, Height = ThumbHeight, Stretch = Stretch.Uniform, Margin = new Thickness(0, 1, 6, 1), ToolTip = Loc.T("Tip.Lib.Thumb") };
        if (_thumbs.TryGetValue(e.Path, out var src))
            image.Source = src;
        _thumbViews[e.Path] = image;
        var frame = new Border { Background = Brush("B.BgInput"), Child = image };
        DockPanel.SetDock(frame, Dock.Left);
        row.Children.Add(frame);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = e.Name, Foreground = Brush("B.TextHi"), TextTrimming = TextTrimming.CharacterEllipsis });
        texts.Children.Add(new TextBlock { Text = e.Source + (e.Folder.Length > 0 ? " · " + e.Folder : ""), Style = Find("T.Dim"), FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis });
        row.Children.Add(texts);
        row.ToolTip = e.Path + "\n" + Loc.T("Tip.Lib.Row");
        row.MouseLeftButtonUp += (_, ev) =>
        {
            if (ev.OriginalSource is DependencyObject d && Up<ButtonBase>(d) != null)
                return;
            _open = _open == e.Path ? null : e.Path;
            Fill();
        };
        row.MouseLeftButtonDown += (_, ev) =>
        {
            if (ev.ClickCount == 2)
                _host!.OpenLibraryEffect(e.Path);
        };
        _list.Children.Add(row);
        if (_open == e.Path)
            Emitters(e);
    }

    // The emitters of the open row: drag one onto the layers panel, or the button adds it to the active part.
    private void Emitters(LibraryEntry e)
    {
        var box = new StackPanel { Margin = new Thickness(14, 0, 6, 6) };
        var open = new Button { Content = Loc.T("Lib.Open"), ToolTip = Loc.T("Tip.Lib.Open"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 4) };
        open.Click += (_, _) => _host!.OpenLibraryEffect(e.Path);
        box.Children.Add(open);
        var list = LibraryIndex.Emitters(e.Path);
        if (list.Count == 0)
            box.Children.Add(new TextBlock { Text = Loc.T("Lib.NoEmitters"), Style = Find("T.Dim"), FontSize = 11 });
        foreach (var (name, text) in list)
        {
            string n = name;
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 1), Background = Brush("B.Transparent"), Cursor = Cursors.SizeAll, ToolTip = Loc.T("Tip.Lib.Emitter") };
            var add = new Button { Style = Find("Btn.Tiny"), Width = 20, Height = 20, ToolTip = Loc.T("Tip.Lib.AddEmitter") };
            add.Content = new System.Windows.Shapes.Path { Data = (Geometry)Application.Current.FindResource("Ic.Plus"), Style = Find("Ico.Small"), Width = 9, Height = 9 };
            add.Click += (_, _) => _host!.AddLibraryEmitter(e.Path, n);
            DockPanel.SetDock(add, Dock.Right);
            line.Children.Add(add);
            line.Children.Add(new TextBlock { Text = "⋮⋮ " + name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            Point down = default;
            line.PreviewMouseLeftButtonDown += (_, ev) => down = ev.GetPosition(line);
            line.PreviewMouseMove += (_, ev) =>
            {
                if (ev.LeftButton != MouseButtonState.Pressed || ev.OriginalSource is DependencyObject d && Up<ButtonBase>(d) != null)
                    return;
                var p = ev.GetPosition(line);
                if (Math.Abs(p.Y - down.Y) < SystemParameters.MinimumVerticalDragDistance && Math.Abs(p.X - down.X) < SystemParameters.MinimumHorizontalDragDistance)
                    return;
                DragDrop.DoDragDrop(line, new DataObject(typeof(LibraryDrag), new LibraryDrag(e.Path, n)), DragDropEffects.Copy);
            };
            box.Children.Add(line);
        }
        _list.Children.Add(box);
    }

    // ---- thumbnails ----

    private static string ThumbFolder => Path.Combine(AppSettings.CacheFolder, "thumbs");

    // The rows on screen get their pictures one after another: from the disk cache, else simulated on a worker and drawn
    // by the viewport on the UI thread (it owns the device). A newer list stops the older queue.
    private void QueueThumbs(List<string> paths)
    {
        int generation = ++_thumbGeneration;
        var todo = new Queue<string>(paths.Where(p => !_thumbs.ContainsKey(p)));
        NextThumb(todo, generation);
    }

    private void NextThumb(Queue<string> todo, int generation)
    {
        if (generation != _thumbGeneration || todo.Count == 0 || _host == null)
            return;
        string path = todo.Dequeue();
        string cache = Path.Combine(ThumbFolder, SafeKey(path) + ".png");
        var roots = _host.LibraryRoots().ToList();
        Task.Run(() =>
        {
            if (File.Exists(cache))
                return (Cached: Pfx.Textures.ImageFiles.ReadPng(File.ReadAllBytes(cache)), Scene: (ThumbScene?)null);
            return (Cached: (Pfx.Textures.RgbaImage?)null, Scene: ThumbScene.Build(path, roots, ThumbWidth / (float)ThumbHeight));
        }).ContinueWith(t =>
        {
            if (generation != _thumbGeneration)
                return;
            if (t.Status == TaskStatus.RanToCompletion)
            {
                var image = t.Result.Cached;
                if (image == null && t.Result.Scene is { } scene)
                {
                    // Drawn at twice the size and shown scaled: the sprites keep their soft edges at 80 px.
                    image = _host.RenderThumb(scene.Preview, scene.Camera, ThumbWidth * 2, ThumbHeight * 2);
                    if (image != null)
                        Save(cache, image);
                }
                if (image != null)
                {
                    var src = ToBitmap(image);
                    _thumbs[path] = src;
                    if (_thumbViews.TryGetValue(path, out var view))
                        view.Source = src;
                }
            }
            NextThumb(todo, generation);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static string SafeKey(string path)
    {
        try
        {
            return LibraryIndex.ThumbKey(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "missing";
        }
    }

    private static void Save(string path, Pfx.Textures.RgbaImage image)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.Write(path, Pfx.Textures.ImageFiles.WritePng(image));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static BitmapSource ToBitmap(Pfx.Textures.RgbaImage image)
    {
        var bgra = new byte[image.Pixels.Length];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = image.Pixels[i + 2];
            bgra[i + 1] = image.Pixels[i + 1];
            bgra[i + 2] = image.Pixels[i];
            bgra[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, bgra, image.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private void SaveSettings()
    {
        try
        {
            _host!.Settings.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static T? Up<T>(DependencyObject? d) where T : class
    {
        while (d != null && d is not T)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private static Style Find(string key) => (Style)Application.Current.FindResource(key);

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
}

// An emitter dragged out of the library: the effect file and the emitter's name.
public sealed record LibraryDrag(string Path, string Emitter);
