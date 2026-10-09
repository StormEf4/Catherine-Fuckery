using System;
using System.Collections.Generic;
using System.Linq;
using InvestigationNightmares.Generated;

namespace InvestigationNightmares.Story;

/// <summary>
/// Runs the Investigation Team's part of Catherine's nights, entirely from the sheets:
/// the Midnight Channel intro, Team conversations on landings, Persona power charges and use,
/// and the Shadow boss. It only sees which files Catherine opens, the player's hotkeys and a clock;
/// everything it wants on screen goes through <see cref="IPresentation"/>.
/// </summary>
public sealed class NightDirector
{
    public enum Mode { Free, Script, TeamMenu }

    sealed record Step(string Kind, DialogueRow? Line, PowersRow? Power, MusicRow? Music, double Duration, int GrantCharges);

    readonly IPresentation _p;
    readonly IProgressStore _store;
    readonly bool _p4gAvailable;
    bool _warnedMissing;

    readonly Queue<Step> _script = new();
    double _waitUntil = double.NaN;
    bool _waitingForAdvance;

    readonly HashSet<string> _unlocked = new();
    readonly Dictionary<string, int> _charges = new();
    readonly Dictionary<string, int> _bonus = new();
    readonly HashSet<string> _talkedThisVisit = new();

    double _sukundaUntil = double.NaN;
    double _sukundaScale = 1;
    PowersRow? _autoPull;
    double _autoPullTimeout = double.NaN;
    double _lastScale = 1;

    BossesRow? _boss;
    double _nextTaunt;
    int _tauntIndex;
    double _climbLineHideAt = double.NaN;

    int _menuSelected;
    List<LandingsRow> _menuMembers = new();

    public Mode CurrentMode { get; private set; } = Mode.Free;
    public TriggersRow? Area { get; private set; }

    public NightDirector(IPresentation presentation, IProgressStore store, bool p4gAvailable)
    {
        _p = presentation;
        _store = store;
        _p4gAvailable = p4gAvailable;
    }

    public int Charges(string powerId) => _charges.GetValueOrDefault(powerId);
    public bool IsUnlocked(string powerId) => _unlocked.Contains(powerId);
    bool InClimb => Area is { Kind: "stage" or "boss_stage" };

    // ---------------------------------------------------------------- events from the game

    public void OnFileOpened(string path, double now)
    {
        var t = SheetIndex.MatchTrigger(path);
        if (t == null || t.Id == Area?.Id) return;
        EnterArea(t, now);
    }

    public void EnterArea(TriggersRow t, double now)
    {
        if (!_p4gAvailable)
        {
            if (!_warnedMissing)
            {
                _warnedMissing = true;
                _p.ShowToast("Persona 4 Golden wasn't found on this PC, so the Investigation Team can't reach Vincent's nightmares. Install P4G through Steam and restart.", 10);
            }
            return;
        }
        var previous = Area;
        Area = t;
        _talkedThisVisit.Clear();
        EndAutoPull(now);
        _sukundaUntil = double.NaN;

        if (previous?.Kind == "boss_stage" && t.Kind != "boss_clear" && t.Kind != "boss_stage") LeaveBoss();

        switch (t.Kind)
        {
            case "stage":
            case "boss_stage":
                foreach (var id in _unlocked)
                {
                    _charges[id] = SheetIndex.Powers[id].ChargesPerStage + _bonus.GetValueOrDefault(id);
                    _bonus[id] = 0;
                }
                if (t.Kind == "boss_stage") StartBoss(t, now);
                break;
            case "landing":
                if (Sheets.Landings.Any(l => l.Landing == t.Id))
                {
                    var talk = SheetIndex.Bindings["mod_talk"];
                    _p.ShowToast($"The Investigation Team is on this landing. Press {KeyName(talk.Vk)} or {talk.XinputButtons.Replace("+", " + ")} to talk.", 6);
                }
                break;
            case "boss_clear":
                // Story events load all the time; only the first one right after the boss climb counts.
                if (previous?.Kind == "boss_stage") ClearBoss(now);
                break;
        }

        var intro = Sheets.Intro.Where(i => i.Trigger == t.Id).OrderBy(i => i.Order).ToList();
        string introKey = "intro_seen:" + t.Id;
        if (intro.Count > 0 && !_store.Get(introKey))
        {
            _store.Set(introKey);
            foreach (var i in intro)
                Enqueue(new Step(i.Kind, i.Line == null ? null : SheetIndex.Dialogue[i.Line], i.Power == null ? null : SheetIndex.Powers[i.Power],
                    i.Music == null ? null : SheetIndex.Music[i.Music], i.DurationS, 0));
        }
        RefreshHud();
        RefreshScale();
        Pump(now);
    }

