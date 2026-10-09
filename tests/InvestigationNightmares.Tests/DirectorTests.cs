using System.Collections.Generic;
using System.Linq;
using InvestigationNightmares.Generated;
using InvestigationNightmares.Input;
using InvestigationNightmares.Powers;
using InvestigationNightmares.Story;
using Xunit;

namespace InvestigationNightmares.Tests;

sealed class Recorder : IPresentation
{
    public readonly List<string> Log = new();
    public bool InputBlocked;
    public double Scale = 1;
    public string? Plate;
    public List<(string, int)> Hud = new();
    public void ShowLine(DialogueRow line, CharactersRow speaker, bool modal) => Log.Add($"line {line.Id} {(modal ? "modal" : "climb")}");
    public void HideLine() => Log.Add("hide-line");
    public void ShowTeamMenu(IReadOnlyList<CharactersRow> members, int selected) => Log.Add($"menu {string.Join(",", members.Select(m => m.Id))} sel={selected}");
    public void HideTeamMenu() => Log.Add("menu-hide");
    public void TvStatic(bool fadeIn, double seconds) => Log.Add(fadeIn ? "static-in" : "static-out");
    public void PlayMusic(MusicRow music) => Log.Add($"music {music.Id}");
    public void StopMusic() => Log.Add("music-stop");
    public void PlayBark(CharactersRow speaker, int waveIndex) => Log.Add($"bark {speaker.Id} {waveIndex}");
    public void ShowCutIn(PowersRow power, CharactersRow owner) => Log.Add($"cutin {power.Id}");
    public void ShowBossPlate(CharactersRow? shadow) { Plate = shadow?.Id; Log.Add($"plate {shadow?.Id ?? "none"}"); }
    public void ShowToast(string text, double seconds) => Log.Add($"toast {text}");
    public void ShowPowerHud(IReadOnlyList<(PowersRow power, int charges)> powers) => Hud = powers.Select(p => (p.power.Id, p.charges)).ToList();
    public void SetTimeScale(double scale) { Scale = scale; Log.Add($"scale {scale}"); }
    public void StartAutoPull(PowersRow power) => Log.Add($"autopull {power.Id}");
    public void SetInputBlocked(bool blocked) => InputBlocked = blocked;
}

public class DirectorTests
{
    static TriggersRow T(string id) => SheetIndex.Triggers[id];

    static (NightDirector d, Recorder r) New(bool p4g = true)
    {
        var r = new Recorder();
        return (new NightDirector(r, new MemoryProgressStore(), p4g), r);
    }

    static void FinishScript(NightDirector d, ref double now)
    {
        for (int guard = 0; guard < 50 && d.CurrentMode == NightDirector.Mode.Script; guard++)
        {
            now += 2;
            d.Update(now);
            d.OnAdvance(now);
        }
    }

    [Fact]
    public void Intro_plays_once_in_sheet_order_and_unlocks_sukunda()
    {
        var (d, r) = New();
        double now = 0;
        d.EnterArea(T("night1_stage1"), now);
        Assert.Equal(NightDirector.Mode.Script, d.CurrentMode);
        Assert.True(r.InputBlocked);
        Assert.Equal("static-in", r.Log.Last());

        FinishScript(d, ref now);
        Assert.Equal(NightDirector.Mode.Free, d.CurrentMode);
        Assert.False(r.InputBlocked);
        var order = r.Log.Where(l => l.StartsWith("line") || l.StartsWith("music") || l.StartsWith("static")).ToList();
        Assert.Equal(new[]
        {
            "static-in", "music bgm_backside_of_the_tv", "line intro_yu_1 modal", "line intro_yu_2 modal",
            "line intro_yu_grant modal", "line intro_teddie modal", "static-out",
        }, order);
        Assert.True(d.IsUnlocked("sukunda"));
        Assert.Equal(1, d.Charges("sukunda"));
        Assert.Contains(("sukunda", 1), r.Hud);

        // Retrying the stage (another area first, then back) doesn't replay it.
        d.EnterArea(T("night1_landing"), now);
        r.Log.Clear();
        d.EnterArea(T("night1_stage1"), now);
        Assert.DoesNotContain("static-in", r.Log);
    }

    [Fact]
    public void Same_area_file_reopened_is_ignored()
    {
        var (d, r) = New();
        d.OnFileOpened("C:/Games/CatherineClassic/data/puzzle/stg_01_01.bin", 0);
        Assert.Equal("night1_stage1", d.Area?.Id);
        int before = r.Log.Count;
        d.OnFileOpened(@"C:\Games\CatherineClassic\data\puzzle\stg_01_01.bin", 0.5);
        Assert.Equal(before, r.Log.Count);
    }

