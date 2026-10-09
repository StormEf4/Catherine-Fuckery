using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace InvestigationNightmares.Audio;

/// <summary>One stream inside an AFS2 (.awb) container.</summary>
public sealed record Afs2Entry(int Index, int CueId, long Offset, long Size);

/// <summary>
/// CRI ADX2 wave archive (.awb, "AFS2"): u8 version, u8 offset size, u8 id size, u8, u32 count,
/// u16 alignment, u16 subkey, then ids, then count+1 offsets (each start rounded up to the alignment).
/// </summary>
public sealed class Afs2
{
    public IReadOnlyList<Afs2Entry> Entries { get; }
    public int Subkey { get; }

    public Afs2(ReadOnlySpan<byte> header, long baseOffset = 0)
    {
        if (header.Length < 16 || !header[..4].SequenceEqual("AFS2"u8)) throw new InvalidDataException("not an AFS2 (.awb) archive");
        int offSize = header[5], idSize = header[6];
        int count = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        int align = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);
        Subkey = BinaryPrimitives.ReadUInt16LittleEndian(header[14..]);
        if (count < 0 || count > 1_000_000 || (offSize != 2 && offSize != 4) || (idSize != 2 && idSize != 4)) throw new InvalidDataException("bad AFS2 header");
        int idsAt = 16, offsAt = 16 + count * idSize;
        if (header.Length < offsAt + (count + 1) * offSize) throw new InvalidDataException("AFS2 header truncated");
        var h = header.ToArray();
        long Off(int i) => offSize == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(offsAt + i * 4)) : BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(offsAt + i * 2));
        var list = new List<Afs2Entry>(count);
        for (int i = 0; i < count; i++)
        {
            int id = idSize == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(header[(idsAt + i * 2)..]) : BinaryPrimitives.ReadInt32LittleEndian(header[(idsAt + i * 4)..]);
            long start = Off(i), end = Off(i + 1);
            if (align > 1) start = (start + align - 1) / align * align;
            list.Add(new Afs2Entry(i, id, baseOffset + start, end - start));
        }
        Entries = list;
    }

    /// <summary>Build an AFS2 archive (tests and tools): ids are 0..n-1, offsets 4 bytes, alignment 32.</summary>
    public static byte[] Build(IReadOnlyList<byte[]> streams)
    {
        const int align = 32;
        int n = streams.Count;
        int headerLen = 16 + n * 2 + (n + 1) * 4;
        var offsets = new long[n + 1];
        long pos = headerLen;
        for (int i = 0; i < n; i++)
        {
            offsets[i] = pos;
            long start = (pos + align - 1) / align * align;
            pos = start + streams[i].Length;
        }
        offsets[n] = pos;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("AFS2"u8); w.Write(new byte[] { 2, 4, 2, 0 }); w.Write(n); w.Write((ushort)align); w.Write((ushort)0);
        for (int i = 0; i < n; i++) w.Write((ushort)i);
        foreach (var o in offsets) w.Write((uint)o);
        for (int i = 0; i < n; i++)
        {
            long start = (offsets[i] + align - 1) / align * align;
            while (ms.Length < start) w.Write((byte)0);
            w.Write(streams[i]);
        }
        return ms.ToArray();
    }

    /// <summary>How many header bytes to read to parse an AFS2 with this many entries (upper bound).</summary>
    public static int HeaderBytesFor(ReadOnlySpan<byte> first16)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(first16[8..]);
        return 16 + count * 4 + (count + 1) * 4;
    }
}

/// <summary>What a CRI audio stream is, from its first bytes.</summary>
public sealed record CriStreamInfo(string Codec, int Channels, int SampleRate, int Blocks, int CipherType)
{
    public override string ToString() => Codec == "HCA" ? $"HCA {Channels}ch {SampleRate}Hz blocks={Blocks} cipher={CipherType}" : Codec;

