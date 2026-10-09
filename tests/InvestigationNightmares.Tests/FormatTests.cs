using System;
using System.IO;
using System.Linq;
using System.Text;
using InvestigationNightmares.Audio;
using InvestigationNightmares.Formats;
using InvestigationNightmares.Images;
using InvestigationNightmares.Util;
using Xunit;

namespace InvestigationNightmares.Tests;

public class FormatTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(0x20000 * 2 + 17)]
    public void Huffman_round_trips(int size)
    {
        var rng = new Random(size);
        var data = new byte[size];
        // skewed distribution like real game data
        for (int i = 0; i < size; i++) data[i] = (byte)(rng.NextDouble() < 0.6 ? 0 : rng.Next(256));
        var packed = PreappHuffman.Compress(data);
        Assert.True(PreappHuffman.LooksCompressed(packed));
        Assert.Equal(data, PreappHuffman.Decompress(packed, size));
    }

    [Fact]
    public void Huffman_single_symbol_chunk()
    {
        var data = Enumerable.Repeat((byte)7, 300).ToArray();
        Assert.Equal(data, PreappHuffman.Decompress(PreappHuffman.Compress(data), data.Length));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DwPack_reads_back_what_was_built(bool compress)
    {
        var a = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("Investigation Team ", 200)));
        var b = new byte[] { 1, 2, 3 };
        var bytes = DwPack.Build(new[] { ("bustup/b002_000.bin", a), ("field/テレビ.bin", b) }, compress, packIndex: 3);
        using var pack = new DwPack(new MemoryStream(bytes));
        Assert.Equal(3, pack.PackIndex);
        Assert.Equal(new[] { "bustup/b002_000.bin", "field/テレビ.bin" }, pack.Entries.Select(e => e.Path));
        Assert.Equal(compress, pack.Entries[0].Compressed);
        Assert.Equal(a, pack.Read(pack.Entries[0]));
        Assert.Equal(b, pack.Read(pack.Entries[1]));
    }

    [Fact]
    public void DwPack_rejects_other_files()
    {
        Assert.Throws<InvalidDataException>(() => new DwPack(new MemoryStream(new byte[64])));
    }

    static byte[] MakeTmx8(int w, int h, Func<int, (byte r, byte g, byte b, byte a)> palette, Func<int, int, byte> index)
    {
        var d = new byte[0x40 + 256 * 4 + w * h];
        d[0] = 2;
        Encoding.ASCII.GetBytes("TMX0").CopyTo(d, 8);
        d[0x10] = 1; d[0x11] = 0x00;
        BitConverter.GetBytes((ushort)w).CopyTo(d, 0x12);
        BitConverter.GetBytes((ushort)h).CopyTo(d, 0x14);
        d[0x16] = 0x13;
        for (int i = 0; i < 256; i++)
        {
            // store in GS CSM1 order: logical index i lives at swapped position
            int stored = (i & 0xE7) | ((i & 0x08) << 1) | ((i & 0x10) >> 1);
            var (r, g, b, a) = palette(i);
            d[0x40 + stored * 4] = r; d[0x40 + stored * 4 + 1] = g; d[0x40 + stored * 4 + 2] = b; d[0x40 + stored * 4 + 3] = a;
        }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) d[0x40 + 1024 + y * w + x] = index(x, y);
        return d;
    }

    [Fact]
    public void Tmx_8bit_unswizzles_palette_and_scales_alpha()
    {
        var tmx = MakeTmx8(4, 2, i => ((byte)i, (byte)(255 - i), 0, i == 0 ? (byte)0 : (byte)0x80), (x, y) => (byte)(x == 0 && y == 0 ? 0 : 8 + x));
        var img = Tmx.Decode(tmx);
        Assert.Equal(4, img.Width);
        // pixel (0,0) = index 0: transparent
        Assert.Equal(0, img.Pixels[3]);
        // pixel (1,0) = index 9: r=9, g=246, opaque (0x80 -> 255)
        Assert.Equal(new byte[] { 0, 246, 9, 255 }, img.Pixels.AsSpan(4, 4).ToArray());
    }

    [Fact]
    public void Extractor_finds_tmx_inside_nested_pak()
    {
        var tmx = MakeTmx8(8, 8, i => (10, 20, 30, 0x80), (x, y) => 1);
        var inner = AtlusPak.Build(new[] { ("face.tmx", tmx) });
        var outer = AtlusPak.Build(new[] { ("readme.txt", Encoding.ASCII.GetBytes("hi")), ("parts.bin", inner) });
        var problems = new System.Collections.Generic.List<string>();
        var found = ImageExtractor.Extract(outer, "bustup/b002.bin", problems);
        var img = Assert.Single(found);
        Assert.Equal("bustup/b002.bin/parts.bin/face.tmx", img.Name);
        Assert.Equal(new byte[] { 30, 20, 10, 255 }, img.Image.Pixels.AsSpan(0, 4).ToArray());
    }

    [Fact]
    public void Dds_dxt1_decodes_endpoint_colors()
    {
        var d = new byte[128 + 8];
        Encoding.ASCII.GetBytes("DDS ").CopyTo(d, 0);
        BitConverter.GetBytes(124).CopyTo(d, 4);
        BitConverter.GetBytes(4).CopyTo(d, 12); BitConverter.GetBytes(4).CopyTo(d, 16);
        BitConverter.GetBytes(32).CopyTo(d, 76); BitConverter.GetBytes(4).CopyTo(d, 80);
        Encoding.ASCII.GetBytes("DXT1").CopyTo(d, 84);
        BitConverter.GetBytes((ushort)0xF800).CopyTo(d, 128); // red
        BitConverter.GetBytes((ushort)0x001F).CopyTo(d, 130); // blue
        d[132] = 0b0000_0001; // pixel0 = c1 (blue), others c0 (red)
        var img = Dds.Decode(d);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, img.Pixels.AsSpan(0, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, img.Pixels.AsSpan(4, 4).ToArray());
    }

    [Fact]
    public void Png_has_valid_signature_and_chunks()
    {
        var img = new Bgra32Image(3, 2);
        var png = img.ToPng();
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray());
        Assert.Contains("IEND", Encoding.ASCII.GetString(png));
    }

    static short[] Sine(int frames, int channels, double hz, int rate)
    {
        var s = new short[frames * channels];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++)
                s[i * channels + c] = (short)(12000 * Math.Sin(2 * Math.PI * hz * (i + c * 7) / rate));
        return s;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void MsAdpcm_tracks_a_sine(int channels)
    {
        int blockAlign = (70 + 22) * channels;
        var pcm = Sine(4000, channels, 440, 44100);
        var enc = MsAdpcm.Encode(pcm, channels, blockAlign);
        var dec = MsAdpcm.Decode(enc, channels, blockAlign);
        Assert.True(dec.Length >= pcm.Length);
        double err = 0, sig = 0;
        for (int i = 0; i < pcm.Length; i++) { err += Math.Pow(dec[i] - pcm[i], 2); sig += Math.Pow(pcm[i], 2); }
        double snrDb = 10 * Math.Log10(sig / err);
        Assert.True(snrDb > 15, $"SNR {snrDb:F1} dB");
    }

    [Fact]
    public void XactWaveBank_reads_pcm_and_adpcm_entries()
    {
        var pcm = Sine(1000, 1, 220, 22050);
        var pcmBytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, pcmBytes, 0, pcmBytes.Length);
        int alignField = 70; // XACT stores blockAlign/channels - 22
        var adpcm = MsAdpcm.Encode(Sine(3000, 2, 330, 44100), 2, (alignField + 22) * 2);
        var bank = XactWaveBank.Build("VOICE", new[]
        {
            (XactWaveBank.PackFormat(XactCodec.Pcm, 1, 22050, 2, true), pcmBytes, (string?)"yosuke_persona"),
            (XactWaveBank.PackFormat(XactCodec.Adpcm, 2, 44100, alignField, false), adpcm, (string?)"bgm_face"),
        }, withNames: true);

        using var wb = new XactWaveBank(new MemoryStream(bank));
        Assert.Equal("VOICE", wb.BankName);
        Assert.Equal(2, wb.Entries.Count);
        Assert.Equal("yosuke_persona", wb.Entries[0].Name);
        var e0 = wb.Decode(wb.Entries[0]);
        Assert.Equal(pcm, e0.Samples);
        var e1 = wb.Entries[1];
        Assert.Equal(XactCodec.Adpcm, e1.Codec);
        Assert.Equal(2, e1.Channels);
        Assert.Equal(44100, e1.SampleRate);
        Assert.Equal(184, e1.BlockAlign);
        var d1 = wb.Decode(e1);
        Assert.InRange(d1.DurationSeconds, 3000 / 44100.0, 3000 / 44100.0 + 0.01);
    }

    [Fact]
    public void Wav_round_trips()
    {
        var p = new Pcm16(2, 48000, Sine(100, 2, 1000, 48000));
        var back = Pcm16.FromWav(p.ToWav());
        Assert.Equal(p.Samples, back.Samples);
        Assert.Equal(48000, back.SampleRate);
        Assert.Equal(2, back.Channels);
    }

    [Fact]
    public void Pcm_converts_rate_and_channels()
    {
        var mono = new Pcm16(1, 22050, Sine(22050, 1, 100, 22050));
        var st = mono.Convert(44100, 2);
        Assert.Equal(2, st.Channels);
        Assert.Equal(44100, st.SampleRate);
        Assert.InRange(st.DurationSeconds, 0.999, 1.001);
        Assert.Equal(st.Samples[200], st.Samples[201]); // both channels carry the mono signal
        var back = st.Convert(22050, 1);
        Assert.InRange(Math.Abs(back.Samples[1000] - mono.Samples[1000]), 0, 300);
        Assert.Equal((2, 44100), Pcm16.ProbeWav(st.ToWav()));
        Assert.Null(Pcm16.ProbeWav(new byte[64]));
    }

    [Theory]
    [InlineData("bustup/*002_*", "bustup/b002_001.bin", true)]
    [InlineData("bustup/*002_*", "bustup/sub/b002_001.bin", false)]
    [InlineData("bustup/**002_*", "bustup/sub/b002_001.bin", true)]
    [InlineData("data*.pac", "DATA00004.PAC", true)]
    [InlineData("data?.pac", "data10.pac", false)]
    public void Glob_matches(string glob, string path, bool expected) => Assert.Equal(expected, Glob.IsMatch(glob, path));
}