    [Fact]
    public void Sukunda_slows_the_clock_for_its_duration_and_spends_a_charge()
    {
        var (d, r) = New();
        double now = 0;
        d.EnterArea(T("night1_stage1"), now);
        FinishScript(d, ref now);
        d.OnPower("sukunda", now);
        Assert.Equal(0.4, r.Scale);
        Assert.Equal(0, d.Charges("sukunda"));
        Assert.Contains("cutin sukunda", r.Log);
        d.Update(now + 7.9);
        Assert.Equal(0.4, r.Scale);
        d.Update(now + 8.1);
        Assert.Equal(1.0, r.Scale);
        d.OnPower("sukunda", now + 9);
        Assert.Contains(r.Log, l => l.Contains("spent for this climb"));
    }

    [Fact]
    public void Powers_do_nothing_on_landings_or_before_unlock()
    {
        var (d, r) = New();
        d.EnterArea(T("night1_landing"), 0);
        d.OnPower("sukunda", 1);
        d.OnPower("garu", 1);
        Assert.DoesNotContain(r.Log, l => l.StartsWith("cutin"));
    }

    [Fact]
    public void Landing_conversation_grants_garu_once_and_returns_to_menu()
    {
        var (d, r) = New();
        double now = 0;
        d.EnterArea(T("night1_stage1"), now);
        FinishScript(d, ref now);
        d.EnterArea(T("night1_landing"), now);
        Assert.Contains(r.Log, l => l.StartsWith("toast The Investigation Team is on this landing"));

        d.OnTalk(now);
        Assert.Equal(NightDirector.Mode.TeamMenu, d.CurrentMode);
        Assert.True(r.InputBlocked);
        Assert.Equal("menu yosuke,chie,teddie sel=0", r.Log.Last());

        d.OnAdvance(now); // pick Yosuke
        Assert.Equal("line landing_yosuke_1 modal", r.Log.Last(l => l.StartsWith("line")));
        d.OnAdvance(now);
        Assert.Equal("line landing_yosuke_grant modal", r.Log.Last(l => l.StartsWith("line")));
        d.OnAdvance(now);
        Assert.True(d.IsUnlocked("garu"));
        Assert.Equal(NightDirector.Mode.TeamMenu, d.CurrentMode);

        // Talking again doesn't grant again.
        int toasts = r.Log.Count(l => l.StartsWith("toast Jiraiya"));
        d.OnAdvance(now); d.OnAdvance(now); d.OnAdvance(now);
        Assert.Equal(toasts, r.Log.Count(l => l.StartsWith("toast Jiraiya")));

        // Teddie adds a bonus Sukunda for the next climb.
        d.OnMenuMove(+1); d.OnMenuMove(+1);
        d.OnAdvance(now); d.OnAdvance(now);
        d.OnMenuCancel(now);
        Assert.Equal(NightDirector.Mode.Free, d.CurrentMode);
        Assert.False(r.InputBlocked);

        d.EnterArea(T("night1_boss"), now);
        Assert.Equal(2, d.Charges("sukunda"));
        Assert.Equal(1, d.Charges("garu"));
    }

    [Fact]
    public void Boss_climb_has_plate_music_taunts_and_defeat_line()
    {
        var (d, r) = New();
        double now = 0;
        d.EnterArea(T("night1_boss"), now);
        Assert.Equal("shadow_yosuke", r.Plate);
        Assert.Contains("music bgm_ill_face_myself_battle", r.Log);
        Assert.Contains("line boss_intro climb", r.Log);
        Assert.Equal(NightDirector.Mode.Free, d.CurrentMode); // never blocks the climb

        d.Update(5.5);
        Assert.Contains("hide-line", r.Log);
        d.Update(25.1);
        Assert.Contains("line boss_taunt_1 climb", r.Log);
        d.Update(50.2);
        Assert.Contains("line boss_taunt_2 climb", r.Log);

        d.EnterArea(T("night1_boss_clear"), 60);
        Assert.Null(r.Plate);
        Assert.Contains("music-stop", r.Log);
        Assert.Contains("line boss_defeat modal", r.Log);
    }

