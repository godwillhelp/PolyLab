using System.IO.Compression;
using System.Net;
using System.Text;

namespace PolyLab.CgUi;

public sealed record Shot(int ScreenH, Raster Raster, byte[] Png);

public sealed record GalleryItem(Screen Ir, Encoded Data, Shot[] Shots, bool Sheet);

public static class Gallery
{
    public static readonly int[] Heights = [1080, 720];
    public const int LineBudget = 10_000;

    public static Shot Shoot(Screen ir, Screen decoded, int screenH)
    {
        var raster = new Raster(decoded, screenH);
        int w = raster.DeviceW, h = raster.DeviceH;
        var lin = new Rgb[w * h];
        var lines = new List<Line>();
        if (ir.Backdrop is { } bd)
        {
            var back = new Screen { W = ir.W, H = ir.H, Root = new Node { Flags = Flags.Visible, W = ir.W, H = ir.H } };
            back.Root.Draws.Add(new FillBox(bd));
            lines.AddRange(new Raster(back, screenH).Lines);
        }
        lines.AddRange(raster.Lines.OrderBy(l => l.Layer));
        foreach (var l in lines)
        {
            var (a, r, g, b) = Colour.Split(l.Argb);
            var src = Rgb.Of8(r, g, b);
            for (int y = l.Box.Y0; y < l.Box.Y1; y++)
                for (int x = l.Box.X0; x < l.Box.X1; x++)
                    lin[y * w + x] = Colour.LinBlend(lin[y * w + x], src, a / 255.0);
        }
        var rgb = new byte[w * h * 3];
        for (int i = 0; i < lin.Length; i++)
        {
            rgb[3 * i] = (byte)Colour.To8(Colour.Srgb(lin[i].R));
            rgb[3 * i + 1] = (byte)Colour.To8(Colour.Srgb(lin[i].G));
            rgb[3 * i + 2] = (byte)Colour.To8(Colour.Srgb(lin[i].B));
        }
        return new Shot(screenH, raster, Png(w, h, rgb));
    }

