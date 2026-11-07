<img src="docs/assets/icon.png" alt="NuclearLiveFeed" width="96" height="96">

# NuclearLiveFeed

Version 0.3.2 (`dev.dikka.nuclearlivefeed`). It is an active mod: `install-all-mods.sh`, the build loops and the modpack (`tools/nomnom-mirrors.toml`) include it. The viewer page is not part of the mod package.

NuclearLiveFeed streams the live battlefield from your game to map viewers: aircraft, missiles and bombs, ground vehicles, ships and structures. Viewers receive the feed. The only thing they can send back is a change to your target list, and only when the setting allows it. The companion viewer is in [viewer/](viewer/README.md).

## What viewers see

Only what your side knows, in single player and multiplayer:

- Your side's units at their exact positions.
- Enemy units only while your side tracks them. Observed tracks move live; older tracks stay at their last known position and are marked as last known.
- Neutral units and all structures.
- Missile and bomb launches and detonations for munitions your side can see.
- Which aircraft is yours, so the viewer can follow it and measure from it.
- Airbases, their owning side and their runways (ends, names, landing and reversible flags).
- Your current target list, newest first, and whether you can change it (you fly an aircraft).
- Per unit type, the range of its anti-air weapons (the game's own turret fire limit), so viewers can draw air-defense rings for enemy sites your side knows.

Last-known enemy tracks keep the heading and damage state from the last moment your side saw them.

`Visibility / Reveal All Units In Single Player` sends every unit, but only in single player.

## Target selection from a viewer

A viewer may change your target list through the same game calls the in-game map uses: only units your side knows, never your own aircraft, not units hidden by the in-game target filter, at most 128 targets. `Server / Viewer Target Selection` decides who may do it: `ThisPC` (default, browsers on this PC), `Lan` (any connected viewer) or `Off`.

## Connecting a viewer

1. Install the mod and start a mission.
2. Open the viewer page in Chrome or Edge on the same PC.
3. When the browser asks to access apps on this device, allow it.

`https://map.scramd.com/guide` explains the map to players: connecting, symbols, marks and controls.

The feed listens on `ws://127.0.0.1:39391`. For a tablet or phone on the LAN, set `Server / Allow LAN Viewers` to true and enter `ws://<this PC's LAN address>:39391` in the viewer (click the status line). Viewer pages must come from an allowed origin (`Server / Allowed Origins`): by default `https://map.scramd.com` and local development pages.

While no viewer is connected (and UDP output is off), the mod samples nothing, so it costs almost no frame time. When a viewer connects, a new feed session starts within a frame: the viewer gets a session reset and then the full current picture.

## Settings

| Section / Setting | Default | Meaning |
| --- | --- | --- |
| General / Enabled | true | Run the feed server. |
| Server / Port | 39391 | WebSocket port. |
| Server / Allow LAN Viewers | false | Listen on all interfaces instead of 127.0.0.1 only. |
| Server / Allowed Origins | `https://map.scramd.com, http://localhost:*, http://127.0.0.1:*` | Web pages allowed to connect. |
| Server / Allow Non-Browser Clients | true | Accept local tools that send no Origin header. |
| Server / Viewer Target Selection | ThisPC | Which viewers may change your target list: `Off`, `ThisPC`, `Lan`. |
| UDP / Enabled | false | Also send each DIS PDU as a UDP datagram. |
| UDP / Host, DIS Port, CoT Port | 127.0.0.1, 3000, 0 | UDP destination; CoT port 0 sends no CoT over UDP. |
| DIS / Exercise ID, Site ID, Application ID | 1, 1, 1 | DIS identifiers of this producer. |
| Updates / Sample Rate Hz | 20 | How often the game state is checked. |
| Updates / Max Entity Rate Hz | 10 | Upper limit per entity. |
| Updates / Heartbeat Seconds | 5 | Full state refresh interval. |
| Updates / Air, Ground Position Threshold | 10 m, 5 m | Dead-reckoning error that triggers an update. |
| Updates / Orientation Threshold Degrees | 5 | Orientation error that triggers an update. |
| Visibility / Reveal All Units In Single Player | false | See above. |
| Map / Anchor Longitude | 0 | Longitude of the map center for DIS/CoT coordinates; latitude comes from the map. |

Server and UDP changes restart the feed; update settings apply immediately.

## Protocol

Binary WebSocket frames carry DIS 7 PDUs (Entity State, Entity State Update, Fire, Detonation); text frames carry CoT XML (hello, session, structures and airbases, ownship, target list). A new viewer first receives its hello (protocol version and what it may control), then a snapshot: session, structures, then every current entity as a full Entity State. Viewers send text frames `targets add|remove|set|clear [site:app:entity ...]`. The protocol version (`CotWriter.ProtocolVersion`, now 1) must match the viewer's `PROTOCOL`; raise both when a viewer could misread the stream. On a mismatch the viewer stops and shows "Update mod" or "Reload page". [DESIGN.md](DESIGN.md) documents the profile, the private conventions and the verification status.

## Checks

`tests/FeedChecks` runs the real encoder, geodesy, dead-reckoning tracker and WebSocket server without the game (43 checks). Run from the repository root with the workspace environment loaded:

```bash
dotnet run --project NuclearLiveFeed/tests/FeedChecks -c Release
```

Passing checks does not establish game behavior: the Harmony hooks, unit sampling, visibility rules and performance need a client test.