    [Fact]
    public void Garu_runs_the_pull_macro_at_its_speed_until_done()
    {
        var (d, r) = New();
        double now = 0;
        d.EnterArea(T("night1_stage1"), now);
        FinishScript(d, ref now);
        d.EnterArea(T("night1_landing"), now);
        d.OnTalk(now); d.OnAdvance(now); d.OnAdvance(now); d.OnAdvance(now); d.OnMenuCancel(now);
        d.EnterArea(T("night1_boss"), now);
        d.OnPower("garu", now);
        Assert.Contains("autopull garu", r.Log);
        Assert.Equal(2.5, r.Scale);
        d.OnPower("sukunda", now); // one power at a time
        Assert.Equal(2.5, r.Scale);
        d.OnAutoPullFinished(now + 1);
        Assert.Equal(1.0, r.Scale);
    }

    [Fact]
    public void Missing_p4g_says_so_once_and_does_nothing_else()
    {
        var (d, r) = New(p4g: false);
        d.EnterArea(T("night1_stage1"), 0);
        d.EnterArea(T("night1_boss"), 1);
        Assert.Single(r.Log);
        Assert.StartsWith("toast Persona 4 Golden wasn't found", r.Log[0]);
    }

    [Fact]
    public void Every_sheet_row_the_director_uses_resolves()
    {
        foreach (var b in Sheets.Bosses)
        {
            Assert.True(SheetIndex.Triggers.ContainsKey(b.Stage));
            Assert.All(b.Taunts, t => Assert.True(SheetIndex.Dialogue.ContainsKey(t)));
        }
        Assert.Equal(0x20, ComboTracker.ModifierOf(Sheets.Bindings.Where(b => b.Owner == "mod").Select(b => b.XinputButtons)));
        foreach (var b in Sheets.Bindings)
        {
            InputNames.ButtonMask(b.XinputButtons);
            InputNames.VirtualKey(b.Vk);
        }
    }
}

public class PowerTests
{
    [Fact]
    public void TimeScaler_is_continuous_and_monotonic()
    {
        var t = new TimeScaler();
        Assert.Equal(1000, t.ToGame(1000));
        Assert.Equal(2000, t.ToGame(2000));
        t.SetScale(0.5, 2000);
        Assert.Equal(2500, t.ToGame(3000));
        t.SetScale(2, 3000);
        Assert.Equal(4500, t.ToGame(4000));
        t.SetScale(0, 4000);
        Assert.Equal(4500, t.ToGame(9000));
        t.SetScale(1, 9000);
        Assert.Equal(4600, t.ToGame(9100));
        Assert.Equal(4600, t.ToGame(9050)); // a racing caller with an older reading never sees time go back
    }

    [Fact]
    public void AutoPull_holds_grab_and_taps_back_once_per_pull()
    {
        var m = new AutoPullMacro(3, 400);
        int taps = 0;
        bool prevBack = false, grabAlways = true;
        for (double ms = 0; ms < m.TotalMs; ms += 5)
        {
            var (grab, back, done) = m.StateAt(ms);
            Assert.False(done);
            grabAlways &= grab;
            if (back && !prevBack) taps++;
            prevBack = back;
        }
        Assert.True(grabAlways);
        Assert.Equal(3, taps);
        Assert.True(m.StateAt(m.TotalMs).done);
    }

    [Fact]
    public void Combo_hides_modifier_and_fires_once()
    {
        const ushort BACK = 0x20, LB = 0x100, A = 0x1000;
        var c = new ComboTracker(new[] { ("p1", (ushort)(BACK | LB)), ("adv", A) }, BACK);
        Assert.Equal(BACK, c.Modifier);
        var (f1, g1) = c.Poll(BACK);
        Assert.Empty(f1); Assert.Equal(0, g1);
        var (f2, g2) = c.Poll(BACK | LB);
        Assert.Equal(new[] { "p1" }, f2); Assert.Equal(0, g2);
        var (f3, g3) = c.Poll(BACK | LB);
        Assert.Empty(f3); Assert.Equal(0, g3);
        var (_, g4) = c.Poll(LB);
        Assert.Equal(0, g4); // LB stays hidden until released
        var (_, g5) = c.Poll(0);
        Assert.Equal(0, g5); // modifier was used: no tap
        Assert.Equal(LB, c.Poll(LB).forGame);
    }

    [Fact]
    public void Lone_modifier_press_reaches_the_game_as_a_tap()
    {
        const ushort BACK = 0x20, LB = 0x100;
        var c = new ComboTracker(new[] { ("p1", (ushort)(BACK | LB)) }, BACK);
        Assert.Equal(0, c.Poll(BACK).forGame);
        Assert.Equal(BACK, c.Poll(0).forGame);
        Assert.Equal(BACK, c.Poll(0).forGame);
        Assert.Equal(BACK, c.Poll(0).forGame);
        Assert.Equal(0, c.Poll(0).forGame);
    }
}
