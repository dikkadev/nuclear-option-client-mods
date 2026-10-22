# Client compatibility and smoke tests

Use three checks for a game update: linked-source logic tests, a build against that client's assemblies, and a game smoke test. A successful restore or logic run alone does not establish compatibility. Keep test logs, reference hashes and decompiled snippets in ignored `.dev/local/` or `.dev/tmp/`.

## Logic and compilation

Run the command in [README.md](README.md) for the logic checks. Supply the current Windows client's full `NuclearOption_Data/Managed` directory and matching BepInEx 5 core assemblies through the local `.env`. Build all eleven active projects in Release. Do not substitute dedicated-server assemblies, add test stubs to production projects, or distribute game DLLs.

Record the game version/Steam build ID, mod source commit, BepInEx version and SHA-256 of `Assembly-CSharp.dll` and direct reference DLLs. Repeat the build when those references change. After rebuilding, run the [metadata preflight](ClientApiCheck/README.md) to verify Harmony target resolution and typed/reflected fields. Inspect current classes with ILSpy when a referenced method or private field changes; a compiling private-field lookup may still fail at runtime.

Prioritize these hook/member boundaries:

| Mod | Current-client inspection |
| --- | --- |
| ControllerGestures | Native user-assignable button-action enumeration; exact named gamepad controls; ReInput.UserData/custom-controller definitions and private saved-map array; explicit nongeneric GetAllMapSaveData selector |
| Delivery | Missile launch/detonation/removal lifetime, hitArmor result contract, local owner identity, TargetScreenUI.SetupCamera and exact TacScreen ownership |
| Units | CombatHUD, HUD flight readouts, MFD/landing and target display update hooks; layout objects |
| TrackAge | TrackingInfo.lastSpottedTime, faction tracking/accuracy APIs and four display refresh hooks |
| MapPlayerNames | UnitMapIcon lifecycle, aircraft pilot/player name access, map maximize state |
| MapTrails | DynamicMap.Update postfix, map icon list, faction mode, HQ tracking data, theme map icon colors |
| Targeting | Target-selection hooks and newest-first target list ordering; alternative selector configuration and optional designation event |
| Sounds | Gauge Initialize/Refresh and reflected voice/aircraft/TMP label fields; fuel and terrain APIs; Unity audio modules; optional Targeting event subscription |
| DeathBlackout | CombatHUD aircraft identity, mainPilot death/ejection state, UnitDisabled/RemoveUnit order |
| Connectivity | KillDisplay.DisplayKill attribution, local aircraft loss lifecycle, Steam Timeline native exports |
| MfdWeapons | TacScreen.Initialize/OnDestroy ownership, private canvas/aircraft fields, WeaponStatus weaponPanel/current readouts, station/countermeasure member contracts and actual per-aircraft canvas paths |

## Isolated client run

Installing or launching a game requires an explicit request. Use a separate test profile/copy when available, with the game's normal content and known save/config backup. Do not overwrite the user's normal plugins or configs. No server deployment is needed.

1. Establish a vanilla/BepInEx-only baseline in a small repeatable single-player mission.
2. Add one active mod at a time. Confirm its loaded log line and absence of Harmony patch/reflection errors in `BepInEx/LogOutput.log`. Check that it remains active after the main menu loads: startup success alone does not prove plugin survival. Exercise its row below, then repeat with all eleven together.
3. Try default settings, feature off, feature back on, respawn, scene exit/re-entry and a second aircraft. Check frame hitches and log spam as well as the visible result.
4. For multiplayer labels/tracks, use a controlled second client in a private session. Check local and remote names, faction changes and stale/accurate tracks. A single-player pass cannot verify these.

