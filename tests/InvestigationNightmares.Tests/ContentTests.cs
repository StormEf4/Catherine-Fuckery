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
/// A synthetic Persona 4 Golden (64-bit) folder laid out like the player's real one: data.cpk with
/// CRILAYLA-compressed bustups and an AFS2 voice archive, data_e.cpk with an English override.
/// Built from our own bytes, not game content. Proves the loader wiring end to end.
/// </summary>
public sealed class FakeP4GInstall : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("fake-p4g").FullName;

    public static byte[] Tmx(byte r, byte g, byte b, byte a = 0xFF, int w = 16, int h = 24)
    {
        var d = new byte[0x40 + w * h * 4];
        Encoding.ASCII.GetBytes("TMX0").CopyTo(d, 8);
        BitConverter.GetBytes((ushort)w).CopyTo(d, 0x12);
        BitConverter.GetBytes((ushort)h).CopyTo(d, 0x14);
        d[0x16] = 0x00; // PSMCT32
        for (int i = 0; i < w * h; i++) { d[0x40 + i * 4] = r; d[0x40 + i * 4 + 1] = g; d[0x40 + i * 4 + 2] = b; d[0x40 + i * 4 + 3] = a; }
        return d;
    }

    public FakeP4GInstall()
    {
        File.WriteAllBytes(Path.Combine(Root, "P4G.exe"), new byte[] { 0x4D, 0x5A });
        // An unknown 140-byte wrapper around the TMX, like the real 5,243,084-byte bustups.
        static byte[] Wrapped(byte[] tmx) => Crilayla.Compress(new byte[75].Concat(tmx).Concat(new byte[64]).ToArray());
        static Pcm16 Tone(int rate, int ch, double hz)
        {
            var smp = new short[rate / 4 * ch];
            for (int i = 0; i < smp.Length; i++) smp[i] = (short)(9000 * Math.Sin(2 * Math.PI * hz * (i / ch) / rate));
            return new Pcm16(ch, rate, smp);
        }
        // Voices are ADX, music is HCA, like the player's install.
        var voices = Afs2.Build(new[] { CriDecoder.Encode(Tone(24000, 1, 300), hca: false), CriDecoder.Encode(Tone(24000, 1, 500), hca: false) });
        var music = Afs2.Build(new[] { CriDecoder.Encode(Tone(48000, 2, 220), hca: true) });
        File.WriteAllBytes(Path.Combine(Root, "data.cpk"), CriCpk.Build(new[]
        {
            ("bustup/b2_1_1.bin", Wrapped(Tmx(224, 128, 42))),
            ("bustup/b2_2_1.bin", Wrapped(Tmx(200, 100, 40))),
            ("bustup/b2_10_1.bin", Wrapped(Tmx(1, 1, 1))),
            ("bustup/b8_1_0.bin", Wrapped(Tmx(217, 71, 59))),
            ("card/persona/i_prc001.tmx", Tmx(30, 60, 200, 0xFF, 32, 32)),
            ("sound/adx2/en/btlmem.awb", voices),
            ("sound/adx2/bgm/snd00_bgm.awb", music),
        }));
        File.WriteAllBytes(Path.Combine(Root, "data_e.cpk"), CriCpk.Build(new[] { ("bustup/b8_1_0.bin", Wrapped(Tmx(9, 9, 9))) }));
    }

    public void Dispose() => Directory.Delete(Root, true);
}

public class ContentTests
{
    [Fact]
    public void Loads_portraits_from_cpk_archives_like_the_players_install()
    {
        using var fake = new FakeP4GInstall();
        using var c = P4GContent.Open(fake.Root);
        Assert.Equal(2, c.ArchiveCount);

        var yosuke = SheetIndex.Characters["yosuke"];
        var portraits = c.Portraits(yosuke);
        Assert.Equal(2, portraits.Count); // b2_1_1 and b2_2_1; b2_10_1 isn't a talking expression
        Assert.Equal(new byte[] { 42, 128, 224, 255 }, portraits[0].Pixels.Take(4).ToArray());
        Assert.Same(portraits[1], c.Portrait(yosuke, 7));

        // data_e.cpk (English) overrides data.cpk
        Assert.Equal(new byte[] { 9, 9, 9, 255 }, c.Portrait(SheetIndex.Characters["teddie"], 0)!.Pixels.Take(4).ToArray());

        Assert.Empty(c.Portraits(SheetIndex.Characters["yu"])); // P4's protagonist has no portrait, by design
        Assert.Empty(c.Portraits(SheetIndex.Characters["chie"]));
        Assert.Contains(c.Problems, p => p.StartsWith("characters[chie].bustup_glob"));
        Assert.DoesNotContain(c.Problems, p => p.StartsWith("characters[yu].bustup"));

        // Shadow Yosuke is Yosuke's real portrait with the Shadow treatment (P4G has no Shadow portrait).
        var shadow = c.Portrait(SheetIndex.Characters["shadow_yosuke"], 0)!;
        Assert.Equal(portraits[0].Width, shadow.Width);
        Assert.NotEqual(portraits[0].Pixels, shadow.Pixels);

        // Izanagi's card for Yu's cut-in
        Assert.NotNull(c.CutIn(SheetIndex.Characters["yu"]));
        Assert.Null(c.CutIn(SheetIndex.Characters["shadow_yosuke"]));
    }

    [Fact]
    public void Voice_barks_decode_from_adx_and_music_from_hca()
    {
        using var fake = new FakeP4GInstall();
        using var c = P4GContent.Open(fake.Root);
        var arc = c.WaveArchive("p4g_voice_bank");
        Assert.Equal(2, arc!.Value.awb.Entries.Count);

        var bark = c.Bark(SheetIndex.Characters["yosuke"], 1);
        Assert.NotNull(bark);
        Assert.Equal(24000, bark!.SampleRate);
        Assert.InRange(bark.DurationSeconds, 0.2, 0.3);
        Assert.Null(c.Bark(SheetIndex.Characters["yosuke"], 5));
        Assert.Contains(c.Problems, p => p.Contains("index 5 outside"));

        var song = c.Music(SheetIndex.Music["bgm_ill_face_myself_battle"] with { WaveIndex = 0 });
        Assert.NotNull(song);
        Assert.Equal(2, song!.Channels);
        Assert.Equal(48000, song.SampleRate);
    }

    [Fact]
    public void Missing_install_pieces_are_named_by_sheet_cell()
    {
        var empty = Directory.CreateTempSubdirectory("empty-p4g").FullName;
        using var c = P4GContent.Open(empty);
        Assert.Contains(c.Problems, p => p.StartsWith("game_files[p4g_exe]"));
        Assert.Contains(c.Problems, p => p.StartsWith("game_files[p4g_data_cpks]"));
        Directory.Delete(empty, true);
    }

    [Fact]
    public void Tmx_alpha_scale_follows_the_data()
    {
        Assert.Equal(255, Images.Tmx.Decode(FakeP4GInstall.Tmx(1, 2, 3, 0x80)).Pixels[3]); // PS2 range: doubled
        Assert.Equal(0xC0, Images.Tmx.Decode(FakeP4GInstall.Tmx(1, 2, 3, 0xC0)).Pixels[3]); // already 0..255
    }
}
