using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using InvestigationNightmares.Audio;
using InvestigationNightmares.Formats;
using InvestigationNightmares.Generated;
using InvestigationNightmares.Images;
using InvestigationNightmares.Story;
using InvestigationNightmares.Util;

// recon: reads the player's own game installs and writes what it finds to ./recon-out, so the unverified
// cells of sheets/*.json can be filled in from facts. It never modifies either game. recon-out holds game
// content (portraits, clips) for you to look at: it is git-ignored and must not be shared.

const string Usage = """
    recon p4g [P4G folder]                         list P4G archives, portraits and wave banks
    recon p4g [P4G folder] --wav <bank.xwb> <from> <to>   export clips <from>..<to> of a bank as WAV to listen to
    recon catherine [Catherine Classic folder]     list Catherine's data files and their formats
    recon list <any folder>                        list a folder's files and look inside CRI .cpk/.acb/.csb and XACT .xwb files
    recon sheets                                   list the sheet cells that still need checking
    Folders default to the Steam install.
    """;

var argv = args.ToList();
if (argv.Count == 0) { Console.WriteLine(Usage); return 1; }
var outRoot = Path.GetFullPath("recon-out");

try
{
    switch (argv[0])
    {
        case "p4g": return P4G(argv.Skip(1).ToList());
        case "catherine": return Catherine(argv.Skip(1).ToList());
        case "sheets": return SheetsTodo();
        case "list" when argv.Count > 1: return ListFolder(argv[1], "list_" + Safe(Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(argv[1])))));
        default: Console.WriteLine(Usage); return 1;
    }
}
catch (Exception e)
{
    Console.Error.WriteLine($"recon: {e.Message}");
    return 2;
}

string? Find(string gameFileId, List<string> argv2)
{
    if (argv2.Count > 0 && !argv2[0].StartsWith("--")) return argv2[0];
    var row = SheetIndex.GameFiles[gameFileId];
    var roots = new List<string>();
    var env = Environment.GetEnvironmentVariable("STEAM_ROOT");
    if (env != null) roots.Add(env);
    roots.AddRange(new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".steam", "steam"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "Steam") });
    return SteamLibraries.FindApp(roots, row.SteamAppid, row.InstallDirName);
}