| Mod | Exercise and expected result |
| --- | --- |
| ControllerGestures | Default unbound controls do nothing extra; bind one button action, verify two short releases add it once while original bindings still run; slow taps, long holds, triples, simultaneous/different controls and controller transfer cannot create unintended doubles; menu/text/focus/aircraft/config changes cancel; save/copy leaves no transient maps |
| Delivery | Launch missiles and bombs with no target selected; then target/landing camera toggles; compare flight/contact/lost counters, retention, compact zero states; cockpit lower-left rows remain separate from radar/weapon UI; F8/popup, settings, eject/respawn and scene cleanup leave no duplicate UI |
| Units | Mixed speed/altitude/range units across HUD, MFD and landing screen; secondary speed on/off/same-unit; no duplicate line or leftover shifted layout |
| TrackAge | Enemy track at 3/4/34/35/60 seconds, reacquire it, friendly track, each surface off/on; age/stale changes once and removed labels stay removed |
| MapPlayerNames | Two player aircraft and AI; local-name toggle, maximized-only toggle, hide/show map, despawn/respawn; no orphan labels, names with markup display literally |
| MapTrails | Own and friendly player trails on minimized and maximized maps, beneath icons and name labels, constant width while zooming, fade/altitude shading, no enemy trails by default, enemy trails break on lost tracks with AllVisible, team change clears, despawn fades out, scene change leaves no orphan layer, cockpit minimap mirror |
| Targeting | Existing tap/hold/cycle/cap behavior, Targeting alone and with Sounds; no selector changes or duplicate patches |
| Sounds | All warning types, default/disabled/missing audio assets, aircraft initializes while clips preload; exactly one stock or replacement gear/overspeed/stall voice, fuel HUD works on first spawn; with Targeting, designation gained/lost tones occur once |
| Sounds fuel/terrain | Steady burn, delayed sample, refuel, zero burn, full-throttle thresholds; clear flight path versus approaching terrain, feature toggles and respawn cleanup |
| DeathBlackout | Main-pilot death/crash, successful ejection, leave/control-transfer then destroy former aircraft, respawn; one local effect, no effect after voluntary departure, screen/audio restore on disable/unload |
| Connectivity | Locally credited air kill, remote kill, optional ground/ship kill, pilot loss/ejection, former-aircraft loss after transfer; one appropriate marker, no startup/log exception if Timeline unavailable |
| MfdWeapons | Every mapped stock aircraft: weapon name/icon/ammo, reload, selected flare/jammer state, transient loadout preview, page toggle, stock HUD opt-in, disable/re-enable, respawn/eject/scene exit. Preserve Chicane instruments, check Darkreach orientation and long labels. Missing/unknown layouts keep stock HUD. All original active/layout states restore and no owned roots/event handlers survive teardown. |

### Plugins disappear after startup

If plugins load successfully but features remain absent, inspect Unity's `Player.log` as well as `BepInEx/LogOutput.log`. BepInEx 5 attaches plugins to one manager GameObject; destroying it stops component updates/coroutines and runs our cleanup methods, which remove Harmony patches. In NOSMR 1.2.5, `[NOSMR] Unloaded` is emitted by `OnDestroy`; seeing it before the main menu is evidence of early component destruction, although it does not alone prove that every plugin was destroyed.

Check `[Chainloader]` → `HideManagerGameObject = true` in the active game's `BepInEx/config/BepInEx.cfg`, the [BepInEx documented remedy](https://docs.bepinex.dev/articles/user_guide/troubleshooting.html#plugins-are-loaded-without-errors-but-do-not-work-afterwards). NOMM 5.4 writes this setting when it installs BepInEx, but skips that installation/configuration step when BepInEx already exists. Importing a modpack does not repair an existing core config. Preserve other settings and restart the game after a correction. If the setting is already true, investigate further instead of assuming that it is the cause.

Current builds also log each plugin's owned patched-method count, effective bound settings, first `Start`, and `OnDestroy`. An installed patch count proves registration, not execution. Map/TrackAge log first surface-hook activity and each eligibility decision once per process, including first label activation/appending. Keep the complete log through the failing action; these events make silent missing-feature reports distinguishable from configuration and UI visibility issues. They omit player names and IDs.

### Make name and track labels observable

Close the game before changing generated configs, and restore normal values after these checks:

