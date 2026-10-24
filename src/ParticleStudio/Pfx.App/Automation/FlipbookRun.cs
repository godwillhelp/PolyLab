using System.Globalization;
using System.Text;
using System.Windows.Threading;
using Pfx.App.Core;
using Pfx.App.Views;
using Pfx.Textures;

namespace Pfx.App.Automation;

// --flipbook <dir>: the P7 acceptance flow inside the real window. The opened effect's first emitter has a material;
// <dir>\frames holds a picture sequence. The atlas builder makes a DXT5 flipbook of it (AnimFPS 1), writes it next to
// the material as p7_num.edds, applies it (material AlbedoMap / TileU / TileV, emitter AnimFPS) and saves the effect;
// then the viewport is paused and scrubbed to 0.5, 1.5, … s (exact scrubbing, P6), each frame captured with the tile
// the simulator draws there. The same files go to the game for the in-game half.
public sealed class FlipbookRun
{
    private const int Frames = 16;
    private readonly MainWindow _w;
    private readonly string _dir;
    private readonly StringBuilder _log = new();
    private TaskCompletionSource? _frame;

    public FlipbookRun(MainWindow window, Launch launch)
    {
        _w = window;
        _dir = Path.GetFullPath(launch.Flipbook!);
    }

    public void Start()
    {
        _w.Viewport.Rendered += () => _frame?.TrySetResult();
        _ = Run();
    }

    private Task Frame()
    {
        _frame = new TaskCompletionSource();
        return _frame.Task;
    }

    private async Task Until(Func<bool> done, int frames = 3000)
    {
        for (int i = 0; i < frames && !done(); i++)
            await Frame();
        if (!done())
            throw new TimeoutException("waited " + frames + " frames");
    }

    private void Log(string line)
    {
        _log.AppendLine(line);
        File.WriteAllText(Path.Combine(_dir, "flipbook.txt"), _log.ToString());
    }

    private async Task Run()
    {
        try
        {
            var doc = _w.Docs[0];
            var emitter = doc.Ptc.Emitters[0];
            _w.Select(doc, new[] { emitter.Block }, false);
            _w.ShowPane("atlas");
            await _w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var builder = _w.AtlasView.Builder;
            var files = Directory.GetFiles(Path.Combine(_dir, "frames"), "*.png");
            builder.AddFiles(files);
            builder.Format = EddsFormat.Dxt5;
            builder.Fps = 1;
            await Until(() => builder.Frames.Count == files.Length && !builder.Building && builder.Result?.Format == EddsFormat.Dxt5);
            var r = builder.Result!;
            Log($"atlas: {files.Length} pictures ({string.Join(", ", builder.Frames.Select(f => f.Name))}) → grid {r.TileU}x{r.TileV}, " +
                $"{r.Levels[0].Width}x{r.Levels[0].Height} DXT5, {r.Levels.Length} mips, video memory {r.GpuBytes} bytes");
            string target = Path.Combine(Path.GetDirectoryName(_w.Slicer.Material!.DiskPath)!, "p7_num.edds");
            builder.Export(target, _w.TexconvTool(out _), path => _w.TextureWritten(path));
            await Until(() => builder.LastExport == target);
            var written = TextureSummary.Read(target);
            Log($"written {target}: {written.Kind} {written.Format} {written.Width}x{written.Height}, ENF1 {written.Enf1}, {written.Mips} mips " +
                $"(LZ4 {written.Lz4Levels}, COPY {written.CopyLevels}), {written.DiskBytes} bytes on disk, video memory {written.GpuBytes}");
            bool applied = _w.AtlasView.ApplyToEmitter();
            _w.Save(doc);
            var m = _w.Slicer.Material!;
            Log($"applied {applied}: material {Path.GetFileName(m.DiskPath)} AlbedoMap {m.Emat.Text("AlbedoMap")}, TileU {m.Emat.Number("TileU")}, " +
                $"TileV {m.Emat.Number("TileV")}; emitter {emitter.Name} AnimFPS {emitter.Number("AnimFPS")}; saved, dirty {doc.Dirty}");
            _w.Pipeline.Restart();
            await Until(() => !_w.Pipeline.AnyBusy && _w.Pipeline.PreviewOf(doc) is { } p && p.Params[0].TileU == r.TileU);
            await Until(() => _w.Slicer.Levels != null && _w.Slicer.TileU == r.TileU);
            _w.Pipeline.Playing = false;
            _w.ViewFrom(Pfx.Render.ViewPreset.Front);
            var order = new List<int>();
            for (int k = 0; k <= Frames + 1; k++)
            {
                float t = k + 0.5f;
                _w.Pipeline.Seek(t);
                await Until(() => !_w.Pipeline.AnySeeking);
                await Frame();
                await Frame();
                var preview = _w.Pipeline.PreviewOf(doc)!;
                var counts = new int[preview.Params[0].Frames];
                preview.Sims[0].Emitters[0].CountFrames(counts);
                int tile = Array.FindIndex(counts, c => c > 0);
                order.Add(tile);
                var image = _w.Viewport.CaptureFrame(false);
                if (image != null)
                    ImageFiles.Save(Path.Combine(_dir, $"view_{t.ToString("00.0", CultureInfo.InvariantCulture)}.png"), MainWindow.Opaque(image));
                Log($"t {t:0.0} s: preview time {preview.ShownTime:0.000} s, tile {tile} ({string.Join(" ", counts)})");
            }
            Log("viewport tile order at 0.5 … " + (Frames + 1.5) + " s: " + string.Join(" ", order));
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex);
        }
        _w.Close();
    }
}
