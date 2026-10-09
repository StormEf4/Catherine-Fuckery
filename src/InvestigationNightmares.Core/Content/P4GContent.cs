using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InvestigationNightmares.Audio;
using InvestigationNightmares.Formats;
using InvestigationNightmares.Generated;
using InvestigationNightmares.Images;
using InvestigationNightmares.Story;
using InvestigationNightmares.Util;

namespace InvestigationNightmares.Content;

/// <summary>
/// Everything the mashup takes from Persona 4 Golden, read from the player's own install while Catherine
/// runs: the Team's portraits (from the DW_PACK archives), their voice barks and the music (from the XACT
/// wave banks). Nothing is written back to P4G and nothing from it is shipped with the mod.
/// Every miss is recorded in <see cref="Problems"/> so the log says exactly which sheet cell to fix.
/// </summary>
public sealed class P4GContent : IDisposable
{
    readonly string _root;
    readonly List<DwPack> _packs = new();
    readonly Dictionary<string, XactWaveBank> _banks = new();
    readonly ConcurrentDictionary<string, IReadOnlyList<Bgra32Image>> _portraits = new();
    readonly ConcurrentDictionary<string, Pcm16?> _audio = new();
    public ConcurrentQueue<string> Problems { get; } = new();

    public string Root => _root;

    P4GContent(string root) => _root = root;

    /// <summary>Opens the archives of the P4G install at <paramref name="root"/>.</summary>
    public static P4GContent Open(string root)
    {
        var c = new P4GContent(root);
        var exe = SheetIndex.GameFiles["p4g_exe"];
        if (!File.Exists(Path.Combine(root, exe.Path)))
            c.Problems.Enqueue($"game_files[p4g_exe]: {exe.Path} not found in {root}; is this really Persona 4 Golden?");

        var pacGlob = SheetIndex.GameFiles["p4g_data_pacs"].Path;
        var re = Glob.ToRegex(pacGlob);
        foreach (var f in Directory.EnumerateFiles(root, "*.pac", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (!re.IsMatch(Path.GetFileName(f))) continue;
            try { c._packs.Add(DwPack.Open(f)); }
            catch (Exception e) { c.Problems.Enqueue($"game_files[p4g_data_pacs]: {Path.GetFileName(f)}: {e.Message}"); }
        }
        if (c._packs.Count == 0) c.Problems.Enqueue($"game_files[p4g_data_pacs]: no DW_PACK archive matching '{pacGlob}' in {root}");

        foreach (var row in Sheets.GameFiles.Where(g => g.Game == "p4g" && g.Format == "xact_wavebank"))
        {
            var path = Path.Combine(root, row.Path);
            if (!File.Exists(path)) { c.Problems.Enqueue($"game_files[{row.Id}]: {row.Path} not found"); continue; }
            try { c._banks[row.Id] = XactWaveBank.Open(path); }
            catch (Exception e) { c.Problems.Enqueue($"game_files[{row.Id}]: {e.Message}"); }
        }
        return c;
    }

    public int PackCount => _packs.Count;

    /// <summary>All portraits of a character, in the order of their archive paths (expression N = index N).</summary>
    public IReadOnlyList<Bgra32Image> Portraits(CharactersRow ch) => _portraits.GetOrAdd(ch.Id, _ => LoadPortraits(ch));

    public Bgra32Image? Portrait(CharactersRow ch, int expression)
    {
        var all = Portraits(ch);
        if (all.Count == 0) return null;
        return all[Math.Clamp(expression, 0, all.Count - 1)];
    }

    IReadOnlyList<Bgra32Image> LoadPortraits(CharactersRow ch)
    {
        var re = Glob.ToRegex(ch.BustupGlob);
        var result = new List<Bgra32Image>();
        var problems = new List<string>();
        foreach (var pack in _packs)
            foreach (var e in pack.Entries.Where(e => re.IsMatch(e.Path)).OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var found = ImageExtractor.Extract(pack.Read(e), e.Path, problems);
                    // Composite bustups (base + eyes/mouth layers) come out as several images; the largest is the body.
                    var best = found.OrderByDescending(f => (long)f.Image.Width * f.Image.Height).FirstOrDefault();
                    if (best != null) result.Add(best.Image);
                }
                catch (Exception ex) { problems.Add($"{e.Path}: {ex.Message}"); }
                if (result.Count >= 16) break;
            }
        foreach (var p in problems.Take(5)) Problems.Enqueue($"characters[{ch.Id}].bustup_glob: {p}");
        if (result.Count == 0) Problems.Enqueue($"characters[{ch.Id}].bustup_glob: nothing in the P4G archives matches '{ch.BustupGlob}'");
        return result;
    }

    public Pcm16? Bark(CharactersRow ch, int index) => Wave(ch.VoiceSource, index, $"bark of {ch.Id}");

    public Pcm16? Music(MusicRow m) => m.WaveIndex is int i ? Wave(m.Source, i, $"music[{m.Id}].wave_index") : NoteMissing($"music[{m.Id}].wave_index is empty");

    Pcm16? NoteMissing(string msg) { Problems.Enqueue(msg); return null; }

    Pcm16? Wave(string bankId, int index, string what) => _audio.GetOrAdd($"{bankId}#{index}", _ =>
    {
        if (!_banks.TryGetValue(bankId, out var bank)) return NoteMissing($"{what}: wave bank {bankId} isn't open");
        if (index < 0 || index >= bank.Entries.Count) return NoteMissing($"{what}: index {index} outside {bankId} (0..{bank.Entries.Count - 1})");
        try { return bank.Decode(bank.Entries[index]); }
        catch (Exception e) { return NoteMissing($"{what}: {e.Message}"); }
    });

    /// <summary>Load everything the sheets name up front (on a background thread) so nothing hitches mid-climb.</summary>
    public void Preload()
    {
        foreach (var ch in Sheets.Characters) Portraits(ch);
        foreach (var d in Sheets.Dialogue)
            if (d.BarkIndex is int b) Bark(SheetIndex.Characters[d.Speaker], b);
        foreach (var m in Sheets.Music) Music(m);
    }

    public IEnumerable<(string bankId, XactWaveBank bank)> Banks => _banks.Select(kv => (kv.Key, kv.Value));
    public IEnumerable<DwPack> Packs => _packs;

    public void Dispose()
    {
        foreach (var p in _packs) p.Dispose();
        foreach (var b in _banks.Values) b.Dispose();
    }
}
