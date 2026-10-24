using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Pfx.App.Model;
using Pfx.App.Textures;

namespace Pfx.App.Views;

public interface ITextureHost
{
    SlicerModel Slicer { get; }
    IReadOnlyList<EffectDoc> Docs { get; }
    string? Resolve(string gamePath);
    void OpenInBuilder();
}

// The texture properties panel: TextureFacts rows for the slicer's texture. The effect's total video memory reads
// every texture file of the effect, so it runs on a worker; rows are rebuilt once per dispatcher turn while shown.
public partial class TextureView : UserControl
{
    private bool _queued;
    private int _effectJob;
    private (EffectDoc Doc, long Bytes, int Count)? _effect;

    public TextureView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => Invalidate();
    }

    public ITextureHost? Host { get; set; }
    public IReadOnlyList<InfoRow> Rows { get; private set; } = Array.Empty<InfoRow>();

    public void Invalidate()
    {
        if (_queued || !IsVisible)
            return;
        _queued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, Rebuild);
    }

    public void Rebuild()
    {
        _queued = false;
        if (Host == null)
            return;
        var s = Host.Slicer;
        EmptyText.Text = s.Emitter == null ? Loc.T("Slicer.EmptyEmitter") : s.Material == null ? Loc.T("Slicer.EmptyMaterial")
            : s.TextureGamePath == null ? Loc.T("Slicer.EmptyTexture") : s.LoadError ?? "";
        EmptyText.Visibility = EmptyText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Rows = s.Emitter == null ? Array.Empty<InfoRow>()
            : TextureFacts.Rows(s, Host.Docs, Host.Resolve, _effect is { } e && e.Doc == s.Doc ? (e.Bytes, e.Count) : null);
        RowList.ItemsSource = Rows;
        ToBuilderButton.IsEnabled = s.Levels != null;
        ReloadButton.IsEnabled = s.TexturePath != null;
        StartEffectTotal(s.Doc);
    }

    private void StartEffectTotal(EffectDoc? doc)
    {
        if (doc == null || Host == null)
            return;
        var paths = TextureFacts.TexturesOf(doc, Host.Resolve);
        int job = ++_effectJob;
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        Task.Run(() => paths.Select(TextureFacts.SummaryOf).OfType<Pfx.Textures.TextureSummary>().ToList()).ContinueWith(t =>
        {
            if (job != _effectJob || t.Exception != null)
                return;
            var total = (doc, t.Result.Sum(x => x.GpuBytes), t.Result.Count);
            if (_effect != total)
            {
                _effect = total;
                Invalidate();
            }
        }, ui);
    }

    private void OnToBuilder(object sender, RoutedEventArgs e) => Host?.OpenInBuilder();

    private void OnReload(object sender, RoutedEventArgs e) => Host?.Slicer.Reload();
}