- In `dev.dikka.nuclearmapplayernames.cfg`, temporarily set `Hide Local Player Name = false`. Spawn in a player-controlled aircraft and inspect its map icon. Default settings hide the local name, and AI aircraft have no player name, so ordinary single-player play is not a positive test.
- In `dev.dikka.nucleartrackage.cfg`, temporarily set `Show Only Outdated Tracks = false` and `Minimum Age Seconds = 0`. Select a known enemy in the target list; even a freshly observed enemy should have an `AGE` label. Friendly tracks remain excluded.
- Restore TrackAge defaults, then observe a moving enemy that loses all friendly sensor observation. Age starts appearing after four seconds only when the game also considers its stored position inaccurate (20 m threshold on most surfaces, 100 m in the target list). A stationary or continuously observed target may never receive a label. Reacquiring it should remove the label.

### Check Sounds audio directly

In the full pack, open F1, find NuclearSounds / Audio Test / Play Audio Test, tick once and close F1. After two seconds expect a 0.7-second tone; about two seconds later expect the custom lower-gear voice. Repeat from normal unpaused gameplay if the game was paused. Confirm a persisted true setting is reset on startup without playback, repeated clicks do not overlap tests, and unload cleans up the generated test clip. Compare audible results with the manual-test PCM, mixer gain/cutoff, source pitch/spatial/mute and next-frame/0.2-second playback snapshots in Player.log. `timeSamples` reflects an assigned source clip and may remain zero for a valid PlayOneShot; do not interpret it alone as failure. This manual test changes no global audio settings.

Disable missile voice warnings, restart, then enable them from F1 with a detected missile threat still present. The appropriate clip must remain available without restarting after the toggle change. Disable voices while threats appear/disappear, then re-enable after they clear: stale counts must not produce old callouts. Change aircraft while disabled and repeat; state must reset.

With designation tones enabled, clear all selected targets, put a known target inside the HUD designator selection radius and use the normal HUD target-select action, then clear the selection again. Expect short lock/lost tone sequences. Changing targets while the list remains nonempty does not trigger a lock/lost transition. This checks direct playback without needing a missile or low-altitude flight. The log reports cached MP3 count and the first direct playback request (clip data, source availability/volume/mute, listener volume/pause). A request is not proof of audibility; also check the game's normal audio mix. Terrain warnings may be disabled in an existing config.

Custom gear/stall/overspeed voices keep stock conditions and cooldowns; uneventful flight is not a positive test. Anti-radiation seeker warnings use the client's `ARAD` key and should select the custom radar clip. Check that case separately from IR/ARH/SARH/Optical. Startup or direct-playback diagnostics do not establish successful stock voice substitution.

### Check MFD presentation and stock restoration

Start in Chicane and compare its weapon panel and weapon-switch popup with the NOTT reference: icon visible at native scaled dimensions, readable fixed-size text, selected countermeasure enlarged and bold, and an opaque black popup with a two-pixel accent border and aligned station rows. Test short and long weapon names, empty ammo, reload fractions and rapidly changing stations. Check text/image colors after changing the TacScreen theme.

With Show Stock Weapon HUD off, the separate lower-right aircraft outline should be hidden. Switch F8 to stock, opt into the stock HUD, disable the mod and respawn: the outline and stock readouts must restore. Trigger an aircraft failure/damage event and confirm damage overlays, failure reports and damage audio still run with custom MFD content visible. Repeat in an aircraft with a different weapon panel and in Darkreach for rotation.

Check the entire gray stock HUD backing disappears, not only the text and icons. Show Capacitor HUD defaults off independently of Show Stock Weapon HUD: cycle both settings, activate the jammer and charge/discharge the supply, and verify native callbacks never reveal the hidden capacitor. With it enabled, verify the native bar/readout sits eight canvas units from the top-right edge at multiple resolutions. Toggle F8 and disable/re-enable the mod; confirm its native parent, sibling order, dimensions and placement restore without duplicate indicators. On VT-7, check the cockpit NOZZLE label sits beside the glyph at all nozzle angles, retains native style, and does not overlap the upper separator or lower engine panel; its Flight HUD counterpart must retain native placement. The bounded ready diagnostic records whether measured placement succeeded.

## Acceptance

