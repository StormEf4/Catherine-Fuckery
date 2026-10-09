# Investigation Team Nightmares

*Persona 4 Golden × Catherine Classic*

Vincent is climbing the towers of his nightmares when the Midnight Channel crackles on. Yu Narukami steps out of the static: "You're not just dreaming. This is the other side of the TV." The Investigation Team has found a way in.

You play **Catherine Classic**. The Investigation Team comes in from **your own Persona 4 Golden install**. Their portraits, voices and music are read from P4G's files while Catherine runs, and nothing from either game ships with this mod.

## What's in version 0.1 (Night 1)

- **Midnight Channel intro.** When Night 1 begins, the screen dissolves into TV static to *Backside of the TV*. Yu greets Vincent, Teddie pops in, and Yu hands over Izanagi's power.
- **Persona powers mid-climb:**
  - **Izanagi · Sukunda** (`1`, or Back + LB): the nightmare slows to 40% for 8 seconds so you can think.
  - **Jiraiya · Garu** (`2`, or Back + RB): a gust pulls the block in front of you out three times in a blink.
  - Each power refills once per climb, and a Persona cut-in banner plays when you use one.
- **The Team on the landing.** Press `T` (or Back + Y) to open the Team's TV and talk to Yosuke, Chie or Teddie. Yosuke hands you Jiraiya's Garu, and Teddie gives you an extra Sukunda for the next climb.
- **A Shadow boss.** Night 1's boss climb becomes **Shadow Yosuke**: his name plate, his "I am a shadow... the true self", taunts while you climb, and *I'll Face Myself -Battle-*. When you clear the night, Yosuke takes it back.

Colosseum versus with P4 character picks and Persona powers is planned for version 0.2. Later nights and the rest of the Team come after that.

## What you need

- **Catherine Classic** (Steam). This is the game you play.
- **Persona 4 Golden** (Steam, PC release), installed on the same PC. The mashup reads the Team's portraits, voices and music from it. If it isn't installed, Catherine runs normally and tells you why the Team didn't show up.
- **Reloaded-II** (the mod loader), with its **reloaded.sharedlib.hooks** mod enabled.

## Install

1. Install Reloaded-II and add `Catherine.exe` to it as an application.
2. Drag `InvestigationTeamNightmares-<version>-reloaded.zip` onto Reloaded-II, or unzip it into Reloaded-II's `Mods` folder.
3. Enable **Investigation Team Nightmares** for Catherine and launch the game from Reloaded-II.

If Persona 4 Golden isn't a Steam install, set `P4GPath` in `%LOCALAPPDATA%\InvestigationNightmares\settings.json`. To see the intro again, set `ReplayIntro` to `true` in the same file.

## Status: built and unit-tested, not yet run in the game

This version was built and tested on Linux, without either game. What that covers:

- **Verified by tests:** the readers for P4G's archives, portraits, sound banks and audio formats; the Steam library finder; the whole Night 1 story flow (intro, landing talks, power charges, the boss and its taunts); the clock scaling; Garu's pull timing; and the controller hotkey handling. That is 40 C# tests plus 6 sheet tests. Content loading was also tested end to end against a synthetic P4G folder laid out the way the sheets describe.
- **Not yet verified:** 37 sheet cells (run `python3 tools/preflight.py` to list them). They cover which P4G files hold each portrait, voice clip and song, which Catherine file opens for each stage, Catherine's grab button, and how Catherine reads its clock and controller. Until they're checked on a real install, expect things like missing portraits or a trigger that doesn't fire.

The mod writes a log to `%LOCALAPPDATA%\InvestigationNightmares\log.txt`. Every problem names the sheet cell to fix, and the log also lists each file Catherine opens, which is how the stage triggers get filled in.

### Filling in the unverified cells

On a PC with both games installed:

```
recon p4g          # lists P4G's archives, exports the portraits as PNGs, lists every sound bank entry with its length
recon p4g --wav SND/ROOT.xwb 0 200   # export clips to listen for the right barks
recon catherine    # lists Catherine's data files and their formats (shows what format the music is in)
recon sheets       # what's still unverified
```

Then play into Night 1 with the mod enabled and read the log. Edit the sheets, run `./build.sh`, and play again.

Everything `recon` writes goes to `recon-out/`. It's game content for you to look at, so it's git-ignored and must never be committed or shared.

## How it's built

- `sheets/*.json` is the design and the source of truth: characters, dialogue, Persona powers, landings, the boss, music, stage triggers, controls, every hook and the tuning numbers. Each row becomes one C# record (`tools/gen_code.py`). `tools/preflight.py` cross-checks every cell and reference before each build.
- `src/InvestigationNightmares.Core`: P4G readers (DW_PACK + Huffman, Atlus PAK, TMX, DDS, XACT wave banks, MS-ADPCM), the Steam finder, and the night's director (`Story/NightDirector.cs`). It runs on any OS and is fully unit-tested.
- `src/InvestigationNightmares.Mod`: the Reloaded-II mod. It hooks Direct3D 9 `Present` and `Reset` (overlay), `CreateFile` (stage triggers and music swap), `QueryPerformanceCounter` and `timeGetTime` (Sukunda and Garu), and XInput, DirectInput and the window's key messages (hotkeys and Garu's button presses). It never touches code inside `Catherine.exe`.
- `src/InvestigationNightmares.Recon`: the `recon` tool.
- `./build.sh` runs preflight, codegen, all tests, then packages both zips into `dist/`.

See [docs/DESIGN.md](docs/DESIGN.md) for the design notes and the modding route.

## Credits

- *Persona 4 Golden* and *Catherine Classic* are © ATLUS / SEGA. This mod contains none of their files; it reads your own installs.
- The P4G PC archive and sound bank layouts were learned from [tge-was-taken/preappfile](https://github.com/tge-was-taken/preappfile) and [Sewer56/p4gpc.modloader](https://github.com/Sewer56/p4gpc.modloader). The readers here are independent implementations.
- Catherine Classic's folder layout comes from [Shasties/catherine_character_mods](https://github.com/Shasties/catherine_character_mods).
- Mod loader: [Reloaded-II](https://github.com/Reloaded-Project/Reloaded-II) by Sewer56.
