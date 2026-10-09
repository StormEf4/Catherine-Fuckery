using System;
using System.Collections.Generic;
using System.Linq;
using InvestigationNightmares.Content;
using InvestigationNightmares.Generated;
using InvestigationNightmares.Mod.Overlay;
using InvestigationNightmares.Powers;
using InvestigationNightmares.Story;

namespace InvestigationNightmares.Mod;

/// <summary>The director's requests, carried out with the overlay, the audio player and the hooks. Render thread only.</summary>
sealed class Presentation : IPresentation
{
    readonly OverlayState _state;
    readonly AudioPlayer _audio;
    readonly GameHooks _hooks;
    readonly Func<P4GContent?> _content;
    readonly ModLog _log;
    readonly Func<double> _now;

    /// <summary>Music rows Catherine plays itself because their track file is redirected.</summary>
    public HashSet<string> RedirectedMusic { get; } = new();

    public Presentation(OverlayState state, AudioPlayer audio, GameHooks hooks, Func<P4GContent?> content, ModLog log, Func<double> now)
    {
        _state = state; _audio = audio; _hooks = hooks; _content = content; _log = log; _now = now;
    }

    public void ShowLine(DialogueRow line, CharactersRow speaker, bool modal) => _state.Line = (line, speaker, modal);
    public void HideLine() => _state.Line = null;
    public void ShowTeamMenu(IReadOnlyList<CharactersRow> members, int selected) => _state.Menu = (members.ToList(), selected);
    public void HideTeamMenu() => _state.Menu = null;
    public void TvStatic(bool fadeIn, double seconds) => _state.Static = (fadeIn, _now(), seconds);
    public void ShowCutIn(PowersRow power, CharactersRow owner) => _state.CutIn = (power, owner, _now());
    public void ShowBossPlate(CharactersRow? shadow) => _state.BossPlate = shadow;
    public void ShowPowerHud(IReadOnlyList<(PowersRow power, int charges)> powers) => _state.Hud = powers.ToList();

    public void ShowToast(string text, double seconds)
    {
        _state.Toasts.Add((text, _now() + seconds));
        _log.Info($"toast: {text}");
    }

    public void PlayMusic(MusicRow music)
    {
        if (RedirectedMusic.Contains(music.Id)) return; // Catherine is playing it from its own (redirected) file
        var pcm = _content()?.Music(music);
        if (pcm == null) { _log.Warn($"music {music.Id} unavailable (see earlier problems)"); return; }
        _audio.PlayMusic(pcm, music.Volume, music.Loop);
    }

    public void StopMusic() => _audio.StopMusic();

    public void PlayBark(CharactersRow speaker, int waveIndex)
    {
        var pcm = _content()?.Bark(speaker, waveIndex);
        if (pcm != null) _audio.PlayBark(pcm, SheetIndex.Tuning("bark_volume"));
    }

    public void SetTimeScale(double scale) => _hooks.SetTimeScale(scale);

    public void StartAutoPull(PowersRow power)
    {
        _hooks.SetTimeScale(power.TimeScaleDuring);
        _hooks.StartMacro(new AutoPullMacro((int)power.Param, power.PullStepMs), power.TimeScaleDuring);
    }

    public void SetInputBlocked(bool blocked) => _hooks.InputBlocked = blocked;
}