    public static CriStreamInfo Identify(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 8 && (BinaryPrimitives.ReadUInt32BigEndian(d) & 0x7F7F7F7F) == 0x48434100) // "HCA\0", possibly masked
        {
            int headerSize = BinaryPrimitives.ReadUInt16BigEndian(d[6..]);
            int pos = 8, ch = 0, rate = 0, blocks = 0, cipher = 0;
            while (pos + 4 <= Math.Min(headerSize, d.Length))
            {
                uint tag = BinaryPrimitives.ReadUInt32BigEndian(d[pos..]) & 0x7F7F7F7F;
                if (tag == 0x666D7400 && pos + 16 <= d.Length) // fmt
                {
                    ch = d[pos + 4];
                    rate = (d[pos + 5] << 16) | (d[pos + 6] << 8) | d[pos + 7];
                    blocks = BinaryPrimitives.ReadInt32BigEndian(d[(pos + 8)..]);
                    pos += 16;
                }
                else if (tag == 0x63697068 && pos + 6 <= d.Length) // ciph
                {
                    cipher = BinaryPrimitives.ReadUInt16BigEndian(d[(pos + 4)..]);
                    pos += 6;
                }
                else if (tag == 0x636F6D70) pos += 16; // comp
                else if (tag == 0x64656300) pos += 12; // dec
                else if (tag == 0x76627220) pos += 8;  // vbr
                else if (tag == 0x61746800) pos += 6;  // ath
                else if (tag == 0x6C6F6F70) pos += 16; // loop
                else if (tag == 0x72766100) pos += 8;  // rva
                else if (tag == 0x636F6D6D) break;     // comm (variable)
                else if (tag == 0x70616400) break;     // pad
                else break;
            }
            return new CriStreamInfo("HCA", ch, rate, blocks, cipher);
        }
        if (d.Length >= 0x14 && d[0] == 0x80 && d[1] == 0x00)
            return new CriStreamInfo($"ADX type {d[4]}", d[7], (int)BinaryPrimitives.ReadUInt32BigEndian(d[8..]), 0, 0);
        if (d.Length >= 4 && d[..4].SequenceEqual("RIFF"u8)) return new CriStreamInfo("RIFF/WAV", 0, 0, 0, 0);
        return new CriStreamInfo("unknown " + Convert.ToHexString(d[..Math.Min(8, d.Length)]), 0, 0, 0, 0);
    }
}

/// <summary>Decodes CRI ADX and HCA streams to 16-bit PCM using VGAudio (MIT, Alex Barney).</summary>
public static class CriDecoder
{
    public static Pcm16 Decode(byte[] stream)
    {
        var info = CriStreamInfo.Identify(stream);
        VGAudio.Formats.AudioData audio = info.Codec switch
        {
            "HCA" when info.CipherType == 0 => new VGAudio.Containers.Hca.HcaReader().Read(stream),
            "HCA" => throw new NotSupportedException($"encrypted HCA (cipher {info.CipherType})"),
            _ when info.Codec.StartsWith("ADX") => new VGAudio.Containers.Adx.AdxReader().Read(stream),
            _ => throw new NotSupportedException($"{info.Codec} audio"),
        };
        var pcm = audio.GetFormat<VGAudio.Formats.Pcm16.Pcm16Format>();
        int ch = pcm.Channels.Length, frames = pcm.Channels[0].Length;
        var inter = new short[ch * frames];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < ch; c++) inter[f * ch + c] = pcm.Channels[c][f];
        return new Pcm16(ch, pcm.SampleRate, inter);
    }

    /// <summary>Encode PCM as ADX or HCA (test fixtures).</summary>
    public static byte[] Encode(Pcm16 pcm, bool hca)
    {
        int frames = pcm.Samples.Length / pcm.Channels;
        var chans = new short[pcm.Channels][];
        for (int c = 0; c < pcm.Channels; c++)
        {
            chans[c] = new short[frames];
            for (int f = 0; f < frames; f++) chans[c][f] = pcm.Samples[f * pcm.Channels + c];
        }
        var fmt = new VGAudio.Formats.Pcm16.Pcm16Format(chans, pcm.SampleRate);
        return hca ? new VGAudio.Containers.Hca.HcaWriter().GetFile(fmt) : new VGAudio.Containers.Adx.AdxWriter().GetFile(fmt);
    }
}
