using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace InvestigationNightmares.Audio;

public enum XactCodec { Pcm = 0, Xma = 1, Adpcm = 2, Wma = 3 }

public sealed record XactEntry(int Index, string? Name, XactCodec Codec, int Channels, int SampleRate, int BlockAlign,
    int BitsPerSample, long DataOffset, int DataLength)
{
    /// <summary>MS-ADPCM samples per channel per block.</summary>
    public int AdpcmSamplesPerBlock => (BlockAlign - 7 * Channels) * 2 / Channels + 2;

    public double DurationSeconds => Codec switch
    {
        XactCodec.Pcm => DataLength / (double)(Channels * (BitsPerSample / 8) * SampleRate),
        XactCodec.Adpcm => DataLength / (double)BlockAlign * AdpcmSamplesPerBlock / SampleRate,
        _ => 0,
    };
}

/// <summary>
/// XACT3 wave bank (.xwb) reader, as used by Persona 4 Golden PC's SND folder.
/// Header: "WBND", u32 contentVersion, u32 headerVersion, 5 segments {offset, length}:
/// bank data, entry metadata, seek tables, entry names, wave data.
/// </summary>
public sealed class XactWaveBank : IDisposable
{
    const uint FlagEntryNames = 0x10000, FlagCompact = 0x20000;
    readonly Stream _s;
    public string BankName { get; }
    public IReadOnlyList<XactEntry> Entries { get; }

    public XactWaveBank(Stream s)
    {
        _s = s;
        var r = new BinaryReader(s, Encoding.ASCII, leaveOpen: true);
        s.Position = 0;
        if (r.ReadUInt32() != 0x444E4257) throw new InvalidDataException("not a little-endian XACT wave bank (WBND)");
        uint version = r.ReadUInt32();
        if (version < 42) throw new InvalidDataException($"XACT wave bank version {version} is older than XACT3");
        r.ReadUInt32(); // header version
        var seg = new (int off, int len)[5];
        for (int i = 0; i < 5; i++) seg[i] = (r.ReadInt32(), r.ReadInt32());

        s.Position = seg[0].off;
        uint flags = r.ReadUInt32();
        int count = r.ReadInt32();
        BankName = Encoding.ASCII.GetString(r.ReadBytes(64)).TrimEnd('\0');
        int metaSize = r.ReadInt32();
        int nameSize = r.ReadInt32();
        int alignment = r.ReadInt32();
        uint compactFormat = r.ReadUInt32();

        string?[] names = new string?[count];
        if ((flags & FlagEntryNames) != 0 && seg[3].len > 0 && nameSize > 0)
        {
            s.Position = seg[3].off;
            for (int i = 0; i < count; i++) names[i] = Encoding.ASCII.GetString(r.ReadBytes(nameSize)).TrimEnd('\0');
        }

        var list = new List<XactEntry>(count);
        long dataBase = seg[4].off;
        if ((flags & FlagCompact) != 0)
        {
            s.Position = seg[1].off;
            var raw = new uint[count];
            for (int i = 0; i < count; i++) raw[i] = r.ReadUInt32();
            for (int i = 0; i < count; i++)
            {
                long off = (raw[i] & 0x1FFFFF) * (long)alignment;
                long next = i + 1 < count ? (raw[i + 1] & 0x1FFFFF) * (long)alignment : seg[4].len;
                int len = (int)(next - off - (raw[i] >> 21));
                list.Add(MakeEntry(i, names[i], compactFormat, dataBase + off, len));
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                s.Position = seg[1].off + (long)i * metaSize;
                uint format, off, len;
                if (metaSize >= 16)
                {
                    r.ReadUInt32(); // flags + duration
                    format = r.ReadUInt32();
                    off = r.ReadUInt32();
                    len = r.ReadUInt32();
                }
                else if (metaSize >= 12)
                {
                    format = r.ReadUInt32();
                    off = r.ReadUInt32();
                    len = r.ReadUInt32();
                }
                else throw new InvalidDataException($"unsupported XACT entry size {metaSize}");
                list.Add(MakeEntry(i, names[i], format, dataBase + off, (int)len));
            }
        }
        Entries = list;
    }

    public static XactWaveBank Open(string path) => new(File.OpenRead(path));

