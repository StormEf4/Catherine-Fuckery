using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using InvestigationNightmares.Generated;

namespace InvestigationNightmares.Story;

/// <summary>Lookups over the generated sheet rows.</summary>
public static class SheetIndex
{
    public static readonly IReadOnlyDictionary<string, CharactersRow> Characters = Sheets.Characters.ToDictionary(r => r.Id);
    public static readonly IReadOnlyDictionary<string, DialogueRow> Dialogue = Sheets.Dialogue.ToDictionary(r => r.Id);
    public static readonly IReadOnlyDictionary<string, PowersRow> Powers = Sheets.Powers.ToDictionary(r => r.Id);
    public static readonly IReadOnlyDictionary<string, MusicRow> Music = Sheets.Music.ToDictionary(r => r.Id);
    public static readonly IReadOnlyDictionary<string, TriggersRow> Triggers = Sheets.Triggers.ToDictionary(r => r.Id);
    public static readonly IReadOnlyDictionary<string, BindingsRow> Bindings = Sheets.Bindings.ToDictionary(r => r.Id);
    public static readonly IReadOnlyDictionary<string, GameFilesRow> GameFiles = Sheets.GameFiles.ToDictionary(r => r.Id);

    public static double Tuning(string id) => Sheets.Tuning.First(t => t.Id == id).Value;

    static readonly List<(TriggersRow row, Regex re)> TriggerRegexes =
        Sheets.Triggers.Select(t => (t, new Regex(t.FileRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))).ToList();

    /// <summary>The trigger whose regex matches a file path Catherine opened, if any.</summary>
    public static TriggersRow? MatchTrigger(string openedPath)
    {
        var p = openedPath.Replace('\\', '/');
        int data = p.IndexOf("/data/", StringComparison.OrdinalIgnoreCase);
        if (data >= 0) p = p[(data + 1)..];
        foreach (var (row, re) in TriggerRegexes)
            if (re.IsMatch(p)) return row;
        return null;
    }
}
