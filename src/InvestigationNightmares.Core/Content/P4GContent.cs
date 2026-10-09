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
/// Everything the mashup takes from Persona 4 Golden (64-bit Steam release), read from the player's own
/// install while Catherine runs: the Team's portraits (bustups inside the CRI .cpk archives) and voice
/// barks and music (ADX2 .awb wave archives inside them). Nothing is written back to P4G and nothing
/// from it is shipped with the mod. Every miss is recorded in <see cref="Problems"/> naming the sheet cell to fix.
/// </summary>
public sealed class P4GContent : IDisposable
{
    readonly string _root;
    readonly List<CriCpk> _cpks = new();
    // path (lower-case) -> (archive, entry); the first archive listed in the sheet wins
    readonly Dictionary<string, (CriCpk cpk, CpkEntry entry)> _files = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, IReadOnlyList<Bgra32Image>> _portraits = new();
    readonly ConcurrentDictionary<string, Pcm16?> _audio = new();
    readonly ConcurrentDictionary<string, Afs2?> _awbs = new();
    public ConcurrentQueue<string> Problems { get; } = new();

    public string Root => _root;
    public int ArchiveCount => _cpks.Count;
    public int FileCount => _files.Count;

    P4GContent(string root) => _root = root;

    /// <summary>Opens the archives of the P4G install at <paramref name="root"/>.</summary>
    public static P4GContent Open(string root)
    {
        var c = new P4GContent(root);
        var exe = SheetIndex.GameFiles["p4g_exe"];
        if (!File.Exists(Path.Combine(root, exe.Path)))
            c.Problems.Enqueue($"game_files[p4g_exe]: {exe.Path} not found in {root}; is this really Persona 4 Golden?");

        var row = SheetIndex.GameFiles["p4g_data_cpks"];
        foreach (var name in row.Path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path)) { c.Problems.Enqueue($"game_files[p4g_data_cpks]: {name} not found in {root}"); continue; }
            try
            {
                var cpk = CriCpk.Open(path);
                c._cpks.Add(cpk);
                foreach (var e in cpk.Entries) c._files.TryAdd(e.Path, (cpk, e));
            }
            catch (Exception e) { c.Problems.Enqueue($"game_files[p4g_data_cpks]: {name}: {e.Message}"); }
        }
        return c;
    }

    public IEnumerable<string> Paths => _files.Keys;

    public (CriCpk cpk, CpkEntry entry)? Locate(string path) => _files.TryGetValue(path, out var f) ? f : null;

    /// <summary>A file from the archives, decompressed; null if absent.</summary>
    public byte[]? ReadFile(string path) => _files.TryGetValue(path, out var f) ? f.cpk.Read(f.entry) : null;

    /// <summary>All portraits of a character, in name order (expression N = index N).</summary>
    public IReadOnlyList<Bgra32Image> Portraits(CharactersRow ch) => _portraits.GetOrAdd(ch.Id, _ => LoadPortraits(ch));

    public Bgra32Image? Portrait(CharactersRow ch, int expression)
    {
        var all = Portraits(ch);
        if (all.Count == 0) return null;
        return all[Math.Clamp(expression, 0, all.Count - 1)];
    }

    IReadOnlyList<Bgra32Image> LoadPortraits(CharactersRow ch)
    {
        var result = new List<Bgra32Image>();
        if (ch.BustupGlob == null) return result; // e.g. the P4 protagonist, who has no portrait
        var re = Glob.ToRegex(ch.BustupGlob);
        var problems = new List<string>();
        foreach (var path in _files.Keys.Where(p => re.IsMatch(p)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Take(16))
        {
            try
            {
                var found = ImageExtractor.Extract(ReadFile(path)!, path, problems);
                // Composite bustups (body + eye/mouth layers) come out as several images; the largest is the body.
                var best = found.OrderByDescending(f => (long)f.Image.Width * f.Image.Height).FirstOrDefault();
                if (best != null) result.Add(Compact(best.Image));
            }
            catch (Exception ex) { problems.Add($"{path}: {ex.Message}"); }
        }
        foreach (var p in problems.Take(5)) Problems.Enqueue($"characters[{ch.Id}].bustup_glob: {p}");
        if (result.Count == 0) Problems.Enqueue($"characters[{ch.Id}].bustup_glob: nothing in the P4G archives matches '{ch.BustupGlob}'");
        return result;
    }

    /// <summary>Trim the empty margin and halve big portraits: they're drawn at most about half the screen tall.</summary>
    static Bgra32Image Compact(Bgra32Image img)
    {
        var (x, y, w, h) = img.OpaqueBounds();
        var c = img.Crop(x, y, w, h);
        return c.Height > 900 ? c.Downscale(2) : c;
    }

    public Pcm16? Bark(CharactersRow ch, int index) => Wave(ch.VoiceSource, index, $"bark of {ch.Id}");

    public Pcm16? Music(MusicRow m) => m.WaveIndex is int i ? Wave(m.Source, i, $"music[{m.Id}].wave_index") : NoteMissing($"music[{m.Id}].wave_index is empty");

    Pcm16? NoteMissing(string msg) { Problems.Enqueue(msg); return null; }

    /// <summary>The AFS2 table of a game_files row like "data.cpk|sound/adx2/en/btlmem.awb".</summary>
    public (Afs2 awb, CriCpk cpk, CpkEntry entry)? WaveArchive(string gameFileId)
    {
        var row = SheetIndex.GameFiles[gameFileId];
        var inner = row.Path.Contains('|') ? row.Path[(row.Path.IndexOf('|') + 1)..] : row.Path;
        if (!_files.TryGetValue(inner, out var f)) { Problems.Enqueue($"game_files[{gameFileId}]: {inner} not in the P4G archives"); return null; }
        var awb = _awbs.GetOrAdd(gameFileId, _ =>
        {
            try
            {
                var first = f.cpk.ReadHead(f.entry, 16);
                return new Afs2(f.cpk.ReadHead(f.entry, Afs2.HeaderBytesFor(first)), f.entry.Offset);
            }
            catch (Exception e) { Problems.Enqueue($"game_files[{gameFileId}]: {e.Message}"); return null; }
        });
        return awb == null ? null : (awb, f.cpk, f.entry);
    }

    Pcm16? Wave(string bankId, int index, string what) => _audio.GetOrAdd($"{bankId}#{index}", _ =>
    {
        var arc = WaveArchive(bankId);
        if (arc == null) return null;
        var (awb, cpk, _) = arc.Value;
        if (index < 0 || index >= awb.Entries.Count) return NoteMissing($"{what}: index {index} outside {bankId} (0..{awb.Entries.Count - 1})");
        var e = awb.Entries[index];
        var head = cpk.ReadRange(e.Offset, (int)Math.Min(e.Size, 512));
        var info = CriStreamInfo.Identify(head);
        return NoteMissing($"{what}: stream is {info}; decoding {info.Codec} isn't in this version yet");
    });

    /// <summary>Load everything the sheets name up front (on a background thread) so nothing hitches mid-climb.</summary>
    public void Preload()
    {
        foreach (var ch in Sheets.Characters) Portraits(ch);
        foreach (var d in Sheets.Dialogue)
            if (d.BarkIndex is int b) Bark(SheetIndex.Characters[d.Speaker], b);
        foreach (var m in Sheets.Music) Music(m);
    }

    public void Dispose()
    {
        foreach (var c in _cpks) c.Dispose();
    }
}