public class CriTests
{
    [Fact]
    public void Utf_table_round_trips_all_column_types()
    {
        var bytes = UtfTable.Write("Test", new[] { "B", "U16", "U32", "U64", "Name", "Blob" }, new[]
        {
            new object[] { (byte)1, (ushort)500, 70000u, 1UL << 40, "bgm_boss_01", new byte[] { 9, 8 } },
            new object[] { (byte)2, (ushort)7, 3u, 5UL, "landing", new byte[] { 1 } },
        });
        var t = UtfTable.Read(bytes);
        Assert.Equal("Test", t.Name);
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal((ushort)500, t.Rows[0]["U16"]);
        Assert.Equal(1UL << 40, t.Rows[0]["U64"]);
        Assert.Equal("landing", t.Rows[1]["Name"]);
        Assert.Equal(new byte[] { 9, 8 }, (byte[])t.Rows[0]["Blob"]!);
    }

    [Fact]
    public void Cpk_lists_and_reads_files()
    {
        var a = Encoding.ASCII.GetBytes("ADX-ish data");
        var b = new byte[3000];
        new Random(1).NextBytes(b);
        var cpk = CriCpk.Build(new[] { ("bgm/bgm_01.adx", a), ("bustup/b002_000.bin", b) });
        using var c = new CriCpk(new MemoryStream(cpk));
        Assert.Equal(new[] { "bgm/bgm_01.adx", "bustup/b002_000.bin" }, c.Entries.Select(e => e.Path));
        Assert.Equal(a, c.ReadStored(c.Entries[0]));
        Assert.Equal(b, c.ReadStored(c.Entries[1]));
        Assert.False(c.Entries[1].Compressed);
    }
}