    public void OnAdvance(double now)
    {
        if (CurrentMode == Mode.TeamMenu) { OpenConversation(now); return; }
        if (CurrentMode != Mode.Script || !_waitingForAdvance) return;
        _waitingForAdvance = false;
        _p.HideLine();
        Pump(now);
    }

    public void OnTalk(double now)
    {
        if (CurrentMode != Mode.Free || Area?.Kind != "landing") return;
        _menuMembers = Sheets.Landings.Where(l => l.Landing == Area.Id).ToList();
        if (_menuMembers.Count == 0) return;
        CurrentMode = Mode.TeamMenu;
        _menuSelected = Math.Clamp(_menuSelected, 0, _menuMembers.Count - 1);
        _p.SetInputBlocked(true);
        _p.ShowTeamMenu(_menuMembers.Select(m => SheetIndex.Characters[m.Character]).ToList(), _menuSelected);
        RefreshScale();
    }

    public void OnMenuMove(int delta)
    {
        if (CurrentMode != Mode.TeamMenu) return;
        _menuSelected = (_menuSelected + delta + _menuMembers.Count) % _menuMembers.Count;
        _p.ShowTeamMenu(_menuMembers.Select(m => SheetIndex.Characters[m.Character]).ToList(), _menuSelected);
    }

    public void OnMenuCancel(double now)
    {
        if (CurrentMode != Mode.TeamMenu) return;
        _p.HideTeamMenu();
        CurrentMode = Mode.Free;
        _p.SetInputBlocked(false);
        RefreshScale();
    }

    public void OnPower(string powerId, double now)
    {
        if (CurrentMode != Mode.Free || !InClimb || !SheetIndex.Powers.TryGetValue(powerId, out var power)) return;
        if (!_unlocked.Contains(powerId)) return;
        if (_autoPull != null) return;
        if (Charges(powerId) <= 0)
        {
            _p.ShowToast($"{power.Skill} is spent for this climb.", 2);
            return;
        }
        _charges[powerId]--;
        var owner = SheetIndex.Characters[power.Owner];
        _p.ShowCutIn(power, owner);
        var castLine = SheetIndex.Dialogue[power.CastLine];
        if (castLine.BarkIndex is int bark) _p.PlayBark(owner, bark);
        switch (power.Effect)
        {
            case "time_scale":
                _sukundaUntil = now + power.DurationS;
                _sukundaScale = power.Param;
                break;
            case "auto_pull":
                _autoPull = power;
                // Generous real-time timeout in case the game never reports back.
                _autoPullTimeout = now + power.Param * power.PullStepMs / 1000.0 / power.TimeScaleDuring + 3;
                _p.StartAutoPull(power);
                break;
        }
        RefreshHud();
        RefreshScale();
    }

    public void OnAutoPullFinished(double now) => EndAutoPull(now);

    public void Update(double now)
    {
        if (!double.IsNaN(_sukundaUntil) && now >= _sukundaUntil) { _sukundaUntil = double.NaN; RefreshScale(); }
        if (_autoPull != null && now >= _autoPullTimeout) EndAutoPull(now);
        if (!double.IsNaN(_climbLineHideAt) && now >= _climbLineHideAt && CurrentMode == Mode.Free)
        {
            _climbLineHideAt = double.NaN;
            _p.HideLine();
        }
        if (_boss != null && Area?.Kind == "boss_stage" && now >= _nextTaunt && CurrentMode == Mode.Free)
        {
            var id = _boss.Taunts[_tauntIndex++ % _boss.Taunts.Length];
            SayWhileClimbing(SheetIndex.Dialogue[id], now);
            _nextTaunt = now + _boss.TauntIntervalS;
        }
        if (CurrentMode == Mode.Script && !_waitingForAdvance && !double.IsNaN(_waitUntil) && now >= _waitUntil)
        {
            _waitUntil = double.NaN;
            Pump(now);
        }
    }

    // ---------------------------------------------------------------- internals

    void Enqueue(Step s)
    {
        _script.Enqueue(s);
        if (CurrentMode == Mode.Free)
        {
            CurrentMode = Mode.Script;
            _climbLineHideAt = double.NaN;
            _p.HideLine();
            _p.SetInputBlocked(true);
        }
    }

