using System;
using System.Drawing;
using System.IO;
using Reloaded.Mod.Interfaces;

namespace InvestigationNightmares.Mod;

/// <summary>Writes to Reloaded-II's console and to %LOCALAPPDATA%\InvestigationNightmares\log.txt.</summary>
sealed class ModLog
{
    readonly ILogger? _logger;
    readonly StreamWriter? _file;
    readonly object _gate = new();

    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InvestigationNightmares");

    public ModLog(ILogger? logger)
    {
        _logger = logger;
        try
        {
            Directory.CreateDirectory(DataDir);
            _file = new StreamWriter(Path.Combine(DataDir, "log.txt"), append: false) { AutoFlush = true };
        }
        catch { _file = null; }
    }

    public void Info(string msg) => Write(msg, Color.LightSkyBlue);
    public void Warn(string msg) => Write("WARN " + msg, Color.Gold);
    public void Error(string msg) => Write("ERROR " + msg, Color.OrangeRed);

    void Write(string msg, Color color)
    {
        var line = $"[InvestigationNightmares] {msg}";
        try { _logger?.WriteLineAsync(line, color); } catch { }
        lock (_gate)
        {
            try { _file?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg}"); } catch { }
        }
    }
}
