using System.Buffers.Binary;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Encoders;

namespace Pfx.Textures;

// EDDS "LZ4 " chunk: u32 raw size, then one entry per 64 KB of raw data: u32 compressed size (high bit set on the
// last entry) and an LZ4 block. Blocks are chained: a block may copy from the 64 KB of output before it.
public static class Lz4Chunk
{
    public const int BlockSize = 65536;
    private const uint LastBlock = 0x80000000;

    public static byte[] Decode(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length < 4)
            throw new InvalidDataException("LZ4 chunk shorter than its size field");
        int total = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(chunk));
        var output = new byte[total];
        int at = 4, done = 0;
        bool last = false;
        while (done < total)
        {
            if (last)
                throw new InvalidDataException("LZ4 block after the block marked last");
            if (at + 4 > chunk.Length)
                throw new InvalidDataException("LZ4 chunk cut off");
            uint entry = BinaryPrimitives.ReadUInt32LittleEndian(chunk[at..]);
            int size = (int)(entry & ~LastBlock);
            last = (entry & LastBlock) != 0;
            at += 4;
            if (at + size > chunk.Length)
                throw new InvalidDataException("LZ4 block runs past the chunk");
            int want = Math.Min(BlockSize, total - done);
            int dict = Math.Min(BlockSize, done);
            int got = LZ4Codec.Decode(chunk.Slice(at, size), output.AsSpan(done, want), output.AsSpan(done - dict, dict));
            if (got != want)
                throw new InvalidDataException($"LZ4 block at {at} decoded to {got} bytes, expected {want}");
            at += size;
            done += want;
        }
        if (!last && total > 0)
            throw new InvalidDataException("LZ4 chunk has no block marked last");
        if (at != chunk.Length)
            throw new InvalidDataException($"LZ4 chunk has {chunk.Length - at} bytes after its last block");
        return output;
    }

    // Returns null unless the chunk is below 80 % of the raw size: vanilla stores everything else as COPY (the largest
    // chunk/raw ratio among the 882 LZ4 chunks of P:\graphics is 3276/4096 = 0.7998, and compressible COPY chunks there
    // stay above 0.8).
    public static byte[]? Encode(ReadOnlySpan<byte> raw, LZ4Level level)
    {
        using var encoder = LZ4Encoder.Create(true, level, BlockSize, 0);
        var output = new byte[(int)(((long)raw.Length * 4 + 4) / 5)];
        if (output.Length < 4)
            return null;
        BinaryPrimitives.WriteUInt32LittleEndian(output, (uint)raw.Length);
        var scratch = new byte[LZ4Codec.MaximumOutputSize(BlockSize)];
        int at = 4;
        for (int start = 0; start < raw.Length; start += BlockSize)
        {
            var block = raw.Slice(start, Math.Min(BlockSize, raw.Length - start));
            encoder.TopupAndEncode(block, scratch, true, false, out int loaded, out int encoded);
            if (loaded != block.Length || encoded <= 0)
                throw new InvalidOperationException($"LZ4 encoder took {loaded} of {block.Length} bytes and wrote {encoded}");
            if (at + 4 + encoded >= output.Length)
                return null;
            uint entry = (uint)encoded | (start + BlockSize >= raw.Length ? LastBlock : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at), entry);
            scratch.AsSpan(0, encoded).CopyTo(output.AsSpan(at + 4));
            at += 4 + encoded;
        }
        return output[..at];
    }
}