    void Pump(double now)
    {
        if (CurrentMode != Mode.Script) return;
        while (_script.Count > 0)
        {
            var s = _script.Dequeue();
            switch (s.Kind)
            {
                case "tv_static_in":
                case "tv_static_out":
                    _p.TvStatic(s.Kind == "tv_static_in", s.Duration);
                    _waitUntil = now + s.Duration;
                    return;
                case "music":
                    _p.PlayMusic(s.Music!);
                    break;
                case "line":
                    {
                        var speaker = SheetIndex.Characters[s.Line!.Speaker];
                        _p.ShowLine(s.Line, speaker, modal: true);
                        if (s.Line.BarkIndex is int bark) _p.PlayBark(speaker, bark);
                        _waitingForAdvance = true;
                        return;
                    }
                case "grant_power":
                    Grant(s.Power!, s.GrantCharges);
                    break;
                case "return_to_menu":
                    CurrentMode = Mode.TeamMenu;
                    _p.ShowTeamMenu(_menuMembers.Select(m => SheetIndex.Characters[m.Character]).ToList(), _menuSelected);
                    RefreshScale();
                    return;
            }
        }
        CurrentMode = Mode.Free;
        _p.SetInputBlocked(false);
        RefreshScale();
    }

    void OpenConversation(double now)
    {
        var member = _menuMembers[_menuSelected];
        _p.HideTeamMenu();
        CurrentMode = Mode.Free; // Enqueue switches to Script
        foreach (var lineId in member.Lines)
            Enqueue(new Step("line", SheetIndex.Dialogue[lineId], null, null, 0, 0));
        if (member.GrantsPower != null && _talkedThisVisit.Add(member.Id))
            Enqueue(new Step("grant_power", null, SheetIndex.Powers[member.GrantsPower], null, 0, member.GrantCharges));
        Enqueue(new Step("return_to_menu", null, null, null, 0, 0));
        Pump(now);
    }

    void Grant(PowersRow power, int bonusCharges)
    {
        bool isNew = _unlocked.Add(power.Id);
        if (isNew && InClimb) _charges[power.Id] = power.ChargesPerStage;
        _bonus[power.Id] = _bonus.GetValueOrDefault(power.Id) + bonusCharges;
        var owner = SheetIndex.Characters[power.Owner];
        var key = SheetIndex.Bindings[power.Hotkey];
        string extra = bonusCharges > 0 ? $" (+{bonusCharges} for your next climb)" : "";
        _p.ShowToast(isNew
            ? $"{owner.Persona}'s {power.Skill} joined you{extra}. Use it with {KeyName(key.Vk)} or {key.XinputButtons.Replace("+", " + ")}."
            : $"{owner.Persona}'s {power.Skill}{extra}.", 5);
        RefreshHud();
    }

    void StartBoss(TriggersRow stage, double now)
    {
        _boss = Sheets.Bosses.FirstOrDefault(b => b.Stage == stage.Id);
        if (_boss == null) return;
        var shadow = SheetIndex.Characters[_boss.Shadow];
        _p.ShowBossPlate(shadow);
        _p.PlayMusic(SheetIndex.Music[_boss.Music]);
        SayWhileClimbing(SheetIndex.Dialogue[_boss.IntroLine], now);
        _tauntIndex = 0;
        _nextTaunt = now + _boss.TauntIntervalS;
    }

    void LeaveBoss()
    {
        if (_boss == null) return;
        _p.ShowBossPlate(null);
        _p.StopMusic();
        _boss = null;
    }

    void ClearBoss(double now)
    {
        var boss = Sheets.Bosses.FirstOrDefault(b => b.Clear == Area!.Id);
        if (boss == null) return;
        _p.ShowBossPlate(null);
        _p.StopMusic();
        _boss = null;
        if (!_store.Get("boss_cleared:" + boss.Id))
        {
            _store.Set("boss_cleared:" + boss.Id);
            Enqueue(new Step("line", SheetIndex.Dialogue[boss.DefeatLine], null, null, 0, 0));
        }
    }

    void SayWhileClimbing(DialogueRow line, double now)
    {
        var speaker = SheetIndex.Characters[line.Speaker];
        _p.ShowLine(line, speaker, modal: false);
        if (line.BarkIndex is int bark) _p.PlayBark(speaker, bark);
        _climbLineHideAt = now + SheetIndex.Tuning("climb_line_seconds");
    }

    void EndAutoPull(double now)
    {
        if (_autoPull == null) return;
        _autoPull = null;
        _autoPullTimeout = double.NaN;
        RefreshScale();
    }

    void RefreshHud()
    {
        var list = Sheets.Powers.Where(p => _unlocked.Contains(p.Id)).Select(p => (p, Charges(p.Id))).ToList();
        _p.ShowPowerHud(InClimb ? list : new List<(PowersRow, int)>());
    }

    void RefreshScale()
    {
        double s = _autoPull != null ? _autoPull.TimeScaleDuring
                 : !double.IsNaN(_sukundaUntil) ? _sukundaScale
                 : CurrentMode != Mode.Free ? SheetIndex.Tuning("modal_time_scale")
                 : 1.0;
        if (s != _lastScale)
        {
            _lastScale = s;
            _p.SetTimeScale(s);
        }
    }

    static string KeyName(string vk) => vk.StartsWith("VK_") ? vk[3..] : vk;
}
