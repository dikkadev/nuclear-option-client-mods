# NuclearLiveFeed: the live map for Nuclear Option

**Open the map: <https://map.scramd.com>** (Chrome or Edge, on the PC that runs the game)
**How to use it: <https://map.scramd.com/guide>**

NuclearLiveFeed puts your battle on a second screen. While you fly, a browser window shows a live map of what your side knows: aircraft, ground units, ships, missiles and bombs, airbases and runways, and the reach of known enemy air defenses. From the map you can also pick your in-game targets.

## Get started

1. Install NuclearLiveFeed with the NOMM mod manager (search for it), then start the game.
2. Start a mission, join a side and take an aircraft to use the flight readouts and target controls.
3. Open <https://map.scramd.com> in Chrome or Edge on the same PC.
4. When the browser asks to access apps on this device, click **Allow**.

The map's status at the top left says **Live** when it is connected. If it does not, see "Problems and fixes" in the [guide](https://map.scramd.com/guide#trouble).

## What you get

- **Threat areas**: where every enemy air defense site your side knows (vehicles, ships and anti-air emplacements) can reach you at your current height. The flight strip says IN RANGE when one can, with an exit from the named site; other sites may still cover it.
- **Flight strip**: missiles guiding on you with the estimated seconds to impact, the way home to the nearest own runway, and the chosen enemy aircraft with bearing, distance, height and aspect (HOT, COLD, FLANK).
- **Paths**: where each aircraft flew, and where it will be in the next seconds if it keeps its course.
- **Details**: click any unit for its type, side, state, bearing and distance from you, altitude, speed and, for air defense, its range. Click a group for a list of its units.
- **Targets**: Shift-click or the Target button adds eligible enemy or neutral units and buildings to your in-game target list. A Shift-drag box adds enemies only; a double-click makes one unit your only target. The game's own rules apply.
- **Contacts**: Enemy air, Enemy ground and Own side tabs. Aircraft use Nearest or Approach ordering; ground defenses have Threat sorting. Aircraft rows show relative height while flying, MSL without your aircraft, and live enemy aspect.

- **Flight and team tools**: Pin an aircraft from its details, use Copy call (Copy my call for your own aircraft), share an exact bullseye through Layers, and Ctrl-drag to measure. The [guide](https://map.scramd.com/guide#team-calls) explains team calls.

## Fair play

The map shows only what your side knows, the same as your side's radar and spotters give you in the game. In multiplayer this is always so. Enemies your side does not track are not on the map, enemy buildings included until your side finds them; enemies it lost stay at their last known position. Map pictures come from the website; the live data goes from your game straight to your browser, never to the website.

## Settings

Install BepInEx ConfigurationManager to use the F1 settings window. Press F1 in the game and choose NuclearLiveFeed. Without that settings window, edit `BepInEx/config/dev.dikka.nuclearlivefeed.cfg`.

| Setting | Default | What it does |
| --- | --- | --- |
| General / Enabled | On | Sends your side's picture to the map. |
| Server / Viewer Target Selection | ThisPC | Who may change your targets from the map: ThisPC, Lan (other PCs too, with Allow LAN Viewers on) or Off. |
| Server / Allow LAN Viewers | Off | Lets a map on another PC in your network connect. |
| Server / Port | 39391 | The port the map connects to. Change it only if another program uses it. |
| Visibility / Reveal All Units In Single Player | Off | Single player only: shows every unit. |

The other settings are for external tools and fine tuning; the guide's [Mod settings](https://map.scramd.com/guide#settings) explains them. With no map open and UDP output off, the mod does almost no work.

## Watch from another PC

Turn on **Server / Allow LAN Viewers**, then open the map on the other PC, click its status and enter `ws://<game PC address>:39391` (or the port you set). To pick targets from there too, set **Server / Viewer Target Selection** to **Lan**. The guide's [Watch from another PC](https://map.scramd.com/guide#other-pc) has the details.

## Unofficial

An unofficial fan project, not affiliated with, sponsored by or endorsed by Shockfront Studios. It needs a legally acquired copy of Nuclear Option.

For developers: [source, feed format and design on GitHub](https://github.com/dikkadev/nuclear-option-client-mods/tree/main/NuclearLiveFeed).
