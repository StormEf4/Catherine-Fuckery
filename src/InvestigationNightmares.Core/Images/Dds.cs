using System;
using System.Buffers.Binary;
using System.IO;

namespace InvestigationNightmares.Images;

/// <summary>DirectDraw Surface reader: DXT1/DXT3/DXT5 and uncompressed 32/24-bit, first mip only.</summary>
public static class Dds
{
    public static bool IsDds(ReadOnlySpan<byte> d) => d.Length >= 128 && d[..4].SequenceEqual("DDS "u8);

    public static Bgra32Image Decode(ReadOnlySpan<byte> d)
    {
        if (!IsDds(d)) throw new InvalidDataException("not a DDS");
        int h = BinaryPrimitives.ReadInt32LittleEndian(d[12..]);
        int w = BinaryPrimitives.ReadInt32LittleEndian(d[16..]);
        uint pfFlags = BinaryPrimitives.ReadUInt32LittleEndian(d[80..]);
        string fourCC = System.Text.Encoding.ASCII.GetString(d.Slice(84, 4));
        int bits = BinaryPrimitives.ReadInt32LittleEndian(d[88..]);
        uint rMask = BinaryPrimitives.ReadUInt32LittleEndian(d[92..]);
        uint gMask = BinaryPrimitives.ReadUInt32LittleEndian(d[96..]);
        uint bMask = BinaryPrimitives.ReadUInt32LittleEndian(d[100..]);
        uint aMask = BinaryPrimitives.ReadUInt32LittleEndian(d[104..]);
        var data = d[128..];
        var img = new Bgra32Image(w, h);

        if ((pfFlags & 0x4) != 0)
        {
            switch (fourCC)
            {
                case "DXT1": DecodeBlocks(data, img, 8, Dxt.DecodeDxt1Block); break;
                case "DXT3": DecodeBlocks(data, img, 16, Dxt.DecodeDxt3Block); break;
                case "DXT5": DecodeBlocks(data, img, 16, Dxt.DecodeDxt5Block); break;
                default: throw new InvalidDataException($"unsupported DDS fourCC {fourCC}");
            }
            return img;
        }

        int bpp = bits / 8;
        if (bpp != 4 && bpp != 3) throw new InvalidDataException($"unsupported DDS bit depth {bits}");
        bool hasAlpha = (pfFlags & 0x1) != 0 && aMask != 0;
        for (int i = 0; i < w * h; i++)
        {
            uint v = bpp == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]) : (uint)(data[i * 3] | data[i * 3 + 1] << 8 | data[i * 3 + 2] << 16);
            img.Pixels[i * 4 + 0] = Extract(v, bMask);
            img.Pixels[i * 4 + 1] = Extract(v, gMask);
            img.Pixels[i * 4 + 2] = Extract(v, rMask);
            img.Pixels[i * 4 + 3] = hasAlpha ? Extract(v, aMask) : (byte)255;
        }
        return img;
    }

    static byte Extract(uint v, uint mask)
    {
        if (mask == 0) return 0;
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        uint max = mask >> shift;
        return (byte)(((v & mask) >> shift) * 255 / max);
    }

    delegate void BlockDecoder(ReadOnlySpan<byte> block, Span<uint> out16);

    static void DecodeBlocks(ReadOnlySpan<byte> data, Bgra32Image img, int blockBytes, BlockDecoder dec)
    {
        int bw = (img.Width + 3) / 4, bh = (img.Height + 3) / 4;
        Span<uint> px = stackalloc uint[16];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                dec(data.Slice((by * bw + bx) * blockBytes, blockBytes), px);
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        int ix = bx * 4 + x, iy = by * 4 + y;
                        if (ix < img.Width && iy < img.Height)
                            BinaryPrimitives.WriteUInt32LittleEndian(img.Pixels.AsSpan((iy * img.Width + ix) * 4), px[y * 4 + x]);
                    }
            }
    }
}

/// <summary>S3TC block decoders producing 0xAARRGGBB.</summary>
public static class Dxt
{
    static uint Rgb565(int c) =>
        (uint)(((c >> 11) & 31) * 255 / 31) << 16 | (uint)(((c >> 5) & 63) * 255 / 63) << 8 | (uint)((c & 31) * 255 / 31);

    static void ColorBlock(ReadOnlySpan<byte> b, Span<uint> o, bool allowPunchThrough)
    {
        int c0 = BinaryPrimitives.ReadUInt16LittleEndian(b), c1 = BinaryPrimitives.ReadUInt16LittleEndian(b[2..]);
        Span<uint> pal = stackalloc uint[4];
        pal[0] = Rgb565(c0) | 0xFF000000;
        pal[1] = Rgb565(c1) | 0xFF000000;
        uint Mix(uint a, uint bb, int wa, int wb, int div)
        {
            uint r = (uint)((((a >> 16) & 255) * wa + ((bb >> 16) & 255) * wb) / div);
            uint g = (uint)((((a >> 8) & 255) * wa + ((bb >> 8) & 255) * wb) / div);
            uint bl = (uint)(((a & 255) * wa + (bb & 255) * wb) / div);
            return 0xFF000000 | r << 16 | g << 8 | bl;
        }
        if (c0 > c1 || !allowPunchThrough)
        {
            pal[2] = Mix(pal[0], pal[1], 2, 1, 3);
            pal[3] = Mix(pal[0], pal[1], 1, 2, 3);
        }
        else
        {
            pal[2] = Mix(pal[0], pal[1], 1, 1, 2);
            pal[3] = 0;
        }
        uint idx = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
        for (int i = 0; i < 16; i++) o[i] = pal[(int)((idx >> (2 * i)) & 3)];
    }

    public static void DecodeDxt1Block(ReadOnlySpan<byte> b, Span<uint> o) => ColorBlock(b, o, true);

    public static void DecodeDxt3Block(ReadOnlySpan<byte> b, Span<uint> o)
    {
        ColorBlock(b[8..], o, false);
        ulong a = BinaryPrimitives.ReadUInt64LittleEndian(b);
        for (int i = 0; i < 16; i++) o[i] = (o[i] & 0xFFFFFF) | (uint)(((a >> (4 * i)) & 15) * 17) << 24;
    }

    public static void DecodeDxt5Block(ReadOnlySpan<byte> b, Span<uint> o)
    {
        ColorBlock(b[8..], o, false);
        int a0 = b[0], a1 = b[1];
        Span<int> pal = stackalloc int[8];
        pal[0] = a0; pal[1] = a1;
        if (a0 > a1) for (int i = 1; i < 7; i++) pal[i + 1] = ((7 - i) * a0 + i * a1) / 7;
        else
        {
            for (int i = 1; i < 5; i++) pal[i + 1] = ((5 - i) * a0 + i * a1) / 5;
            pal[6] = 0; pal[7] = 255;
        }
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)b[2 + i] << (8 * i);
        for (int i = 0; i < 16; i++) o[i] = (o[i] & 0xFFFFFF) | (uint)pal[(int)((bits >> (3 * i)) & 7)] << 24;
    }
}
