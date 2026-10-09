using System;
using System.Buffers.Binary;
using System.IO;

namespace InvestigationNightmares.Images;

/// <summary>
/// Atlus TMX textures (PS2-era Persona format, used for P4's bustups).
/// 0x40-byte header: u16 flag, u16 userId, u32 fileSize, "TMX0" at 0x08, u8 paletteCount at 0x10,
/// u8 paletteFormat, u16 width, u16 height, u8 pixelFormat at 0x16, u8 mipCount, ..., char[28] comment.
/// Palette (if any) follows the header, then pixels. Formats are GS PSM codes.
/// 8-bit palettes are stored in the GS's CSM1 order (bits 3 and 4 of the index swapped), and alpha
/// runs 0..0x80, which is scaled to 0..0xFF.
/// </summary>
public static class Tmx
{
    public const int HeaderSize = 0x40;
    const byte PSMCT32 = 0x00, PSMCT24 = 0x01, PSMCT16 = 0x02, PSMCT16S = 0x0A, PSMT8 = 0x13, PSMT4 = 0x14;

    public static bool IsTmx(ReadOnlySpan<byte> d) => d.Length >= HeaderSize && d.Slice(8, 4).SequenceEqual("TMX0"u8);

    public static Bgra32Image Decode(ReadOnlySpan<byte> d)
    {
        if (!IsTmx(d)) throw new InvalidDataException("not a TMX");
        int palCount = d[0x10];
        byte palFmt = d[0x11];
        int w = BinaryPrimitives.ReadUInt16LittleEndian(d[0x12..]);
        int h = BinaryPrimitives.ReadUInt16LittleEndian(d[0x14..]);
        byte pixFmt = d[0x16];
        var img = new Bgra32Image(w, h);
        int pos = HeaderSize;

        uint[]? palette = null;
        if (pixFmt == PSMT8 || pixFmt == PSMT4)
        {
            if (palCount < 1) throw new InvalidDataException("indexed TMX without a palette");
            int colors = pixFmt == PSMT8 ? 256 : 16;
            palette = new uint[colors];
            int bpp = ColorBytes(palFmt);
            for (int i = 0; i < colors; i++)
                palette[i] = ReadColor(d.Slice(pos + i * bpp, bpp), palFmt);
            pos += colors * bpp * palCount; // only the first palette is used
            if (colors == 256)
            {
                var tiled = (uint[])palette.Clone();
                for (int i = 0; i < 256; i++) palette[i] = tiled[(i & 0xE7) | ((i & 0x08) << 1) | ((i & 0x10) >> 1)];
            }
        }

        var px = img.Pixels;
        for (int i = 0; i < w * h; i++)
        {
            uint c;
            switch (pixFmt)
            {
                case PSMT8: c = palette![d[pos + i]]; break;
                case PSMT4:
                    {
                        byte b = d[pos + i / 2];
                        c = palette![(i & 1) == 0 ? b & 0xF : b >> 4];
                        break;
                    }
                default:
                    {
                        int bpp = ColorBytes(pixFmt);
                        c = ReadColor(d.Slice(pos + i * bpp, bpp), pixFmt);
                        break;
                    }
            }
            BinaryPrimitives.WriteUInt32LittleEndian(px.AsSpan(i * 4), c);
        }
        return img;
    }

    static int ColorBytes(byte fmt) => fmt switch
    {
        PSMCT32 => 4,
        PSMCT24 => 3,
        PSMCT16 or PSMCT16S => 2,
        _ => throw new InvalidDataException($"unsupported TMX color format 0x{fmt:X2}"),
    };

    /// <summary>Returns 0xAARRGGBB (little-endian memory = B,G,R,A).</summary>
    static uint ReadColor(ReadOnlySpan<byte> s, byte fmt)
    {
        int r, g, b, a;
        switch (fmt)
        {
            case PSMCT32: r = s[0]; g = s[1]; b = s[2]; a = Math.Min(255, s[3] * 2); break;
            case PSMCT24: r = s[0]; g = s[1]; b = s[2]; a = 255; break;
            default:
                {
                    int v = BinaryPrimitives.ReadUInt16LittleEndian(s);
                    r = (v & 0x1F) << 3; g = ((v >> 5) & 0x1F) << 3; b = ((v >> 10) & 0x1F) << 3;
                    a = (v & 0x8000) != 0 ? 255 : 0;
                    break;
                }
        }
        return (uint)(a << 24 | r << 16 | g << 8 | b);
    }
}
