<img src="docs/assets/icon.png" alt="NuclearSpectator" width="96" height="96">

# NuclearSpectator

`NuclearSpectator` is a client-side `Nuclear Option` BepInEx mod that improves spectating other aircraft.

## Features

- **Cockpit view with a working cockpit:** when you have no aircraft of your own and look from another aircraft's cockpit, the mod builds that aircraft's native cockpit displays, the same ones its pilot sees. Its HUD is off by default (Show HUD):
  - HUD: compass, pitch ladder, flight path marker, the helmet speed/altitude/heading readouts, the aircraft's own HUD gauges and its damage display.
  - Cockpit screens: the MFD radar screen, clock and the aircraft's instrument pages.
  - The cockpit stick and throttle move with the pilot's inputs.
  - A target camera on the cockpit screens, aimed at the pilot's first selected target. Other players' aircraft have no native target camera on your client, so the mod builds one from the game's own camera rig. The game's target information overlay is not shown. With NuclearMinimap 0.4.4 or later installed, its cockpit map takes that screen area instead, the whole time you spectate.
  - The weapon and target HUD (target boxes, weapon reticles) stays hidden: it is built around your own target selection.
  - Leaving the cockpit view, switching aircraft or spawning your own aircraft removes all of it again.
- **Move around in the cockpit view:** while you spectate from a cockpit, the free-camera move controls (left stick, or WASD) move your view relative to where you look; on a gamepad the right trigger moves up and the left trigger down. The view stays attached to the aircraft, up to 25 m away. Speed follows a curve: slow near the stick centre for fine moves, 10 m/s at full stick. Hold Fast Move (Left Shift or the gamepad's A button) for four times the speed; it eases in and out. Beyond 1.5 m you see the aircraft's outside and its pilot instead of the cockpit interior. The view reset control (Center) glides back to the pilot's eye point in under a second; moving stops the glide. A binding (Return To Seat, unbound by default) does the same glide in addition to Center, without resetting where you look. A binding (Lock Horizon, unbound by default; a key, gamepad button or joystick button) switches to a level view: it follows the aircraft's heading but not its pitch or roll, and mouse look and head tracking turn, tilt and roll relative to the horizon. Locking and unlocking turn the view smoothly over 0.6 s instead of jumping.
- **Rotors that spin:** a helicopter (or propeller aircraft) that is already flying when it first appears on your client, for example after you join a server or start spectating, now starts with its rotors at flight speed. Without the mod, the game starts them folded and stopped, and they take about half a minute to spin up while the aircraft flies, which looks broken in every view. This applies only while you spectate; nothing changes while you fly.
- **No empty HUD:** while you have no aircraft, the game no longer shows its HUD with placeholder text (weapon name, ammo), for example after you close the map in a cockpit view.
- **Hide UI in steps (F10):** each press goes to the next step, for clean video and screenshots: first the game's interface (HUD, chat, kill feed, map, panels) is hidden and this mod's info panel stays; then everything is hidden; then everything shows again. Cockpit screens stay on. It works only while you spectate: while you fly it does nothing, and getting an aircraft shows everything again. The pause menu and full map still show.
- **Info panel:** pilot, aircraft type, speed, altitude (sea level and above ground) and heading of the aircraft you follow, at the bottom of the screen. Off in the cockpit view by default (Show In Cockpit View).
- **Cockpit view first:** when you start following an aircraft without one of your own (from the map, the scoreboard, the game's spectate controls or the aircraft keys), you see it from the cockpit instead of from outside. Switching from the outside view of one aircraft to another stays outside, and switching from a cockpit view stays in the cockpit.
- **Follow the nearest aircraft when yours is lost:** when the aircraft you follow is destroyed, its pilot dies or ejects, or it leaves the game, the camera follows the aircraft nearest to it after 2 seconds, of any team, in the same view (from the cockpit only while Keep Cockpit View is on). After an ejection the game first follows the pilot; the switch still comes. Choosing something else during the 2 seconds (map, scoreboard, spawn menu, aircraft keys) cancels it, and the pause menu delays it. AI aircraft count only with Include AI Aircraft on, the same setting as for the aircraft keys. Enemy aircraft your team does not track are skipped, because the game's camera cannot follow them. A binding (Toggle Follow Nearest, unbound by default) switches it on and off; a line at the info panel position shows whether it is on, also in the cockpit view. F10 keeps that line in its first step and hides it in the second.
- **Next/previous aircraft keys:** follow the next or previous aircraft, players sorted by name, optionally AI after them. From the cockpit view you stay in the cockpit view. Players in a team cycle through their own team. They are unbound by default; bind them in Configuration Manager (F1).

Everything uses the game's own prefabs and units. Nothing applies while you fly your own aircraft, and nothing is sent to the server.

## Requirements

- `Nuclear Option`
- `BepInEx 5`

## Installation

1. Install BepInEx for `Nuclear Option`.
2. Build the mod or download a release archive.
3. Extract the `NuclearSpectator` folder into `BepInEx/plugins/`.

```text
BepInEx/
  plugins/
    NuclearSpectator/
      NuclearSpectator.dll
```

## Configuration

Config entries are created automatically after the mod loads.

Main config file:

- `BepInEx/config/dev.dikka.nuclearspectator.cfg`

Bindings (Hide All UI, Fast Move, Lock Horizon, Return To Seat, Next Aircraft, Previous Aircraft, Toggle Follow Nearest): in F1, select **Add binding** and press a key (with Ctrl, Shift or Alt for a combination), a mouse button, a gamepad button, trigger or D-pad direction, or a joystick button, hat or axis direction. **Remove** deletes one control. A gamepad binding works on any gamepad; a joystick binding belongs to that device and waits while it is disconnected. Bindings add to the game's own bindings. They do nothing while you type, in the pause menu, with F1 open or without focus; Lock Horizon also waits while the full map is open. The first time a bound control fires, the log records it (`Binding 'Lock Horizon' fired from ...`).

Defaults:

```ini
[General]
Enabled = true
Hide All UI = F10

[Cockpit View]
Show HUD = false
Show Cockpit Screens = true
Animate Stick And Throttle = true
Show Target Camera = true
Hide Empty HUD = true

[Cockpit Movement]
Enabled = true
## Metres per second at full stick
Move Speed = 10
## 1 is linear
Stick Curve = 2
Fast Move = LeftShift; Gamepad:ButtonSouth
Fast Move Multiplier = 4
## Metres
Max Distance = 25
Show Exterior Beyond = 1.5
Triggers Move Up And Down = true
Lock Horizon =
Return To Seat =

[Rotors]
Fix Rotors Of Aircraft Already In Flight = true

[Info Panel]
Show Info Panel = true
Show In Cockpit View = false
Font Size = 22

[Aircraft Cycling]
Next Aircraft =
Previous Aircraft =
Include AI Aircraft = false
Keep Cockpit View = true

[Following]
Start In Cockpit View = true
Follow Nearest When Lost = true
Toggle Follow Nearest =
## Seconds
Follow Nearest Delay = 2
Show Follow Nearest Status = true
```

All values apply live through Configuration Manager. Changing a cockpit setting rebuilds the current cockpit view.

## How it works

The game creates an aircraft's HUD gauges and damage display (`AircraftParameters.StatusDisplay` and `HUDExtras`) only for your own aircraft, and its cockpit creates the MFD screen (`TacScreen`) only for the aircraft on your `CombatHUD`. While you spectate in a cockpit, the mod instantiates the same prefabs for the followed aircraft and enables its `Cockpit` animation.

Several native components look up their aircraft through `CombatHUD.aircraft` or `GameManager.GetLocalAircraft`, which are empty while you spectate. Harmony transpilers rewrite exactly those reads in the HUD and MFD app managers, the artificial horizon, the pylon, engine and radar-warning displays and the helmet display. The rewritten read returns the native value, or the spectated aircraft only while one of the mod's own copies (or the helmet display, during a spectator cockpit view) runs. `CombatHUD.aircraft` itself is never written, so targeting, input and other mods see no change.

Some native components never unsubscribe from aircraft events, because they normally live until the aircraft is disabled. Before the mod removes its copies early, it removes every event handler that points to them, so a later aircraft event cannot fail on a destroyed component.

`CameraStateManager.SetFollowingUnit` always switches to the outside (orbit) view. A postfix switches to the cockpit view when you have no aircraft and the view before was nothing (or a lost aircraft) or another aircraft's cockpit. When the followed aircraft is lost, the game drops to the free camera (after an ejection it follows the pilot); the mod then follows the nearest live aircraft and gives it the lost aircraft's view. Another player's `Aircraft.HasEjected()` stays false on your client, so the mod reads the pilot's own ejected and dead flags, which every client sets. A follow change the mod did not make cancels a pending switch, except the game's own steps of the same loss: the hand-off to the ejected pilot, the drop of the disabled aircraft and the drop of that pilot.

Remote aircraft choose their rotor and propeller start state in `Unit.onInitialize`, at their spawn point and before the first network position arrives. The first network position calls `Aircraft.SpawnedInPosition`, which measures the real radar altitude and already warm-starts the turbines. The mod repeats the native rotor and propeller start at that moment, with the game's own airborne test.

The game log lists, once per aircraft type, which native components the spectator cockpit created. It also reports any failure once, with its stack, and disables only the affected part.

## Client testing

On 2026-10-29 the user ran 0.1.0 in their client: rotors of helicopters already in flight spun correctly, and the cockpit screens did not work. Cause: other players' aircraft never get a `TargetCam` (it needs network authority), and `TacScreen.Initialize` subscribes to it unconditionally, so it threw and the mod removed the screen. 0.1.1 guards that subscription with a transpiler, and adds cockpit movement. 0.1.2 adds the target camera, trigger up/down movement, empty-HUD hiding and Hide All UI. On 2026-10-30 the user reported that in 0.1.2 nothing worked (F10, cockpit screens, stick animation) and the placeholder HUD still appeared after closing the map; the info panel was also missing, so the runtime was not running. 0.1.3 keeps settings and the runtime independent of the BepInEx plugin component (which BepInEx can destroy early), starts the runtime before patching, shows an in-game "is running" message and logs why a cockpit view gets no spectator cockpit and what the cockpit screen renders into. It also adds Lock Horizon and raises the move speed default to 7 m/s (saved 5 becomes 7). The user's 0.1.3 log (read on their PC) showed the plugin loaded, patches applied and the horizon lock toggling, but no line from the separate DontDestroyOnLoad runner object, so its per-frame work never ran; the info panel was off in their config. 0.1.4 drops that object and ticks the per-frame work from the plugin's LateUpdate and a CameraStateManager.LateUpdate postfix, at most once per frame. On 2026-10-30 the user ran 0.1.4: Hide All UI worked, the cockpit screens showed, and the target camera was a black square; the log showed the cockpit built for three aircraft types and the damage display copy throwing in native engine damage. On 2026-10-30 the user reported that Lock Horizon worked from a key but not from a controller button: it was a BepInEx keyboard shortcut, which reads Unity's legacy input, and the game's controllers only reach mods through Rewired. 0.1.5 moves all four bindings to the shared binding editor (`Shared/NuclearBindings`), which reads controllers through Rewired; saved keys carry over. 0.1.6 swallows the damage display exception for the spectator's copy only, gives the target camera the native exposure, fog handling and nose/tail mounts and logs its camera setup, skips it when NuclearMinimap 0.4.4+ shows its map on that screen, and makes the horizon lock a level frame that mouse look and head tracking turn in. 0.1.6 is not yet run in a client. On 2026-10-30 the user asked for the cockpit view as the default spectating view, F10 in three steps and following the nearest aircraft after a loss; 0.1.8 adds them and is not yet run in a client. On 2026-10-31 the user asked for faster, non-linear cockpit movement with a fast-move button, and reported that the regular HUD sometimes still showed while spectating. 0.1.9 adds the stick curve, 10 m/s and Fast Move, and treats a destroyed own aircraft as no aircraft: the game keeps it as yours, and on CombatHUD, for about 30 s until the server removes it, so every spectator feature waited that long. 0.1.10 (2026-10-31, user request) makes the view reset and the horizon lock smooth; 0.1.11 adds the Return To Seat binding. On 2026-10-31 the user asked that nothing from the mod applies while you fly and that the watched aircraft's HUD not show: 0.1.12 limits Hide All UI, the rotor fix and the Follow Nearest toggle to spectating, drops the in-game start message and turns Show HUD off by default (saved configs switch once, recorded in the hidden `[Internal] Defaults Revision`). Offline checks cover the cycling order, the native methods and IL reads the mod depends on, and compiled field types. They do not establish rendering, lifecycle or rotor behavior. See `tests/SMOKE-TESTS.md` for the client checks.

## Local Development Install

For local installs during development:

1. Copy the workspace `.env.example` to `.env`
2. Set `STEAMAPPS_DIR`
3. Run:

```bash
./scripts/install-mod.sh -v
```

## Building

```bash
dotnet build NuclearSpectator.csproj -c Release
```

Build output:

- `bin/Release/NuclearSpectator.dll`
