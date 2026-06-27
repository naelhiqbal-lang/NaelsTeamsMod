# NaelsTeamsMod

Fresh BepInEx 5 plugin for Super Battle Golf `1.1.2-436`.

Current feature set:

- Automatically assigns connected non-spectator players into balanced teams.
- Adds native team header rows to the normal TAB scoreboard.
- Colors name tags and scoreboard rows by team.
- Blocks same-team friendly fire against teammates and their balls.
- Adds `Teams` to the real Match Setup `Mode` dropdown.
- Patches the dropdown population step directly, so the game refreshing the menu should not remove `Teams`.
- Scans live dropdowns and patches `TMP_Dropdown.Show` as a fallback for the Mode dropdown.
- Shows Red/Blue team drag lists in Teams mode and records dropped players as runtime team assignments.
- Inserts team lists above Spectators and preserves the game's drag/drop helper object.
- Reopens the setup menu with assigned players visually restored into Red/Blue.
- TAB scoreboard team headers show `RED TEAM` / `BLUE TEAM` plus total points.
- Forces Red/Blue to be valid active drop targets during drag using the game's current input system.
- Temporarily uses the game's own cosmetic skin-color switcher to match live players to their assigned team color.
- Syncs host team assignments to other modded clients.
- Supports Steam ID team overrides from the generated config.
- Toggle overlay with `F8`.

This is intentionally small and testable before adding deeper gameplay hooks such as shared team balls.

## Install

Copy `NaelsTeamsMod-0.4.17.dll` into the active Super Battle Golf BepInEx plugins folder:

`Super Battle Golf/BepInEx/plugins/NaelsTeamsMod/NaelsTeamsMod-0.4.17.dll`

If using Thunderstore Mod Manager, import/copy it into that profile's `BepInEx/plugins` folder instead.

## Config

BepInEx creates:

`BepInEx/config/codex.sbg.modernteammode.cfg`

Useful settings:

- `TeamCount`: number of teams, clamped from 2 to 8.
- `TeamModeEnabled`: turns team mode on or off.
- `TeamNames`: comma-separated team names, for example `Red,Blue`.
- `TeamColors`: matching hex colors, for example `FF5555,5599FF`.
- `PlayerTeamOverrides`: optional Steam ID mapping, for example `76561198000000000=Red;76561198000000001=Blue`.
