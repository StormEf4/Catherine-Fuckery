using System.Collections.Generic;
using InvestigationNightmares.Generated;

namespace InvestigationNightmares.Story;

/// <summary>What the director asks the game side to show, play or change. The mod implements it with the
/// D3D9 overlay, its audio player and its hooks; tests implement it with a recorder.</summary>
public interface IPresentation
{
    void ShowLine(DialogueRow line, CharactersRow speaker, bool modal);
    void HideLine();
    void ShowTeamMenu(IReadOnlyList<CharactersRow> members, int selected);
    void HideTeamMenu();
    void TvStatic(bool fadeIn, double seconds);
    void PlayMusic(MusicRow music);
    void StopMusic();
    void PlayBark(CharactersRow speaker, int waveIndex);
    void ShowCutIn(PowersRow power, CharactersRow owner);
    void ShowBossPlate(CharactersRow? shadow);
    void ShowToast(string text, double seconds);
    void ShowPowerHud(IReadOnlyList<(PowersRow power, int charges)> powers);
    void SetTimeScale(double scale);
    void StartAutoPull(PowersRow power);
    void SetInputBlocked(bool blocked);
}

/// <summary>Small persistent flags (intro seen, etc.).</summary>
public interface IProgressStore
{
    bool Get(string key);
    void Set(string key);
}

public sealed class MemoryProgressStore : IProgressStore
{
    readonly HashSet<string> _keys = new();
    public bool Get(string key) => _keys.Contains(key);
    public void Set(string key) => _keys.Add(key);
}
