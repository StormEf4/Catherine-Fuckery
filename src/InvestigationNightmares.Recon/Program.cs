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

    int wav = a.IndexOf("--wav");
    if (wav >= 0)
    {
        var bankPath = Path.Combine(dir, a[wav + 1]);
        int from = int.Parse(a[wav + 2]), to = int.Parse(a[wav + 3]);
        using var bank = XactWaveBank.Open(bankPath);
        var clipDir = Path.Combine(outDir, "clips", Path.GetFileNameWithoutExtension(bankPath));
        Directory.CreateDirectory(clipDir);
        for (int i = from; i <= Math.Min(to, bank.Entries.Count - 1); i++)
        {
            try { File.WriteAllBytes(Path.Combine(clipDir, $"{i:D5}.wav"), bank.Decode(bank.Entries[i]).ToWav()); }
            catch (Exception e) { Console.WriteLine($"  {i}: {e.Message}"); }
        }
        Console.WriteLine($"clips written to {clipDir}");
        return 0;
    }

    // Archives
    var entriesTxt = new StringBuilder();
    var bustupDir = Path.Combine(outDir, "bustups");
    Directory.CreateDirectory(bustupDir);
    var bustupIndex = new StringBuilder();
    int pngs = 0;
    var allPaths = new List<string>();
    foreach (var f in Directory.EnumerateFiles(dir, "*.pac").OrderBy(f => f))
    {
        DwPack pack;
        try { pack = DwPack.Open(f); }
        catch (Exception e) { entriesTxt.AppendLine($"{Path.GetFileName(f)}: not DW_PACK ({e.Message})"); continue; }
        using (pack)
        {
            entriesTxt.AppendLine($"== {Path.GetFileName(f)}: {pack.Entries.Count} entries");
            foreach (var e in pack.Entries)
            {
                entriesTxt.AppendLine($"{e.Path}\t{e.UncompressedSize}\t{(e.Compressed ? "huffman" : "stored")}");
                allPaths.Add(e.Path);
            }
            foreach (var e in pack.Entries.Where(e => e.Path.Contains("bustup", StringComparison.OrdinalIgnoreCase)).Take(800))
            {
                var problems = new List<string>();
                List<ImageExtractor.Found> found;
                try { found = ImageExtractor.Extract(pack.Read(e), e.Path, problems); }
                catch (Exception ex) { bustupIndex.AppendLine($"{e.Path}\tERROR {ex.Message}"); continue; }
                foreach (var img in found)
                {
                    var name = img.Name.Replace('/', '_').Replace('@', '_') + ".png";
                    File.WriteAllBytes(Path.Combine(bustupDir, name), img.Image.ToPng());
                    bustupIndex.AppendLine($"{img.Name}\t{img.Image.Width}x{img.Image.Height}\t{name}");
                    pngs++;
                }
                foreach (var p in problems) bustupIndex.AppendLine($"{e.Path}\tPROBLEM {p}");
            }
        }
    }
    File.WriteAllText(Path.Combine(outDir, "pac_entries.txt"), entriesTxt.ToString());
    File.WriteAllText(Path.Combine(outDir, "bustup_index.txt"), bustupIndex.ToString());
    Console.WriteLine($"archives: {allPaths.Count} entries -> pac_entries.txt; {pngs} portrait images -> bustups/");

    // What each character's glob matches today
    var chars = new StringBuilder();
    foreach (var ch in Sheets.Characters)
    {
        var re = Glob.ToRegex(ch.BustupGlob);
        var hits = allPaths.Where(p => re.IsMatch(p)).ToList();
        chars.AppendLine($"{ch.Id} ({ch.BustupGlob}): {hits.Count} matches");
        foreach (var h in hits.Take(20)) chars.AppendLine("    " + h);
    }
    File.WriteAllText(Path.Combine(outDir, "characters.txt"), chars.ToString());
    Console.Write(chars);

    // Wave banks
    foreach (var xwb in Directory.EnumerateFiles(dir, "*.xwb", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(dir, xwb).Replace('\\', '/');
        var sb = new StringBuilder();
        try
        {
            using var bank = XactWaveBank.Open(xwb);
            sb.AppendLine($"{rel}: bank '{bank.BankName}', {bank.Entries.Count} entries");
            sb.AppendLine("index\tname\tcodec\tch\trate\tseconds");
            foreach (var e in bank.Entries)
                sb.AppendLine($"{e.Index}\t{e.Name}\t{e.Codec}\t{e.Channels}\t{e.SampleRate}\t{e.DurationSeconds:F2}");
            var longest = bank.Entries.OrderByDescending(e => e.DurationSeconds).Take(3).Select(e => $"#{e.Index} {e.DurationSeconds:F0}s");
            Console.WriteLine($"{rel}: {bank.Entries.Count} entries, longest {string.Join(", ", longest)}");
        }
        catch (Exception e) { sb.AppendLine($"{rel}: {e.Message}"); Console.WriteLine($"{rel}: {e.Message}"); }
        File.WriteAllText(Path.Combine(outDir, "xwb_" + rel.Replace('/', '_') + ".txt"), sb.ToString());
    }
    Console.WriteLine($"written to {outDir}");
    return 0;
}

int Catherine(List<string> a)
{
    var dir = Find("cc_exe", a) ?? throw new Exception("Catherine Classic not found; pass its folder");
    var outDir = Path.Combine(outRoot, "catherine");
    Directory.CreateDirectory(outDir);
    Console.WriteLine($"Catherine Classic: {dir}");
    var list = new StringBuilder();
    var byExt = new Dictionary<string, (int count, long bytes, string magic)>(StringComparer.OrdinalIgnoreCase);
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
    }
    File.WriteAllText(Path.Combine(outDir, "files.txt"), list.ToString());
    var summary = new StringBuilder("extension\tfiles\tMB\tfirst file starts with\n");
    foreach (var (ext, v) in byExt.OrderByDescending(kv => kv.Value.bytes))
        summary.AppendLine($"{ext}\t{v.count}\t{v.bytes / 1048576.0:F1}\t{v.magic}");
    File.WriteAllText(Path.Combine(outDir, "summary.txt"), summary.ToString());
    Console.Write(summary);
    Console.WriteLine($"written to {outDir}. Next: play into Night 1 with the mod enabled; its log lists each file opened per area.");
    return 0;
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
