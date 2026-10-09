using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace InvestigationNightmares.Util;

/// <summary>Finds a Steam game's install folder from Steam's own library list (libraryfolders.vdf + appmanifest_*.acf).</summary>
public static class SteamLibraries
{
    /// <summary>Minimal Valve KeyValues text parser: returns nested dictionaries (case-insensitive keys).</summary>
    public static Dictionary<string, object> ParseVdf(string text)
    {
        int i = 0;
        var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        ParseBlock(text, ref i, root, topLevel: true);
        return root;
    }

    static void ParseBlock(string t, ref int i, Dictionary<string, object> into, bool topLevel)
    {
        while (true)
        {
            SkipWs(t, ref i);
            if (i >= t.Length) { if (!topLevel) throw new FormatException("unterminated VDF block"); return; }
            if (t[i] == '}') { i++; if (topLevel) throw new FormatException("unexpected } in VDF"); return; }
            string key = ReadToken(t, ref i);
            SkipWs(t, ref i);
            if (i < t.Length && t[i] == '{')
            {
                i++;
                var child = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                ParseBlock(t, ref i, child, topLevel: false);
                into[key] = child;
            }
            else into[key] = ReadToken(t, ref i);
        }
    }

    static void SkipWs(string t, ref int i)
    {
        while (i < t.Length)
        {
            if (char.IsWhiteSpace(t[i])) i++;
            else if (t[i] == '/' && i + 1 < t.Length && t[i + 1] == '/') { while (i < t.Length && t[i] != '\n') i++; }
            else break;
        }
    }

    static string ReadToken(string t, ref int i)
    {
        var sb = new StringBuilder();
        if (i < t.Length && t[i] == '"')
        {
            i++;
            while (i < t.Length && t[i] != '"')
            {
                if (t[i] == '\\' && i + 1 < t.Length) { i++; sb.Append(t[i] switch { 'n' => '\n', 't' => '\t', _ => t[i] }); }
                else sb.Append(t[i]);
                i++;
            }
            if (i >= t.Length) throw new FormatException("unterminated VDF string");
            i++;
        }
        else
            while (i < t.Length && !char.IsWhiteSpace(t[i]) && t[i] != '{' && t[i] != '}' && t[i] != '"') sb.Append(t[i++]);
        if (sb.Length == 0 && (i >= t.Length || t[i] == '{' || t[i] == '}')) throw new FormatException("missing VDF token");
        return sb.ToString();
    }

    /// <summary>All library roots listed by a Steam install (including the Steam folder itself).</summary>
    public static List<string> LibraryRoots(string steamRoot)
    {
        var roots = new List<string> { steamRoot };
        foreach (var vdfPath in new[] { Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"), Path.Combine(steamRoot, "config", "libraryfolders.vdf") })
        {
            if (!File.Exists(vdfPath)) continue;
            var vdf = ParseVdf(File.ReadAllText(vdfPath));
            foreach (var top in vdf.Values)
                if (top is Dictionary<string, object> lib)
                    foreach (var kv in lib)
                    {
                        // New format: "0" { "path" "D:\\Steam" ... }  Old format: "1" "D:\\Steam"
                        string? p = kv.Value is Dictionary<string, object> d && d.TryGetValue("path", out var pv) ? pv as string
                                  : int.TryParse(kv.Key, out _) ? kv.Value as string : null;
                        if (!string.IsNullOrEmpty(p) && !roots.Exists(r => string.Equals(Path.GetFullPath(r), Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase)))
                            roots.Add(p);
                    }
        }
        return roots;
    }

    /// <summary>Install folder of a Steam app, or null. Falls back to the expected folder name when the manifest is missing.</summary>
    public static string? FindApp(IEnumerable<string> steamRoots, int appId, string fallbackDirName)
    {
        foreach (var steam in steamRoots)
        {
            if (!Directory.Exists(steam)) continue;
            foreach (var lib in LibraryRoots(steam))
            {
                var apps = Path.Combine(lib, "steamapps");
                var manifest = Path.Combine(apps, $"appmanifest_{appId}.acf");
                if (File.Exists(manifest))
                {
                    var acf = ParseVdf(File.ReadAllText(manifest));
                    if (acf.TryGetValue("AppState", out var st) && st is Dictionary<string, object> state &&
                        state.TryGetValue("installdir", out var dir) && dir is string d)
                    {
                        var full = Path.Combine(apps, "common", d);
                        if (Directory.Exists(full)) return full;
                    }
                }
                var guess = Path.Combine(apps, "common", fallbackDirName);
                if (Directory.Exists(guess)) return guess;
            }
        }
        return null;
    }
}
