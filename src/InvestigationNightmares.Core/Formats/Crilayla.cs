using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace InvestigationNightmares.Formats;

/// <summary>
/// CRI's CRILAYLA compression (used for most files inside Persona 4 Golden's 64-bit CPKs).
/// Layout: "CRILAYLA", u32 uncompressed size (excluding the first 0x100 bytes), u32 offset of the stored
/// 0x100-byte prefix (relative to 0x10), compressed bits, then that prefix. The bit stream is read
/// backwards from the end of the compressed bytes, MSB first, and output is produced back to front:
/// 1 bit flag; 0 = 8-bit literal, 1 = 13-bit distance (+3) and a length coded with 2/3/5/8-bit steps.
/// </summary>
public static class Crilayla
{
    public static ReadOnlySpan<byte> Magic => "CRILAYLA"u8;
    static readonly int[] VleBits = { 2, 3, 5, 8 };

    public static bool IsCompressed(ReadOnlySpan<byte> d) => d.Length >= 16 + 0x100 && d[..8].SequenceEqual(Magic);

    public static byte[] Decompress(ReadOnlySpan<byte> input)
    {
        if (!IsCompressed(input)) throw new InvalidDataException("not CRILAYLA data");
        int usize = BinaryPrimitives.ReadInt32LittleEndian(input[8..]);
        int hdrOff = BinaryPrimitives.ReadInt32LittleEndian(input[12..]);
        if (usize < 0 || hdrOff < 0 || 0x10 + hdrOff + 0x100 > input.Length) throw new InvalidDataException("bad CRILAYLA header");
        var result = new byte[usize + 0x100];
        input.Slice(0x10 + hdrOff, 0x100).CopyTo(result);

        int inPos = 0x10 + hdrOff - 1;
        int bitPool = 0, bitsLeft = 0;
        int Bits(int count, ReadOnlySpan<byte> src)
        {
            int outBits = 0, produced = 0;
            while (produced < count)
            {
                if (bitsLeft == 0)
                {
                    if (inPos < 0x10) throw new InvalidDataException("CRILAYLA stream ended early");
                    bitPool = src[inPos--];
                    bitsLeft = 8;
                }
                int take = Math.Min(bitsLeft, count - produced);
                outBits = (outBits << take) | ((bitPool >> (bitsLeft - take)) & ((1 << take) - 1));
                bitsLeft -= take;
                produced += take;
            }
            return outBits;
        }

        int outEnd = 0x100 + usize - 1;
        int written = 0;
        while (written < usize)
        {
            if (Bits(1, input) != 0)
            {
                int refPos = outEnd - written + Bits(13, input) + 3;
                int length = 3;
                int level;
                for (level = 0; level < VleBits.Length; level++)
                {
                    int v = Bits(VleBits[level], input);
                    length += v;
                    if (v != (1 << VleBits[level]) - 1) break;
                }
                if (level == VleBits.Length)
                {
                    int v;
                    do { v = Bits(8, input); length += v; } while (v == 255);
                }
                if (refPos >= result.Length) throw new InvalidDataException("CRILAYLA back-reference out of range");
                for (int i = 0; i < length && written < usize; i++)
                {
                    result[outEnd - written] = result[refPos--];
                    written++;
                }
            }
            else
            {
                result[outEnd - written] = (byte)Bits(8, input);
                written++;
            }
        }
        return result;
    }

    /// <summary>
    /// Compressor (greedy matches in a small window). Used to build test fixtures and to check the
    /// decoder against the format, not tuned for size.
    /// </summary>
    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x100) throw new ArgumentException("CRILAYLA needs at least 0x100 bytes");
        var body = data[0x100..].ToArray();
        int n = body.Length;
        var bits = new List<bool>();
        void Put(int value, int count) { for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }

        // Output is produced from the end of the body backwards; position p is the next byte to produce.
        int p = n - 1;
        while (p >= 0)
        {
            int bestLen = 0, bestDist = 0;
            for (int dist = 3; dist <= Math.Min(8191 + 3, n - 1 - p); dist++)
            {
                int len = 0;
                while (len < 300 && p - len >= 0 && body[p - len] == body[p + dist - len]) len++;
                if (len > bestLen) { bestLen = len; bestDist = dist; }
            }
            if (bestLen >= 3)
            {
                Put(1, 1);
                Put(bestDist - 3, 13);
                int rest = bestLen - 3;
                bool done = false;
                foreach (var b in VleBits)
                {
                    int max = (1 << b) - 1;
                    int v = Math.Min(rest, max);
                    Put(v, b);
                    rest -= v;
                    if (v != max) { done = true; break; }
                }
                if (!done)
                {
                    while (true) { int v = Math.Min(rest, 255); Put(v, 8); rest -= v; if (v != 255) break; }
                }
                p -= bestLen;
            }
            else
            {
                Put(0, 1);
                Put(body[p], 8);
                p--;
            }
        }
        var packed = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++) if (bits[i]) packed[i >> 3] |= (byte)(0x80 >> (i & 7));
        var o = new byte[0x10 + packed.Length + 0x100];
        Magic.CopyTo(o);
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(8), n);
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(12), packed.Length);
        for (int k = 0; k < packed.Length; k++) o[0x10 + packed.Length - 1 - k] = packed[k];
        data[..0x100].CopyTo(o.AsSpan(0x10 + packed.Length));
        return o;
    }
}