public class CrilaylaTests
{
    [Theory]
    [InlineData(0x100)]
    [InlineData(0x100 + 1)]
    [InlineData(0x100 + 5000)]
    public void Round_trips(int size)
    {
        var rng = new Random(size);
        var data = new byte[size];
        // mixture of runs, repeats and noise so both literals and back-references (incl. long VLE lengths) occur
        for (int i = 0; i < size; i++)
            data[i] = i % 700 < 300 ? (byte)(i % 7) : i % 700 < 400 ? (byte)0xAA : (byte)rng.Next(256);
        var c = Crilayla.Compress(data);
        Assert.True(Crilayla.IsCompressed(c));
        Assert.Equal(data, Crilayla.Decompress(c));
    }

    [Fact]
    public void Cpk_read_decompresses()
    {
        var data = new byte[3000];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i / 10);
        var cpk = CriCpk.Build(new[] { ("bustup/b2_1_1.bin", Crilayla.Compress(data)) });
        using var c = new CriCpk(new MemoryStream(cpk));
        Assert.Equal(data, c.Read(c.Entries[0]));
    }

    [Fact]
    public void Afs2_and_hca_headers_parse()
    {
        // AFS2 with two streams, alignment 32
        var hca = new byte[64];
        "HCA\0"u8.CopyTo(hca);
        hca[6] = 0; hca[7] = 60;
        "fmt\0"u8.CopyTo(hca.AsSpan(8));
        hca[12] = 2; hca[13] = 0x00; hca[14] = 0xAC; hca[15] = 0x44; // 44100
        hca[19] = 100;
        "ciph"u8.CopyTo(hca.AsSpan(24)); hca[29] = 56;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("AFS2"u8); w.Write(new byte[] { 2, 4, 2, 0 }); w.Write(2); w.Write((ushort)32); w.Write((ushort)0);
        w.Write((ushort)7); w.Write((ushort)9);
        w.Write(36); w.Write(36 + 64 + 28); w.Write(36 + 64 + 28 + 64);
        while (ms.Length < 64) w.Write((byte)0);
        w.Write(hca);
        while (ms.Length < 128) w.Write((byte)0);
        w.Write(hca);
        var afs = new Afs2(ms.ToArray());
        Assert.Equal(new[] { 7, 9 }, afs.Entries.Select(e => e.CueId));
        Assert.Equal(64, afs.Entries[0].Offset);
        Assert.Equal(128, afs.Entries[1].Offset);
        var info = CriStreamInfo.Identify(ms.ToArray().AsSpan((int)afs.Entries[1].Offset));
        Assert.Equal("HCA", info.Codec);
        Assert.Equal(2, info.Channels);
        Assert.Equal(44100, info.SampleRate);
        Assert.Equal(100, info.Blocks);
        Assert.Equal(56, info.CipherType);
    }
}