Keep a result per mod and game build: **passed**, **failed**, or **not tested**, with the failing log excerpt/steps. Fix reproducible failures, rerun the affected case and combined session, and leave untested boundaries explicit. Package/version/release only after the client build and required runtime checks pass. Avoid compatibility badges based only on logic tests.

## Focused configuration migration

Verify first creation imports the exact relevant legacy values, preserves the original Tactical Tools file, and does not overwrite existing focused settings. Test Targeting and Sounds separately and together, with legacy Tactical Tools disabled. The focused plugins must refuse to load alongside manually enabled legacy Tactical Tools, preventing duplicate patches. Confirm optional designation subscription is removed on unload and generated tone clips are released. Delivery counters are outside this refactor.

### Delivery counters

Launch a long-range munition, then clear selected targets while it is still airborne. Expect the same tracked flight count in horizontal rows at the normal radar panel's bottom-left, with no extra full background. Check VT-7, nested Darkreach/Alkyon and Ibis panels. Reopen the target camera: its existing vertical counters should remain unchanged and normal rows should hide. Also check landing feed, disable/re-enable, aircraft transfer and ejection. Record the bounded `Delivery counters ready: normal cockpit radar` diagnostic (path, dimensions and layer) or `Delivery cockpit unavailable` warning if attachment fails. This checks actual rendering rather than only metadata.

NuclearDelivery 0.1.2 (`dev.dikka.nucleardelivery`) adds independent missile/bomb tracking and additive displays. Defaults enable both camera views (NOTT vertical M-left/B-right counters above bottom readouts) and cockpit radar rows (horizontal, bottom-left, sized to match the camera). Zero statuses and empty categories are hidden. Yellow means in flight, green means reported `hitArmor` contact, and red means no reported armor contact, removal or flight expiry; green does not verify damage or destruction. Completed results remain for three seconds and flight tracking expires after 120 seconds by default. Master, target-camera, landing-camera and cockpit toggles, sizes and offsets are configurable. Tracking does not depend on target selection or camera availability.

The target overlay owns children under the exact aircraft's lazily created TargetScreenUI; the cockpit overlay uses the source-only NuclearMfd radar surface without replacing stock children. Keep it separate from the weapons replacement and loadout popup, preserve radar behavior, and clean up on aircraft/scene changes. The user accepted the Targeting/Sounds extraction and target-camera counters; normal radar rendering now works but was too small; size matching and landing counters still require client acceptance. Build and metadata checks do not establish delivery game behavior.

Delivery0.1.2: compare radar boxes with target camera at default scales on VT7 and differently scaled panels. With an in-flight munition, extend gear to activate the landing camera; check both M/B columns above mode/magnification, then return to radar/target. Test each independent view toggle, old saved scale values, aircraft transfer and disable/re-enable. No owned overlay should remain on the previous aircraft.

Engagement markers: select two or more targets and fire one guided missile at one of them. Only that target's camera box should show the green missile icon below it, and the icon should follow the box. Fire a second munition at the same target to get the count "2". The marker must clear at detonation or loss of lock and move when a munition retargets. In multiplayer, check that an allied launch marks the target only while Mark Allied Munitions is on, and that enemy missiles never mark a target. Check the icon is visible in color and IR modes, then check the marker toggle and scale, target deselection, aircraft transfer and disable/re-enable. Record `Delivery counters ready: target engagement markers` or the targetBoxes unavailable warning.

### Native minimap on the cockpit Radar screen

NuclearMinimap 0.1.0 runtime acceptance is pending. Compare its Radar presentation with the native HUD minimap at matching range and heading: terrain, contacts, own-aircraft position, selected targets and existing map labels must agree. Check VT-7 plus nested Darkreach/Alkyon and Ibis radar surfaces, radar-equipped and optical-only aircraft, and theme/resolution changes. Confirm source refresh continues while the HUD copy is hidden.

Cycle `AlwaysShow`, `AlwaysHide` and default `HideWhenMfdVisible`. Default hides the minimized HUD only while the cockpit map is available and displayed; external view, active target/landing feed and unavailable source restore the normal HUD policy. Open/close the expanded map under every mode; preserve its interaction and navigation. Return to normal Radar, disable/re-enable, change aircraft, eject and leave the scene; native radar visuals and HUD states must restore without duplicate roots or stale source textures.

