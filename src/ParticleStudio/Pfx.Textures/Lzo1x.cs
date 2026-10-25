using System.IO;

namespace Pfx.Textures;

// LZO1X decompression (the format of compressed PAA mip levels): a port of the decompressor of lzokay (MIT, Copyright (c)
// 2018 Jack Andersen, see THIRD_PARTY.md), which follows the stream format described in the Linux sources
// (Documentation/lzo.txt). The output size is known in advance and every read and copy is bounds-checked.
public static class Lzo1x
{
    public static byte[] Decompress(ReadOnlySpan<byte> input, int outputLength, out int consumed)
    {
        var output = new byte[outputLength];
        int ip = 0, op = 0;

        // Literals copied by the last instruction: 0, 1-3, or 4 for "4 or more"; it decides what an instruction below 16
        // means.
        int state = 0;
        int first = In(input, ip);
        if (first >= 18)
        {
            ip++;
            Literals(input, ref ip, output, ref op, first - 17);
            state = Math.Min(first - 17, 4);
        }

        while (true)
        {
            int inst = In(input, ip++);
            int length, distance, next;
            if (inst >= 64)
            {
                // M2: 3-8 bytes from up to 2 kB back.
                length = (inst >> 5) + 1;
                distance = (In(input, ip++) << 3) + ((inst >> 2) & 7) + 1;
                next = inst & 3;
            }
            else if (inst >= 32)
            {
                // M3: up to 16 kB back.
                length = (inst & 31) + 2;
                if (length == 2)
                    length += Extend(input, ref ip, 31);
                int d = Le16(input, ref ip);
                distance = (d >> 2) + 1;
                next = d & 3;
            }
            else if (inst >= 16)
            {
                // M4: 16-48 kB back; distance 16384 ends the stream.
                length = (inst & 7) + 2;
                if (length == 2)
                    length += Extend(input, ref ip, 7);
                int d = Le16(input, ref ip);
                distance = ((inst & 8) << 11) + (d >> 2);
                next = d & 3;
                if (distance == 0)
                {
                    if (op != outputLength)
                        throw new InvalidDataException($"LZO stream ended after {op} of {outputLength} bytes");
                    consumed = ip;
                    return output;
                }
                distance += 16384;
            }
            else if (state == 0)
            {
                // A run of 4 or more literals.
                int run = inst + 3;
                if (run == 3)
                    run += Extend(input, ref ip, 15);
                Literals(input, ref ip, output, ref op, run);
                state = 4;
                continue;
            }
            else if (state != 4)
            {
                // M1 after 1-3 literals: 2 bytes from up to 1 kB back.
                length = 2;
                distance = (inst >> 2) + (In(input, ip++) << 2) + 1;
                next = inst & 3;
            }
            else
            {
                // M1 after a literal run: 3 bytes from 2-3 kB back.
                length = 3;
                distance = (inst >> 2) + (In(input, ip++) << 2) + 2049;
                next = inst & 3;
            }
            Copy(output, ref op, op - distance, length);
            Literals(input, ref ip, output, ref op, next);
            state = next;
        }
    }

    private static int In(ReadOnlySpan<byte> input, int at)
    {
        if (at >= input.Length)
            throw new InvalidDataException("LZO stream cut off");
        return input[at];
    }

    private static int Le16(ReadOnlySpan<byte> input, ref int ip)
    {
        int value = In(input, ip) | In(input, ip + 1) << 8;
        ip += 2;
        return value;
    }

    // A long length: a zero byte per 255, then the bias and the first non-zero byte.
    private static int Extend(ReadOnlySpan<byte> input, ref int ip, int bias)
    {
        int zeros = 0;
        while (In(input, ip) == 0)
        {
            zeros++;
            ip++;
        }
        long length = zeros * 255L + bias + input[ip++];
        if (length > int.MaxValue / 2)
            throw new InvalidDataException("LZO length out of range");
        return (int)length;
    }

    private static void Literals(ReadOnlySpan<byte> input, ref int ip, byte[] output, ref int op, int count)
    {
        if (ip + count > input.Length || op + count > output.Length)
            throw new InvalidDataException("LZO literal run out of bounds");
        input.Slice(ip, count).CopyTo(output.AsSpan(op));
        ip += count;
        op += count;
    }

    // Byte by byte: a match may overlap the bytes it is producing.
    private static void Copy(byte[] output, ref int op, int from, int count)
    {
        if (from < 0 || op + count > output.Length)
            throw new InvalidDataException("LZO match out of bounds");
        for (int i = 0; i < count; i++)
            output[op++] = output[from + i];
    }
}
