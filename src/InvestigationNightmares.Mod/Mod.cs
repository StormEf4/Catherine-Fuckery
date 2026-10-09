using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using InvestigationNightmares.Audio;
using InvestigationNightmares.Content;
using InvestigationNightmares.Generated;
using InvestigationNightmares.Mod.Overlay;
using InvestigationNightmares.Story;
using InvestigationNightmares.Util;
using Microsoft.Win32;
using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;

namespace InvestigationNightmares.Mod;

/// <summary>
/// Investigation Team Nightmares: a Reloaded-II mod for Catherine Classic that brings Persona 4 Golden's
/// Investigation Team (portraits, voices and music read from the player's own P4G install) into Vincent's
/// nightmares.
/// </summary>
public sealed class Mod : IMod
{
    ModLog _log = null!;
    GameHooks? _hooks;
    NightDirector? _director;
    OverlayRenderer? _renderer;
    readonly OverlayState _state = new();
    volatile P4GContent? _content;
    AudioPlayer? _audio;
    Presentation? _presentation;

    public void StartEx(IModLoaderV1 loaderApi, IModConfigV1 modConfig)
    {
        var loader = (IModLoader)loaderApi;
        _log = new ModLog(loader.GetLogger() as ILogger);
        try { Start(loader); }
        catch (Exception e) { _log.Error($"failed to start: {e}"); }
    }

    void Start(IModLoader loader)
    {
        if (!loader.GetController<IReloadedHooks>().TryGetTarget(out var reloadedHooks))
        {
            _log.Error("Reloaded hooks (reloaded.sharedlib.hooks) are missing; enable it in Reloaded-II.");
            return;
        }
        var settings = ModSettings.Load();
        var store = new FileProgressStore(reset: settings.ReplayIntro);
        if (settings.ReplayIntro) { settings.ReplayIntro = false; settings.Save(); }

        string gameRoot = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName) ?? Environment.CurrentDirectory;
        _log.Info($"Catherine Classic at {gameRoot}");

        string? p4g = FindP4G(settings);
        _log.Info(p4g != null ? $"Persona 4 Golden at {p4g}" : "Persona 4 Golden not found (set P4GPath in settings.json if it isn't a Steam install)");

        _hooks = new GameHooks(reloadedHooks, _log, gameRoot, settings.LogFileOpens);
        _audio = new AudioPlayer(_log);
        _renderer = new OverlayRenderer(() => _content);
        _presentation = new Presentation(_state, _audio, _hooks, () => _content, _log, () => GameHooks.RealSeconds);
        _director = new NightDirector(_presentation, store, p4gAvailable: p4g != null);

        _hooks.OnFrame = OnFrame;
        _hooks.OnDeviceReset = () => { };
        _hooks.InstallEarly();

        if (p4g != null)
            Task.Run(() =>
            {
                try
                {
                    var c = P4GContent.Open(p4g);
                    c.Preload();
                    PrepareMusicRedirects(c, gameRoot);
                    _content = c;
                    _log.Info($"P4G content ready: {c.PackCount} archives, {c.Banks.Count()} wave banks");
                    foreach (var p in c.Problems.Distinct()) _log.Warn("P4G: " + p);
                }
                catch (Exception e) { _log.Error($"reading P4G failed: {e.Message}"); }
            });