Enable Delivery and Weapons together, with either plugin loading first: radar delivery rows keep the accepted size over the map, target/landing counters retain their native feed behavior, and F8 weapons toggling plus loadout preview continue normally. An unavailable map must leave stock radar visible. Record the map attachment/source diagnostics and actual visual result separately from offline metadata passes.

Map0.1.1: switch HeadingUp/NorthUp while turning; verify terrain/grid direction, upright labels, aircraft icons and view cone agree in HUD/MFD. Hold controller D-pad up/down, release immediately and hold both; check continuous zoom, immediate stop, cancellation and bounds1..40. Bind keyboard shortcuts (factory default None), alternate controller buttons and Unbound. On a POV hat, check diagonals. Compare the same zoom on the HUD and MFD, with the HUD shown and hidden and camera feeds selected. Open/close the expanded map and verify native navigation, then chosen minimap zoom resumes. Disable/re-enable and change aircraft/scene. While chat, menu, F1 settings or text input are active, or application focus is lost, held inputs must not change zoom. Map0.1.2: NorthUp is the new-config default; saved choices remain. Zoom through1..40 while flying and turning in both orientations; ownship must stay centered, including VT-7 and HUD/MFD views. With Consume Zoom Bindings on (default), a native press action assigned to D-pad down must not fire while zooming or on release; an alternate input for the same action must still work. Turn consumption off to check both actions fire. Check native controls after menus, focus loss, fullscreen and disable/re-enable. Bind a keyboard chord and check main-key reservation without consuming the modifier. Save/reload native bindings and copy/remap controllers while active; assignments and enabled states must survive unchanged. Check POV diagonals and alternate gamepad controls. No physical controller or game input acceptance follows from the linked-source checks.

Map 0.1.3 tap/hold: with Consume Zoom Bindings on, tap D-pad down for less than 0.3 seconds and verify its native gear action fires once on release without zoom. Hold longer and verify zoom starts after the threshold, with no gear action on press or release. Repeat for D-pad up and native release/timed-up actions (e.g. Radar or weapon cycle). Alternate bindings for the same action must still work; opposing zoom holds cancel. Adjust the delay from 0.1 to 1 second. Cancel pending taps on menu/F1/chat/focus, aircraft switch, configuration changes and disconnect; no delayed action may affect another aircraft. For a keyboard chord, the bare main key remains native when the chord is inactive; configured chord taps replay and long holds zoom only; losing a modifier cancels. Save/reload native preferences and enumerate map-save collections during a short tap: original flags must persist, with no transient custom controllers, maps or source definitions. Verify press/release across normal and fixed input loops, plus cleanup on disable/unload. Physical input and native controller behavior remain unaccepted until checked in the client.

Map 0.1.4 backing: user accepted 0.1.3 interaction; verify HideWhenMfdVisible removes both HUD map and empty gray LowerLeftPanel backing while MFD map remains live. AlwaysShow and camera-feed fallback restore the backing with the HUD map; AlwaysHide suppresses both only while minimized. Fullscreen map, disabled mod, source failures, theme changes and aircraft transitions must restore captured native enabled state. Other LowerLeftPanel children must keep working. Offline asset inspection and build passes do not establish this visual fix.

## Targeting 0.2.0 capped taps

With fresh config, confirm native laser capacity is used: Brawler 3, Chicane 4, Vagrant 1, Cricket/Ibis 2. Tap distinct targets beyond capacity and confirm the oldest drops, newest remains first and selected marker/map icons match the native list. Tap an already selected target and confirm it moves to newest without duplication, marker flicker or losing an unrelated selection. When no candidate is eligible, nothing changes. Test priority and Next Target Order separately.

Hold selection to add beyond tap capacity; the next eligible tap must reduce the list to the current cap while preserving the chosen target. Enable the fixed override, test one and several slots, and verify switching modes/changing the number leaves selections intact until that tap. Missing/detached laser designators fall back to one; an idle disabled designator still supplies its actual capacity. Disable the master switch and verify stock tap/hold behavior returns.