    static XactEntry MakeEntry(int index, string? name, uint f, long off, int len)
    {
        var codec = (XactCodec)(f & 3);
        int channels = (int)((f >> 2) & 7);
        int rate = (int)((f >> 5) & 0x3FFFF);
        int align = (int)((f >> 23) & 0xFF);
        int bits = (f >> 31) != 0 ? 16 : 8;
        int blockAlign = codec switch
        {
            XactCodec.Adpcm => (align + 22) * channels,
            XactCodec.Pcm => channels * bits / 8,
            _ => align,
        };
        return new XactEntry(index, name, codec, channels, rate, blockAlign, codec == XactCodec.Adpcm ? 4 : bits, off, len);
    }

    public byte[] ReadRaw(XactEntry e)
    {
        var buf = new byte[e.DataLength];
        lock (_s)
        {
            _s.Position = e.DataOffset;
            _s.ReadExactly(buf);
        }
        return buf;
    }

    /// <summary>Decode an entry to 16-bit PCM. Throws NotSupportedException for XMA and xWMA.</summary>
    public Pcm16 Decode(XactEntry e)
    {
        var raw = ReadRaw(e);
        switch (e.Codec)
        {
            case XactCodec.Pcm when e.BitsPerSample == 16:
                {
                    var s = new short[raw.Length / 2];
                    Buffer.BlockCopy(raw, 0, s, 0, s.Length * 2);
                    return new Pcm16(e.Channels, e.SampleRate, s);
                }
            case XactCodec.Pcm:
                {
                    var s = new short[raw.Length];
                    for (int i = 0; i < raw.Length; i++) s[i] = (short)((raw[i] - 128) << 8);
                    return new Pcm16(e.Channels, e.SampleRate, s);
                }
            case XactCodec.Adpcm:
                return new Pcm16(e.Channels, e.SampleRate, MsAdpcm.Decode(raw, e.Channels, e.BlockAlign));
            default:
                throw new NotSupportedException($"{e.Codec} audio in wave bank entry {e.Index} is not supported");
        }
    }

    public void Dispose() => _s.Dispose();

    /// <summary>Build a non-compact bank (tests). formats are already-packed mini formats.</summary>
    public static byte[] Build(string bankName, IReadOnlyList<(uint miniFormat, byte[] data, string? name)> entries, bool withNames)
    {
        const int metaSize = 24, nameSize = 64, alignment = 4;
        int bankDataOff = 52, bankDataLen = 96;
        int metaOff = bankDataOff + bankDataLen, metaLen = entries.Count * metaSize;
        int namesOff = metaOff + metaLen, namesLen = withNames ? entries.Count * nameSize : 0;
        int waveOff = (namesOff + namesLen + 3) & ~3;
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0x444E4257u); w.Write(46u); w.Write(44u);
        w.Write(bankDataOff); w.Write(bankDataLen);
        w.Write(metaOff); w.Write(metaLen);
        w.Write(namesOff); w.Write(0);
        w.Write(namesOff); w.Write(namesLen);
        int waveLen = 0;
        foreach (var e in entries) waveLen += (e.data.Length + 3) & ~3;
        w.Write(waveOff); w.Write(waveLen);
        w.Write(withNames ? FlagEntryNames : 0u); w.Write(entries.Count);
        var bn = new byte[64]; Encoding.ASCII.GetBytes(bankName).CopyTo(bn, 0); w.Write(bn);
        w.Write(metaSize); w.Write(nameSize); w.Write(alignment); w.Write(0u); w.Write(0L);
        int off = 0;
        foreach (var e in entries)
        {
            w.Write(0u); w.Write(e.miniFormat); w.Write(off); w.Write(e.data.Length); w.Write(0); w.Write(0);
            off += (e.data.Length + 3) & ~3;
        }
        if (withNames)
            foreach (var e in entries) { var nb = new byte[nameSize]; Encoding.ASCII.GetBytes(e.name ?? "").CopyTo(nb, 0); w.Write(nb); }
        while (ms.Length < waveOff) w.Write((byte)0);
        foreach (var e in entries) { w.Write(e.data); while (ms.Length % 4 != 0) w.Write((byte)0); }
        return ms.ToArray();
    }

    public static uint PackFormat(XactCodec codec, int channels, int rate, int blockAlignField, bool sixteenBit) =>
        (uint)codec | (uint)channels << 2 | (uint)rate << 5 | (uint)blockAlignField << 23 | (sixteenBit ? 1u << 31 : 0);
}