    public static byte[] Png(int w, int h, byte[] rgb)
    {
        var raw = new byte[h * (w * 3 + 1)];
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(rgb, y * w * 3, raw, y * (w * 3 + 1) + 1, w * 3);
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, true))
            zs.Write(raw);
        using var ms = new MemoryStream();
        ms.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var ihdr = new byte[13];
        BE(ihdr, 0, w);
        BE(ihdr, 4, h);
        ihdr[8] = 8;
        ihdr[9] = 2;
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "IDAT", z.ToArray());
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void BE(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 24);
        b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8);
        b[at + 3] = (byte)v;
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var head = new byte[8];
        BE(head, 0, data.Length);
        Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
        s.Write(head);
        s.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (byte v in head.AsSpan(4).ToArray().Concat(data))
            crc = CrcTable[(crc ^ v) & 0xFF] ^ (crc >> 8);
        var tail = new byte[4];
        BE(tail, 0, (int)~crc);
        s.Write(tail);
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);

    private static string Hex(int argb)
    {
        var (a, r, g, b) = Colour.Split(argb);
        return $"rgba({r},{g},{b},{a / 255.0:0.###})";
    }

    public static string Html(IReadOnlyList<GalleryItem> items, string source)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html><html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1"><title>CG_UI gallery</title>
            <style>
            body{margin:0;padding:16px;background:#111;color:#ddd;font:13px/1.4 Segoe UI,Arial,sans-serif}
            h1{font-size:20px}h2{font-size:17px;margin-top:40px;border-top:1px solid #333;padding-top:16px}
            table{border-collapse:collapse;margin:8px 0}td,th{border:1px solid #333;padding:2px 8px;text-align:left}
            td.n{text-align:right;font-variant-numeric:tabular-nums}
            .shot{position:relative;display:inline-block;margin:8px 16px 8px 0;vertical-align:top}
            .shot img{display:block;image-rendering:pixelated}.shot svg{position:absolute;left:0;top:0;width:100%;height:100%}
            .off svg{display:none}.err{color:#f66}.warn{color:#fc6}
            </style></head><body>
            <h1>CG_UI gallery</h1>
            """);
        sb.Append($"<p>Source: {E(source)}. Raster = the int stream decoded and drawn with the rules of CG_UI_PIPELINE.md §6.1–6.2 " +
                  "(canvas lines blended in linear light). Boxes: blue = text, green = image, magenta = live widget; " +
                  "text uses a browser font, not the game font.</p>");
        sb.Append("<label><input type=checkbox checked onchange=\"document.body.classList.toggle('off',!this.checked)\"> widget boxes</label>");
        foreach (var it in items)
            Section(sb, it);
        sb.Append("</body></html>\n");
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, GalleryItem it)
    {
        var ir = it.Ir;
        var nodes = ir.Root.PreOrder().ToList();
        sb.Append($"<h2>{E(ir.Name)} <small>({E(ir.FigmaId)}{(it.Sheet ? ", vector sheet: img: rule off" : "")})</small></h2>");
        sb.Append("<table><tr><th>device</th><th>canvas lines</th><th>per layer</th><th>widgets</th></tr>");
        foreach (var s in it.Shots)
        {
            var perLayer = s.Raster.Lines.GroupBy(l => l.Layer).OrderBy(g => g.Key).Select(g => $"L{g.Key}: {g.Count()}");
            sb.Append($"<tr><td>{s.Raster.DeviceW}×{s.Raster.DeviceH}</td><td class=n>{s.Raster.Lines.Count}</td>" +
                      $"<td>{string.Join(", ", perLayer)}</td><td class=n>{s.Raster.Widgets.Count}</td></tr>");
        }
        sb.Append($"</table><p>Stream: {it.Data.Ints.Length} ints, {it.Data.Strs.Length} strings, {nodes.Count} nodes, " +
                  $"{ir.Components.Count} component variants. Errors {ir.Diags.Count(d => d.Error)}, warnings {ir.Diags.Count(d => !d.Error)}.</p>");
        int zoom = it.Sheet ? 4 : 1;
        foreach (var s in it.Shots)
        {
            var r = s.Raster;
            double u = s.ScreenH / 1080.0;
            sb.Append($"<div class=shot><img width={r.DeviceW * zoom} height={r.DeviceH * zoom} src=\"data:image/png;base64,{Convert.ToBase64String(s.Png)}\">");
            sb.Append($"<svg viewBox=\"0 0 {r.DeviceW} {r.DeviceH}\" preserveAspectRatio=\"none\">");
            foreach (var w in r.Widgets)
            {
                var b = w.Box;
                var n = w.Node;
                string col = n.Text != null ? "#4af" : n.Image != null ? "#4d4" : "#f4f";
                sb.Append($"<rect x={b.X0} y={b.Y0} width={b.X1 - b.X0} height={b.Y1 - b.Y0} fill=none stroke=\"{col}\" stroke-width=0.6 stroke-dasharray=\"2 1\"/>");
                if (n.Text is { } t)
                {
                    double size = t.Size / 16.0 * u;
                    string anchor = t.AlignH switch { 1 => "middle", 2 => "end", _ => "start" };
                    double tx = t.AlignH switch { 1 => (b.X0 + b.X1) / 2.0, 2 => b.X1, _ => b.X0 };
                    double ty = t.AlignV switch { 1 => (b.Y0 + b.Y1) / 2.0 + size * 0.35, 2 => b.Y1 - size * 0.25, _ => b.Y0 + size };
                    sb.Append($"<text x={tx:0.#} y={ty:0.#} font-size={size:0.##} text-anchor={anchor} fill=\"{Hex(t.Argb)}\" font-family=\"Segoe UI,Arial\">{E(t.Chars)}</text>");
                }
                else
                {
                    string label = n.Image?.Ref ?? n.Binding ?? "live";
                    sb.Append($"<text x={b.X0 + 1} y={b.Y0 + 6} font-size=5 fill=\"{col}\">{E(label)}</text>");
                }
            }
            sb.Append($"</svg><div>{r.DeviceW}×{r.DeviceH}{(zoom > 1 ? $", shown ×{zoom}" : "")}</div></div>");
        }

        var counts = it.Shots.Select(s => s.Raster.Lines.GroupBy(l => l.Node).ToDictionary(g => g.Key, g => g.Count())).ToArray();
        var rows = nodes.Select((n, i) => (n, i, c: counts.Select(c => c.GetValueOrDefault(i)).ToArray()))
            .Where(x => x.c.Any(v => v > 0) || x.n.IsWidget).OrderByDescending(x => x.c[0]).ToList();
        sb.Append("<details><summary>Lines per node</summary><table><tr><th>#</th><th>Figma id</th><th>name</th><th>layer</th>");
        foreach (var s in it.Shots)
            sb.Append($"<th>{s.ScreenH}p</th>");
        sb.Append("<th>kind</th></tr>");
        foreach (var (n, i, c) in rows)
        {
            string kind = n.Text != null ? "text" : n.Image != null ? "image" : (n.Flags & Flags.Live) != 0 ? "live" : n.Pattern != null ? "pattern" : "shape";
            sb.Append($"<tr><td class=n>{i}</td><td>{E(n.Id)}</td><td>{E(n.Name)}</td><td class=n>{n.Layer}</td>");
            foreach (int v in c)
                sb.Append($"<td class=n>{v}</td>");
            sb.Append($"<td>{kind}{(n.Visible ? "" : ", hidden")}</td></tr>");
        }
        sb.Append("</table></details>");

        sb.Append("<details open><summary>Errors and warnings</summary><table><tr><th></th><th>node</th><th>name</th><th>message</th></tr>");
        foreach (var d in ir.Diags.OrderBy(d => !d.Error))
            sb.Append($"<tr><td class={(d.Error ? "err>error" : "warn>warning")}</td><td>{E(d.NodeId)}</td><td>{E(d.NodeName)}</td><td>{E(d.Message)}</td></tr>");
        sb.Append("</table></details>");
    }
}
