# Design notes

## The mashup in one sentence

The Persona 4 Investigation Team invades Vincent's nightmares in Catherine Classic. They're allies on the landings, Persona powers mid-climb and Shadow bosses, and every portrait, voice and song comes from the player's own Persona 4 Golden.

## Decisions from the design interview

| Question | Answer |
|---|---|
| Which game the player is in | Catherine Classic |
| What P4G brings | The cast's portraits and voice barks, its music, its Shadows and its Personas, as powers |
| The Team's role | All three: landing allies, Persona powers mid-climb, Shadow bosses |
| First minute | Midnight Channel intro: TV static, Yu greets Vincent and hands over Izanagi |
| Players | Solo story. Colosseum versus with P4 picks is planned for v0.2 |
| First playable version | Night 1 complete: intro, landing with the Team, two powers, Shadow Yosuke boss |

## Route

This is a content port (universal-modder's "Pattern 1"), with the guest's assets read at run time:

- **The host is Catherine Classic** (Gamebryo, DirectX 9, loose files under `data/`). Mod loader: Reloaded-II, which the Catherine modding scene already uses.
- **The guest is Persona 4 Golden PC.** Its files are read in place from the 64-bit release's CRI archives: `data.cpk` holds the portraits (`bustup/*.bin`, CRILAYLA-compressed) and the ADX2 voice and music archives (`sound/adx2/...`). P4G never runs.
- **No code inside `Catherine.exe` is hooked or patched.** Every hook is a public Windows or DirectX entry point (`sheets/hooks.json`), so the mod doesn't depend on Catherine's exe version. Knowing where Vincent is comes from which loose files Catherine opens (`sheets/triggers.json`). Powers work through the game clock and the controller, not through Catherine's internal puzzle state.

## What each P4G piece becomes

| From P4G | In Catherine |
|---|---|
| Bustup portraits (`bustup/bN_E_F.bin` in `data.cpk`) | Dialogue portraits, the Team TV menu, Persona cut-ins, the boss's dialogue box |
| Voice clips (`sound/adx2/en/btlmem.awb`) | A bark played with each line and with each Persona cast |
| *Backside of the TV* | The Midnight Channel intro |
| *I'll Face Myself -Battle-* | The Shadow boss climb. Catherine plays it from its own file if that file is a PCM WAV, otherwise the mod plays it over the scene |
| Izanagi's Sukunda, Jiraiya's Garu | Clock slow-down; auto-pull of three blocks |
| Shadow Yosuke | Night 1 boss: name plate, lines and music. The Immoral Beast's 3D model stays Catherine's own in v0.1 |

## Contract between the director and the game side

- `NightDirector` (Core) is pure logic. Its inputs are file paths Catherine opens, hotkey presses, "Garu finished" and a real-time clock in seconds. Its outputs are calls on `IPresentation`.
- The mod calls the director only on the render thread, once per `Present`. Hook bodies run on game threads and only enqueue events (`ConcurrentQueue`) or read volatile flags.
- **Time:** the director runs on real time (`GetTickCount64`, never hooked). The game sees scaled time through `QueryPerformanceCounter` and `timeGetTime`, which stays continuous and monotonic (`TimeScaler`).
- **Input:**
  - While a modal conversation or the Team menu is open, the game gets no input: XInput is zeroed, DirectInput keyboard state is cleared, key messages are swallowed and `GetAsyncKeyState` reads as up.
  - Mod hotkeys are read before that filter: the raw pad state in the XInput hook, and the original `GetAsyncKeyState`.
  - The pad modifier (Back) is held back from the game while it's down. Released on its own, it reaches the game as a short tap.
- **Garu:** a timeline in game milliseconds (`AutoPullMacro`): grab held, back tapped once per pull. It's driven through XInput for pad players and `SendInput` scan codes for keyboard players.

## Oracles (how we know it works)

1. **Done here:** `python3 tools/preflight.py` (0 errors) and `tools/test_sheets.py` (6 tests). `dotnet test` covers 40 tests: format round-trips, the director scenario tests and a synthetic P4G install.
2. **Needs both games:** the `recon` output fills the 37 unverified cells.
3. **Needs both games:** in-game checks, each with a log line:
   - The intro plays when Night 1 loads.
   - The T menu opens on the landing.
   - Sukunda's clock change shows in the log (`game clock x0.4`) and visibly slows the tower.
   - Garu pulls three blocks.
   - The boss plate and music appear on the boss climb.
   - Clearing the boss plays Yosuke's line.

## Backlog

- v0.2: Colosseum. P4 character "skins" as overlay portraits for P1 and P2, and powers in versus.
- Nights 2+: more Shadows (Kanji, Rise, Mitsuo...), the rest of the Team, more Personas.
- If `recon catherine` shows ADX/OGG music, add an encoder so Catherine plays P4G tracks natively.
- A real Shadow model needs a P4G RMD to Gamebryo NIF converter run on the player's files. That's out of scope for now.
