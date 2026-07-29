# Legend of Keepers — AUTO Battle

[Русский](README.md) | [English](README_EN.md)

A test build of an AUTO-battle mod for the Steam version of **Legend of
Keepers**. It loads when the game is launched normally from Steam and adds an
AUTO toggle in the upper-right corner of combat.

## Download

Download `LegendOfKeepers_AutoBattle_v0.6.30_TESTERS.zip` from the
[v0.6.30 release](https://github.com/Trioracks/LegendOfKeepers-AutoBattle/releases/tag/v0.6.30).

This is a test build. It does not contain the game, Steam, saves, or original
game DLLs.

## What AUTO does

With the upper-right AUTO icon enabled, the mod chooses actions through the
game's own visible UI tiles:

- monster attacks;
- master spells between fights;
- disaster choices in disaster rooms.

The priority uses the game's live previews of health damage, morale damage,
targets, area effects, resistance, and current statuses. The mod does not
simulate mouse input and does not launch attacks, spells, or disasters
directly.

### New in v0.6.30: one-room trap horizon

During a fight, AUTO can consider **only the immediately next** normal AOE
trap room. It avoids spending a current hit on a hero only when that trap is
proven to defeat the hero. The proof covers direct health/morale damage,
resistance, effect immunity and dodge, stack reduction, known deterministic
trap amplification, and known monster death passives that affect the next
trap. Random, conditional, special, and unresolved mechanics always fail
open: the hero remains a current-fight target.

## Install and run

1. In Steam, open **Legend of Keepers → Manage → Browse local files**.
2. If the folder already contains `BepInEx`, `dotnet`, `winhttp.dll`,
   `doorstop_config.ini`, or `.doorstop_version`, back them up first. This
   test package is intended for an installation without other BepInEx mods.
3. Extract the **contents** of the ZIP into the folder containing
   `LegendOfKeepers.exe`.
4. Launch the game normally with Steam's **Play** button. No separate launcher
   is required.

To update, close the game, extract the newer package contents into the same
folder, and accept the file replacement/merge prompt. Saves and original game
files are not included in the package.

The bright AUTO icon means ON; the dim icon means OFF. Turning AUTO off stops
future choices immediately. An action already accepted by the game can finish
its own native animation, but the mod will not submit the next action.

To remove the test package from a clean installation, remove the added
`BepInEx`, `dotnet`, `winhttp.dll`, `doorstop_config.ini`, and
`.doorstop_version` entries. If you had your own BepInEx installation before,
restore its backup instead.

## Feedback and compatibility

This is a test build for the current Windows Steam release. Please send a full
in-game screenshot, `BepInEx\\LogOutput.log` after a normal game exit, and
short reproduction steps. See [the English feedback template](docs/FEEDBACK_EN.md)
or [the Russian template](docs/FEEDBACK_RU.md).

The repository intentionally contains no game files, extracted game metadata,
interop assemblies, saves, or tester logs. The mod uses
[BepInEx](https://github.com/BepInEx/BepInEx) as a runtime dependency; it is
not part of the game.

## Technical details

- Plugin version: `0.6.30`
- GUID: `zubko.legendofkeepers.battleeventinspector`
- BepInEx IL2CPP x86: `6.0.0-be.785`
- Target Unity version: `2019.4.18f1`
