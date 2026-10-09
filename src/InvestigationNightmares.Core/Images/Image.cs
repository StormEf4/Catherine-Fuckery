using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace InvestigationNightmares.Images;

/// <summary>A decoded picture, 4 bytes per pixel in B,G,R,A order (D3DFMT_A8R8G8B8 memory layout).</summary>
public sealed class Bgra32Image
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public Bgra32Image(int width, int height, byte[]? pixels = null)
    {
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
            throw new InvalidDataException($"bad image size {width}x{height}");
        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[width * height * 4];
        if (Pixels.Length != width * height * 4) throw new ArgumentException("pixel buffer size mismatch");
    }

    /// <summary>Tight bounding box of pixels with alpha above the threshold (bustups have big empty margins).</summary>
    public (int x, int y, int w, int h) OpaqueBounds(byte threshold = 8)
    {
        int minX = Width, minY = Height, maxX = -1, maxY = -1;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                if (Pixels[(y * Width + x) * 4 + 3] > threshold)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
        return maxX < 0 ? (0, 0, Width, Height) : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    public Bgra32Image Crop(int x, int y, int w, int h)
    {
        var o = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(Pixels, ((y + row) * Width + x) * 4, o, row * w * 4, w * 4);
        return new Bgra32Image(w, h, o);
    }

    /// <summary>Box-filtered shrink by an integer factor (previews).</summary>
    public Bgra32Image Downscale(int factor)
    {
        if (factor <= 1) return this;
        int w = Math.Max(1, Width / factor), h = Math.Max(1, Height / factor);
        var o = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0;
                    for (int dy = 0; dy < factor; dy++)
                        for (int dx = 0; dx < factor; dx++)
                            sum += Pixels[((y * factor + dy) * Width + x * factor + dx) * 4 + c];
                    o[(y * w + x) * 4 + c] = (byte)(sum / (factor * factor));
                }
        return new Bgra32Image(w, h, o);
    }

    public byte[] ToPng()
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), Height);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        WriteChunk(ms, "IHDR", ihdr);
        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                var line = new byte[1 + Width * 4];
                for (int y = 0; y < Height; y++)
                {
                    line[0] = 0;
                    for (int x = 0; x < Width; x++)
                    {
                        int s = (y * Width + x) * 4, d = 1 + x * 4;
                        line[d] = Pixels[s + 2]; line[d + 1] = Pixels[s + 1]; line[d + 2] = Pixels[s]; line[d + 3] = Pixels[s + 3];
                    }
                    z.Write(line);
                }
            }
            WriteChunk(ms, "IDAT", raw.ToArray());
        }
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint crc = Crc32(t, 0xFFFFFFFF);
        crc = Crc32(data, crc) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }

    static uint[]? _crcTable;

    static uint Crc32(byte[] data, uint crc)
    {
        var table = _crcTable ??= BuildCrcTable();
        foreach (var b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}