int P4G(List<string> a)
{
    var dir = Find("p4g_exe", a) ?? throw new Exception("Persona 4 Golden not found; pass its folder");
    var outDir = Path.Combine(outRoot, "p4g");
    Directory.CreateDirectory(outDir);
    Console.WriteLine($"P4G: {dir}");
    using var c = InvestigationNightmares.Content.P4GContent.Open(dir);
    Console.WriteLine($"{c.ArchiveCount} archives, {c.FileCount} files");
    if (c.ArchiveCount == 0)
    {
        foreach (var p in c.Problems) Console.WriteLine("  " + p);
        Console.WriteLine("No CRI archives found (data.cpk). Run: recon list \"" + dir + "\"");
        return 1;
    }

    // 1. One preview per bustup character number, to put names to numbers.
    var prev = Path.Combine(outDir, "portraits");
    Directory.CreateDirectory(prev);
    var bustups = c.Paths.Where(p => p.StartsWith("bustup/", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    var byChar = bustups.GroupBy(p => System.Text.RegularExpressions.Regex.Match(p, @"bustup/b(\d+)_").Groups[1].Value).Where(g => g.Key != "");
    var index = new StringBuilder();
    void Export(string path, string file)
    {
        try
        {
            var problems = new List<string>();
            var found = ImageExtractor.Extract(c.ReadFile(path)!, path, problems);
            var best = found.OrderByDescending(f => (long)f.Image.Width * f.Image.Height).FirstOrDefault();
            if (best == null) { index.AppendLine($"{path}\tno picture found: {string.Join("; ", problems)}"); return; }
            File.WriteAllBytes(Path.Combine(prev, file), best.Image.Downscale(4).ToPng());
            index.AppendLine($"{path}\t{best.Image.Width}x{best.Image.Height}\t{best.Name}\t{file}");
        }
        catch (Exception e) { index.AppendLine($"{path}\tERROR {e.Message}"); }
    }
    foreach (var g in byChar.OrderBy(g => int.Parse(g.Key)))
    {
        var pick = g.FirstOrDefault(p => p.EndsWith($"b{g.Key}_1_1.bin", StringComparison.OrdinalIgnoreCase)) ?? g.First();
        Export(pick, $"char_{int.Parse(g.Key):D2}.png");
    }
    // Every Yosuke variant (one frame each), to find his Shadow.
    foreach (var p in bustups.Where(p => System.Text.RegularExpressions.Regex.IsMatch(p, @"bustup/b2_\d+_[01]\.bin$")))
        Export(p, "yosuke_" + Path.GetFileNameWithoutExtension(p) + ".png");
    // The first persona cards, to find Izanagi and Jiraiya.
    foreach (var p in c.Paths.Where(p => p.StartsWith("card/persona/", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p).Take(40))
        Export(p, "card_" + Path.GetFileNameWithoutExtension(p) + ".png");
    File.WriteAllText(Path.Combine(outDir, "portraits.txt"), index.ToString());
    Console.WriteLine($"portrait previews -> {prev}");

    // 2. What each character's glob matches today.
    var chars = new StringBuilder();
    foreach (var ch in Sheets.Characters)
    {
        if (ch.BustupGlob == null) { chars.AppendLine($"{ch.Id}: no portrait (by design)"); continue; }
        var re = Glob.ToRegex(ch.BustupGlob);
        var hits = c.Paths.Where(p => re.IsMatch(p)).OrderBy(p => p).ToList();
        chars.AppendLine($"{ch.Id} ({ch.BustupGlob}): {hits.Count} matches  {string.Join(" ", hits.Take(12))}");
    }
    File.WriteAllText(Path.Combine(outDir, "characters.txt"), chars.ToString());
    Console.Write(chars);

    // 3. Audio: what codec and encryption the wave archives use, and the music cue names.
    var audio = new StringBuilder();
    foreach (var awbPath in c.Paths.Where(p => p.EndsWith(".awb", StringComparison.OrdinalIgnoreCase) &&
                 (p.Contains("/bgm", StringComparison.OrdinalIgnoreCase) || p.Contains("btl", StringComparison.OrdinalIgnoreCase) || p.Contains("commu", StringComparison.OrdinalIgnoreCase))).OrderBy(p => p))
    {
        try
        {
            var row = Sheets.GameFiles.FirstOrDefault(r => r.Path.EndsWith("|" + awbPath, StringComparison.OrdinalIgnoreCase));
            var (cpk, entry) = FindEntry(c, awbPath);
            var first = cpk.ReadHead(entry, 16);
            var afs = new Afs2(cpk.ReadHead(entry, Afs2.HeaderBytesFor(first)), entry.Offset);
            audio.AppendLine($"== {awbPath}: {afs.Entries.Count} streams, subkey {afs.Subkey}{(row != null ? $" (sheet: {row.Id})" : "")}");
            foreach (var e in afs.Entries.Take(5))
                audio.AppendLine($"  #{e.Index} cue {e.CueId} {e.Size} bytes: {CriStreamInfo.Identify(cpk.ReadRange(e.Offset, (int)Math.Min(e.Size, 512)))}");
        }
        catch (Exception e) { audio.AppendLine($"== {awbPath}: {e.Message}"); }
    }
    foreach (var acb in new[] { "sound/adx2/bgm/snd00_bgm.acb", "sound/adx2/bgm_en/snd00_bgm.acb" })
    {
        var bytes = c.ReadFile(acb);
        if (bytes == null) continue;
        try { var t = UtfTable.Read(bytes); audio.AppendLine($"== {acb}: cue names"); DumpNames(t, audio, "  ", 0); }
        catch (Exception e) { audio.AppendLine($"== {acb}: {e.Message}"); }
    }
    File.WriteAllText(Path.Combine(outDir, "audio.txt"), audio.ToString());
    Console.WriteLine(string.Join("\n", audio.ToString().Split('\n').Where(l => l.StartsWith("==") || l.StartsWith("  #0"))));
    Console.WriteLine($"written to {outDir} (portraits/, portraits.txt, characters.txt, audio.txt)");
    return 0;
}

static (CriCpk cpk, CpkEntry entry) FindEntry(InvestigationNightmares.Content.P4GContent c, string path) => c.Locate(path) ?? throw new Exception($"{path} not found");

int Catherine(List<string> a)
{
    var dir = Find("cc_exe", a) ?? throw new Exception("Catherine Classic not found; pass its folder");
    Console.WriteLine($"Catherine Classic: {dir}");
    int r = ListFolder(dir, "catherine");
    Console.WriteLine("Next: play into Night 1 with the mod enabled; its log lists each file opened per area.");
    return r;
}

static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

int ListFolder(string dir, string outName)
{
    if (!Directory.Exists(dir)) throw new Exception($"folder not found: {dir}");
    var outDir = Path.Combine(outRoot, outName);
    Directory.CreateDirectory(outDir);
    var list = new StringBuilder();
    var byExt = new Dictionary<string, (int count, long bytes, string magic)>(StringComparer.OrdinalIgnoreCase);
    var inside = new StringBuilder();
    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f))
    {
        var rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
        var info = new FileInfo(f);
        var head = new byte[8];
        try { using var s = File.OpenRead(f); s.ReadAtLeast(head, 8, throwOnEndOfStream: false); } catch { }
        var magic = Convert.ToHexString(head) + " " + new string(head.Select(b => b is >= 32 and < 127 ? (char)b : '.').ToArray());
        list.AppendLine($"{rel}\t{info.Length}\t{magic}");
        var ext = Path.GetExtension(f);
        byExt.TryGetValue(ext, out var cur);
        byExt[ext] = (cur.count + 1, cur.bytes + info.Length, cur.magic ?? magic);
        try { LookInside(f, rel, head, inside); }
        catch (Exception e) { inside.AppendLine($"== {rel}: could not read inside ({e.Message})"); }
    }
    File.WriteAllText(Path.Combine(outDir, "files.txt"), list.ToString());
    var summary = new StringBuilder("extension\tfiles\tMB\tfirst file starts with\n");
    foreach (var (ext, v) in byExt.OrderByDescending(kv => kv.Value.bytes))
        summary.AppendLine($"{ext}\t{v.count}\t{v.bytes / 1048576.0:F1}\t{v.magic}");
    File.WriteAllText(Path.Combine(outDir, "summary.txt"), summary.ToString());
    File.WriteAllText(Path.Combine(outDir, "inside.txt"), inside.ToString());
    Console.Write(summary);
    Console.WriteLine($"written to {outDir} (files.txt, summary.txt, inside.txt)");
    return 0;
}

static void LookInside(string path, string rel, byte[] head, StringBuilder o)
{
    if (head.AsSpan(0, 4).SequenceEqual("CPK "u8))
    {
        using var cpk = CriCpk.Open(path);
        o.AppendLine($"== {rel}: CRI CPK, {cpk.Entries.Count} files");
        foreach (var e in cpk.Entries)
        {
            string magic = "";
            try { var b = cpk.ReadStored(e); magic = Convert.ToHexString(b.AsSpan(0, Math.Min(8, b.Length))); } catch { }
            o.AppendLine($"  {e.Path}\t{e.ExtractSize}\t{(e.Compressed ? "crilayla" : "stored")}\t{magic}");
        }
    }
    else if (head.AsSpan(0, 4).SequenceEqual("WBND"u8))
    {
        using var bank = XactWaveBank.Open(path);
        o.AppendLine($"== {rel}: XACT wave bank '{bank.BankName}', {bank.Entries.Count} entries (index name codec ch rate seconds)");
        foreach (var e in bank.Entries) o.AppendLine($"  {e.Index}\t{e.Name}\t{e.Codec}\t{e.Channels}\t{e.SampleRate}\t{e.DurationSeconds:F2}");
    }
    else if ((rel.EndsWith(".acb", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".csb", StringComparison.OrdinalIgnoreCase)) && UtfTable.LooksLikeUtf(head))
    {
        var t = UtfTable.Read(File.ReadAllBytes(path));
        o.AppendLine($"== {rel}: CRI @UTF '{t.Name}' ({t.Rows.Count} rows)");
        DumpNames(t, o, "  ", 0);
    }
}

// Prints the string cells (cue and track names) of a CRI table and the tables nested in its data cells.
static void DumpNames(UtfTable t, StringBuilder o, string indent, int depth)
{
    if (depth > 4) return;
    for (int r = 0; r < t.Rows.Count && r < 400; r++)
    {
        var strings = t.Rows[r].Where(kv => kv.Value is string s && s.Length > 0).Select(kv => $"{kv.Key}={kv.Value}").ToList();
        var nums = t.Rows[r].Where(kv => kv.Value is byte or ushort or uint or short or int && kv.Key.Contains("Index", StringComparison.OrdinalIgnoreCase)).Select(kv => $"{kv.Key}={kv.Value}");
        if (strings.Count > 0) o.AppendLine($"{indent}[{t.Name} {r}] {string.Join("  ", strings.Concat(nums))}");
        foreach (var kv in t.Rows[r])
            if (kv.Value is byte[] blob && UtfTable.LooksLikeUtf(blob))
            {
                try { DumpNames(UtfTable.Read(blob), o, indent + "  ", depth + 1); } catch { }
            }
    }
}

int SheetsTodo()
{
    int n = 0;
    void Rows<T>(string sheet, IEnumerable<T> rows, Func<T, string> id, Func<T, string[]> unv)
    {
        foreach (var r in rows)
            foreach (var c in unv(r)) { Console.WriteLine($"{sheet}[{id(r)}].{c}"); n++; }
    }
    Rows("game_files", Sheets.GameFiles, r => r.Id, r => r.Unverified);
    Rows("characters", Sheets.Characters, r => r.Id, r => r.Unverified);
    Rows("dialogue", Sheets.Dialogue, r => r.Id, r => r.Unverified);
    Rows("music", Sheets.Music, r => r.Id, r => r.Unverified);
    Rows("triggers", Sheets.Triggers, r => r.Id, r => r.Unverified);
    Rows("bindings", Sheets.Bindings, r => r.Id, r => r.Unverified);
    Rows("powers", Sheets.Powers, r => r.Id, r => r.Unverified);
    Rows("hooks", Sheets.Hooks, r => r.Id, r => r.Unverified);
    Rows("tuning", Sheets.Tuning, r => r.Id, r => r.Unverified);
    Console.WriteLine($"{n} cells to check");
    return 0;
}