Bind Toggle Fixed Tap Target Cap (unbound initially), tap/hold/release and confirm one toggle per press, reflected in F1 settings and preserved after restart. Verify no toggles in chat/text, F1/menu/map, unfocused windows, ejection, disabled aircraft or between scenes, and no deferred toggle when returning while held. A shared stock game shortcut remains stock too. Check config upgrades: old active fixed values survive, old inactive factory 2 becomes default1/off, explicit new values win, old Tactical config remains unchanged. Run with Sounds absent; Targeting still works. These checks require the client; offline passes are not gameplay acceptance.

## Controller double-press acceptance

NuclearControllerGestures 0.1.0 physical input and native action acceptance remain pending. Confirm every action binding starts Unbound, with configurable defaults Maximum Gap=0.30 s, Maximum Press Duration=0.25 s and Cooldown=0.30 s. Bind a harmless button action to one supported control; two short presses must trigger once on the second release, while that control's original action continues unchanged. Test the exact configured limits, slow taps, long first/second presses, repeated holds, a triple burst and holding through cooldown. A fresh neutral observation after cooldown must precede another pair.

Repeat after opening configuration/text entry, losing focus, maximizing the map, leaving flight, respawning, changing binding/timing, switching or disconnecting controllers and disable/re-enable. Pending taps cannot survive those changes. Separate directions/controllers cannot combine their first presses. Axis actions are unavailable. Save and reload native input settings while a pulse is active: no transient controller or owned map may appear in saved settings, and another plugin's replay/input maps must remain intact. Compare action-down/up behavior with the normal native binding; offline sequence/metadata checks do not establish these runtime behaviors.

## Minimap0.2.2 HUD reference marker

With HideWhenMfdVisible, confirm the small green HUD heading-reference dot disappears together with the HUD minimap and panel backing when the cockpit map is visible. Confirm it returns with camera feeds/unavailable cockpit surfaces, Show Cockpit Minimap off, master disable and the expanded map. AlwaysShow retains the marker; AlwaysHide hides it with the minimized HUD map. Previously disabled Image states must stay disabled after restoration, and threat-list children/other HUD elements must remain active. Offline asset inspection and compilation do not establish visual acceptance.

## Minimap 0.3.0 device zoom bindings

Open NuclearMinimap / Controls in ConfigurationManager. Verify the device list and Add zoom in/out buttons render at narrow and wide menu sizes. Select a stick, throttle, gamepad, keyboard or mouse, then learn a button/key/POV direction; learn both signs of a centered joystick axis and a trigger that rests at an endpoint. Mouse movement/wheel must not be captured. Held buttons, deflected axes and drift must not be captured merely by opening capture. Recenter an axis to capture its existing direction. Cancel, wait 15 seconds, collapse/filter the setting, close F1, lose focus and disconnect during capture; no row or deferred action may appear.

Save several rows per device and use stick/throttle together, including a joystick not assigned to player0. Usable rows replace the old defaults only on that device; other devices retain them. Remove all rows on a device to restore its defaults. Duplicate bindings and the same source assigned to opposite directions must be rejected by the editor; independently held opposing controls cancel zoom. Native short taps replay once, long holds zoom only, and alternate game bindings still work. Full-axis/analog actions stay active during axis zoom and the editor marks them; signed Button-action mappings consume only their own pole.

Restart and reconnect devices in a different order. Each row must follow its persistent GUID, never a recycled runtime ID. Test two identical controllers where hardware-only identity is unavailable: ambiguous rows stay inactive. A stale element ID/type must show control unavailable and retain defaults if no valid rows exist. Invalid config strings disable all custom rows without being overwritten until explicitly cleared. Save/reload/remap native input while zooming; no transient controllers/maps/reservations persist. Aircraft/player changes, UI/focus/fullscreen and config edits cancel pending native taps and require neutral before resuming. These physical, menu-layout and game-input checks remain pending after offline checks pass.
