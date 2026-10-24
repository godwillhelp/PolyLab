using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pfx.Textures;

// Reading and writing pictures: PNG through WIC (colour profiles ignored, pixels as stored), TGA by hand (truecolour
// and greyscale, raw or RLE), DDS / EDDS / PAA through their readers (top level).
public static class ImageFiles
{
    public static RgbaImage Load(string path)
    {
        var data = File.ReadAllBytes(path);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => ReadPng(data),
            ".tga" => ReadTga(data),
            ".paa" => PaaFile.Read(data).Decode(),
            ".dds" or ".edds" => EddsFile.IsEdds(data) ? EddsFile.Read(data).Decode() : DdsFile.Read(data).Decode(),
            var ext => throw new NotSupportedException($"Image type '{ext}'"),
        };
    }

    public static void Save(string path, RgbaImage image)
    {
        byte[] data = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => WritePng(image),
            ".tga" => WriteTga(image),
            var ext => throw new NotSupportedException($"Image type '{ext}' for writing"),
        };
        File.WriteAllBytes(path, data);
    }

    public static byte[] ToBgra(RgbaImage image)
    {
        var p = (byte[])image.Pixels.Clone();
        for (int i = 0; i < p.Length; i += 4)
            (p[i], p[i + 2]) = (p[i + 2], p[i]);
        return p;
    }

    private static RgbaImage FromBgra(int width, int height, byte[] bgra)
    {
        for (int i = 0; i < bgra.Length; i += 4)
            (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        return new RgbaImage(width, height, bgra);
    }

    public static RgbaImage ReadPng(byte[] data)
    {
        using var stream = new MemoryStream(data);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if (frame.Format != PixelFormats.Bgra32)
            frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = frame.PixelWidth, h = frame.PixelHeight;
        var bgra = new byte[w * h * 4];
        frame.CopyPixels(bgra, w * 4, 0);
        return FromBgra(w, h, bgra);
    }

    public static byte[] WritePng(RgbaImage image)
    {
        var source = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, ToBgra(image), image.Width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // TGA header: id length, colour map type, image type (2 truecolour, 3 grey, +8 = RLE), colour map spec (5 bytes),
    // origin x/y, width, height, bits per pixel, descriptor (bit 5 = rows top to bottom, bit 4 = right to left).
    public static RgbaImage ReadTga(byte[] data)
    {
        if (data.Length < 18)
            throw new InvalidDataException("TGA shorter than its header");
        int idLength = data[0], mapType = data[1], type = data[2];
        int width = data[12] | data[13] << 8, height = data[14] | data[15] << 8, bits = data[16], descriptor = data[17];
        if (mapType != 0 || type is not (2 or 3 or 10 or 11))
            throw new NotSupportedException($"TGA image type {type} with colour map type {mapType}");
        bool grey = type is 3 or 11, rle = type >= 9;
        if (grey ? bits != 8 : bits is not (24 or 32))
            throw new NotSupportedException($"TGA {(grey ? "grey" : "truecolour")} with {bits} bits");
        if ((descriptor & 0x10) != 0)
            throw new NotSupportedException("TGA stored right to left");
        int bpp = bits / 8, count = width * height;
        var raw = new byte[count * bpp];
        int at = 18 + idLength;
        if (!rle)
        {
            if (at + raw.Length > data.Length)
                throw new InvalidDataException("TGA pixels cut off");
            Array.Copy(data, at, raw, 0, raw.Length);
        }
        else
        {
            int o = 0;
            while (o < raw.Length)
            {
                if (at >= data.Length)
                    throw new InvalidDataException("TGA RLE data cut off");
                int packet = data[at++], n = (packet & 0x7F) + 1;
                if (o + n * bpp > raw.Length)
                    throw new InvalidDataException("TGA RLE packet runs past the image");
                int take = (packet & 0x80) != 0 ? bpp : n * bpp;
                if (at + take > data.Length)
                    throw new InvalidDataException("TGA RLE data cut off");
                for (int i = 0; i < n; i++)
                    Array.Copy(data, (packet & 0x80) != 0 ? at : at + i * bpp, raw, o + i * bpp, bpp);
                at += take;
                o += n * bpp;
            }
        }
        var image = new RgbaImage(width, height);
        bool topDown = (descriptor & 0x20) != 0;
        for (int y = 0; y < height; y++)
        {
            int srcRow = (topDown ? y : height - 1 - y) * width;
            for (int x = 0; x < width; x++)
            {
                int s = (srcRow + x) * bpp, d = (y * width + x) * 4;
                var p = image.Pixels;
                if (grey)
                    p[d] = p[d + 1] = p[d + 2] = raw[s];
                else
                {
                    p[d] = raw[s + 2];
                    p[d + 1] = raw[s + 1];
                    p[d + 2] = raw[s];
                }
                p[d + 3] = bpp == 4 ? raw[s + 3] : (byte)255;
            }
        }
        return image;
    }

    public static byte[] WriteTga(RgbaImage image)
    {
        if (image.Width > ushort.MaxValue || image.Height > ushort.MaxValue)
            throw new ArgumentException($"TGA sides stop at {ushort.MaxValue}, image is {image.Width}x{image.Height}");
        var output = new byte[18 + image.Pixels.Length];
        output[2] = 2;
        output[12] = (byte)image.Width;
        output[13] = (byte)(image.Width >> 8);
        output[14] = (byte)image.Height;
        output[15] = (byte)(image.Height >> 8);
        output[16] = 32;
        output[17] = 0x28;
        ToBgra(image).CopyTo(output, 18);
        return output;
    }
}
