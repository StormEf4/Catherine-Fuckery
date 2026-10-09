using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using InvestigationNightmares.Story;

namespace InvestigationNightmares.Mod;

/// <summary>Player settings (settings.json next to the log) and story progress (progress.json).</summary>
sealed class ModSettings
{
    /// <summary>Persona 4 Golden folder, if Steam can't find it (another store, a copied install).</summary>
    public string? P4GPath { get; set; }

    /// <summary>Write every file Catherine opens to the log. That is how the stage triggers in sheets/triggers.json get filled in.</summary>
    public bool LogFileOpens { get; set; } = true;

    /// <summary>Show the Midnight Channel intro again next time.</summary>
    public bool ReplayIntro { get; set; }

    static string PathOf => System.IO.Path.Combine(ModLog.DataDir, "settings.json");

    public static ModSettings Load()
    {
        try
        {
            if (File.Exists(PathOf)) return JsonSerializer.Deserialize<ModSettings>(File.ReadAllText(PathOf)) ?? new();
        }
        catch { }
        var s = new ModSettings();
        s.Save();
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ModLog.DataDir);
            File.WriteAllText(PathOf, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

sealed class FileProgressStore : IProgressStore
{
    readonly HashSet<string> _keys;
    readonly string _path = Path.Combine(ModLog.DataDir, "progress.json");

    public FileProgressStore(bool reset)
    {
        _keys = new();
        if (reset) return;
        try
        {
            if (File.Exists(_path)) _keys = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(_path)) ?? new();
        }
        catch { }
    }

    public bool Get(string key) => _keys.Contains(key);

    public void Set(string key)
    {
        if (!_keys.Add(key)) return;
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_keys)); } catch { }
    }
}
