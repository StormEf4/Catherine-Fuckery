using System;
using System.IO;
using System.Linq;
using System.Text;
using InvestigationNightmares.Audio;
using InvestigationNightmares.Content;
using InvestigationNightmares.Formats;
using InvestigationNightmares.Story;
using Xunit;

namespace InvestigationNightmares.Tests;

/// <summary>
/// A synthetic Persona 4 Golden folder laid out the way the sheets expect (made of our own bytes, not game
/// content). Proves the loader wiring end to end; whether the real game matches the sheets is what the
/// unverified cells and `recon` are for.
/// </summary>
public sealed class FakeP4GInstall : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("fake-p4g").FullName;

    public FakeP4GInstall()
    {
        File.WriteAllBytes(Path.Combine(Root, "P4G.exe"), new byte[] { 0x4D, 0x5A });
        static byte[] Tmx(byte r, byte g, byte b)
        {
            int w = 16, h = 24;
            var d = new byte[0x40 + w * h * 4];
            Encoding.ASCII.GetBytes("TMX0").CopyTo(d, 8);
            BitConverter.GetBytes((ushort)w).CopyTo(d, 0x12);
            BitConverter.GetBytes((ushort)h).CopyTo(d, 0x14);
            d[0x16] = 0x00; // PSMCT32
            for (int i = 0; i < w * h; i++) { d[0x40 + i * 4] = r; d[0x40 + i * 4 + 1] = g; d[0x40 + i * 4 + 2] = b; d[0x40 + i * 4 + 3] = 0x80; }
            return d;
        }
        var pack = DwPack.Build(new[]
        {
            ("bustup/b002_000.bin", AtlusPak.Build(new[] { ("b002_000.tmx", Tmx(224, 128, 42)) })),
            ("bustup/b002_001.bin", AtlusPak.Build(new[] { ("b002_001.tmx", Tmx(200, 100, 40)) })),
            ("bustup/b008_000.bin", Tmx(217, 71, 59)),
            ("field/other.bin", new byte[100]),
        }, compress: true);
        File.WriteAllBytes(Path.Combine(Root, "data00000.pac"), pack);

        Directory.CreateDirectory(Path.Combine(Root, "SND"));
        var pcm = new short[2205];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)(8000 * Math.Sin(i / 5.0));
        var bytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        var fmt = XactWaveBank.PackFormat(XactCodec.Pcm, 1, 22050, 2, true);
        File.WriteAllBytes(Path.Combine(Root, "SND", "ROOT.xwb"), XactWaveBank.Build("ROOT", new[] { (fmt, bytes, (string?)null), (fmt, bytes, (string?)null) }, false));
        File.WriteAllBytes(Path.Combine(Root, "SND", "BGM.xwb"), XactWaveBank.Build("BGM", new[] { (fmt, bytes, (string?)null) }, false));
    }

    public void Dispose() => Directory.Delete(Root, true);
}

public class ContentTests
{
    [Fact]
    public void Loads_portraits_and_barks_from_an_install_laid_out_like_the_sheets()
    {
        using var fake = new FakeP4GInstall();
        using var c = P4GContent.Open(fake.Root);
        Assert.Equal(1, c.PackCount);

        var yosuke = SheetIndex.Characters["yosuke"];
        var portraits = c.Portraits(yosuke);
        Assert.Equal(2, portraits.Count);
        Assert.Equal(new byte[] { 42, 128, 224, 255 }, portraits[0].Pixels.Take(4).ToArray());
        Assert.Same(portraits[1], c.Portrait(yosuke, 7)); // expression clamps to what exists

        Assert.Single(c.Portraits(SheetIndex.Characters["teddie"]));
        Assert.Empty(c.Portraits(SheetIndex.Characters["chie"]));
        Assert.Contains(c.Problems, p => p.StartsWith("characters[chie].bustup_glob"));

        var bark = c.Bark(yosuke, 1);
        Assert.NotNull(bark);
        Assert.Equal(22050, bark!.SampleRate);
        Assert.Null(c.Bark(yosuke, 99));
        Assert.Contains(c.Problems, p => p.Contains("index 99 outside"));

        // Music rows have no wave_index yet: reported, not crashed.
        Assert.Null(c.Music(SheetIndex.Music["bgm_ill_face_myself_battle"]));
        Assert.Contains(c.Problems, p => p.Contains("music[bgm_ill_face_myself_battle].wave_index is empty"));
    }

    [Fact]
    public void Missing_install_pieces_are_named_by_sheet_cell()
    {
        var empty = Directory.CreateTempSubdirectory("empty-p4g").FullName;
        using var c = P4GContent.Open(empty);
        Assert.Contains(c.Problems, p => p.StartsWith("game_files[p4g_exe]"));
        Assert.Contains(c.Problems, p => p.StartsWith("game_files[p4g_data_pacs]"));
        Assert.Contains(c.Problems, p => p.StartsWith("game_files[p4g_voice_bank]"));
        Directory.Delete(empty, true);
    }
}