        foreach (var row in Sheets.GameFiles.Concat<object>(Sheets.Triggers).Concat(Sheets.Hooks))
        {
            var unverified = (string[])row.GetType().GetProperty("Unverified")!.GetValue(row)!;
            if (unverified.Length > 0)
                _log.Info($"unverified: {row.GetType().GetProperty("Id")!.GetValue(row)} ({string.Join(", ", unverified)})");
        }
    }

    static string? FindP4G(ModSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.P4GPath) && Directory.Exists(settings.P4GPath)) return settings.P4GPath;
        var row = SheetIndex.GameFiles["p4g_exe"];
        var steamRoots = new List<string>();
        foreach (var (hive, key, value) in new[]
        {
            (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
        })
        {
            try { if (hive.OpenSubKey(key)?.GetValue(value) is string s) steamRoots.Add(s.Replace('/', '\\')); } catch { }
        }
        steamRoots.Add(@"C:\Program Files (x86)\Steam");
        return SteamLibraries.FindApp(steamRoots.Distinct(StringComparer.OrdinalIgnoreCase), row.SteamAppid, row.InstallDirName);
    }

    /// <summary>
    /// For music rows that name a Catherine track file: convert the P4G track to that file's own WAV layout
    /// and redirect Catherine to it, so the game plays it with its own volume settings and loops.
    /// </summary>
    void PrepareMusicRedirects(P4GContent c, string gameRoot)
    {
        foreach (var m in Sheets.Music.Where(m => m.ReplacesCcFile != null))
        {
            var original = Path.Combine(gameRoot, m.ReplacesCcFile!);
            try
            {
                if (!File.Exists(original)) { _log.Warn($"music[{m.Id}].replaces_cc_file: {m.ReplacesCcFile} not in Catherine's folder"); continue; }
                var head = new byte[64];
                using (var f = File.OpenRead(original)) f.ReadExactly(head, 0, (int)Math.Min(64, f.Length));
                var probe = Pcm16.ProbeWav(head);
                if (probe == null) { _log.Warn($"music[{m.Id}]: {m.ReplacesCcFile} isn't a PCM WAV (starts {Convert.ToHexString(head, 0, 8)}); the mod plays the track itself instead"); continue; }
                var pcm = c.Music(m);
                if (pcm == null) continue;
                var outDir = Path.Combine(ModLog.DataDir, "redirect");
                Directory.CreateDirectory(outDir);
                var outPath = Path.Combine(outDir, m.Id + ".wav");
                File.WriteAllBytes(outPath, pcm.Convert(probe.Value.rate, probe.Value.channels).ToWav());
                _hooks!.Redirect(m.ReplacesCcFile!, outPath);
                _presentation!.RedirectedMusic.Add(m.Id);
            }
            catch (Exception e) { _log.Warn($"music[{m.Id}] redirect: {e.Message}"); }
        }
    }

    void OnFrame(nint device)
    {
        var hooks = _hooks!;
        var director = _director!;
        double now = GameHooks.RealSeconds;

        while (hooks.FileOpens.TryDequeue(out var path)) director.OnFileOpened(path, now);
        hooks.PollKeyboard(D3D9.FocusWindow(device));
        while (hooks.Hotkeys.TryDequeue(out var key))
        {
            switch (key)
            {
                case "mod_talk": director.OnTalk(now); break;
                case "mod_power_1": director.OnPower("sukunda", now); break;
                case "mod_power_2": director.OnPower("garu", now); break;
                case "mod_advance": director.OnAdvance(now); break;
                case "mod_menu_up": director.OnMenuMove(-1); break;
                case "mod_menu_down": director.OnMenuMove(+1); break;
                case "mod_cancel": director.OnMenuCancel(now); break;
            }
        }
        while (hooks.MacroFinished.TryDequeue(out _)) director.OnAutoPullFinished(now);
        director.Update(now);

        var renderer = _renderer!;
        renderer.OnDevice(device);
        DrawOverlay(device, renderer, now);
    }

    void DrawOverlay(nint dev, OverlayRenderer renderer, double now)
    {
        nint oldRt = D3D9.GetRenderTarget(dev), backBuffer = D3D9.GetBackBuffer(dev);
        try
        {
            if (backBuffer != 0 && backBuffer != oldRt) D3D9.SetRenderTarget(dev, backBuffer);
            if (D3D9.BeginScene(dev) < 0) return;
            nint sb = D3D9.CreateStateBlock(dev);
            try
            {
                D3D9.Setup2D(dev);
                renderer.Draw(dev, _state, now);
            }
            finally
            {
                if (sb != 0) { D3D9.ApplyStateBlock(sb); D3D9.Release(sb); }
                D3D9.EndScene(dev);
            }
        }
        finally
        {
            if (backBuffer != 0 && backBuffer != oldRt && oldRt != 0) D3D9.SetRenderTarget(dev, oldRt);
            D3D9.Release(oldRt);
            D3D9.Release(backBuffer);
        }
    }

    // Reloaded-II lifecycle: this mod hooks process-wide functions, so it can't be unloaded or suspended live.
    public void Start(IModLoaderV1 loader) { }
    public void Suspend() { }
    public void Resume() { }
    public void Unload() { }
    public bool CanUnload() => false;
    public bool CanSuspend() => false;
    public Action Disposing { get; } = () => { };
}
