# NuclearLiveFeed design notes

Recorded 2026-10-25. Version 0.1.0 implements this design. It is built and checked outside the game; it has not run in a game client.

## Scope

- One producer: the game client that runs this mod.
- One or more viewers: a new web map app that uses the shared Nuclear Option map data (see Viewer).
- Mostly one-way flow. Since 0.3 the only viewer-to-game message is a target-list change (see Version 0.3), gated by a setting.
- Viewer drawings are out of scope for v1 (see Version 2).
- Content: aircraft, munitions, all vehicles (static and moving), and buildings.

## Protocol split (decided)

| Content | Protocol |
| --- | --- |
| Aircraft, munitions, all vehicles (static or moving) | Minimal DIS 7 profile (below) |
| Buildings | Cursor on Target (CoT) |
| Viewer drawings | Version 2 |

All vehicles use DIS, not CoT. A Detonation PDU names its target by DIS entity ID, so anything that a munition can hit must have a DIS entity ID. One ID space also gives one timeout model and one damage/appearance model.

## DIS 7 profile (IEEE 1278.1-2012)

### PDUs

| PDU (type) | Use |
| --- | --- |
| Entity State (1) | First appearance, heartbeat, change of dead-reckoning inputs, change of type/force/marking, final deactivation |
| Entity State Update (67) | Position, velocity, orientation or appearance refresh while the last dead-reckoning inputs are still valid |
| Fire (2) | Weapon launch or firing |
| Detonation (3) | Impact or explosion |

Remove Entity is not used. It is a Simulation Management PDU (section 7.5.3): a simulation manager uses it to request removal, with a request ID that expects an Acknowledge. Standard DIS tools do not read it as "this entity is gone". To remove an entity, send one final Entity State PDU with the appearance State set to Deactivated, then stop sending. Viewers also drop an entity after the timeout.

### Entity State Update limits

Entity State Update (section 7.2.5) contains only entity ID, linear velocity, location, orientation, appearance and variable parameters. It is about 72 bytes; a full Entity State PDU is 144 bytes without variable parameters. It has no dead-reckoning parameters (algorithm, linear acceleration, angular velocity), entity type, force ID or marking.

Viewers keep using the dead-reckoning parameters from the last full Entity State PDU. When acceleration or turn rate changes noticeably (for example, an aircraft starts or ends a turn), send a full Entity State PDU, not an update.

### Dead reckoning

| Entity class | Algorithm |
| --- | --- |
| Aircraft and missiles | 4, DRM(R,V,W): rotation and acceleration |
| Ground vehicles and ships | 2, DRM(F,P,W): constant velocity |
| Entities that do not move | 1, Static |

### Update policy

- Send when the dead-reckoning prediction error exceeds a position or orientation threshold, not at a fixed high rate. Map display tolerates larger thresholds than the DIS reference values (about 1 m and 3 degrees); start with roughly 5–20 m for aircraft and tune.
- Cap the rate at single-digit to low double-digit Hz per entity.
- Heartbeat: a full Entity State PDU about every 5 s. Viewer timeout: about 12 s.
- Binary DIS PDUs only.

### Viewer smoothing

Viewers extrapolate each entity with its dead-reckoning parameters between updates. When a new update arrives, they blend from the predicted to the reported state over about 0.3–1 s. Without this blending, entities jump on every correction.

### Munitions

- Guided munitions (missiles, guided bombs): DIS entities of kind 2 (munition) with their own Entity State PDUs. The Fire PDU references the munition entity ID.
- Unguided rounds: Fire and Detonation only, with no specific munition entity.
- Viewers use the Detonation result field (for example entity impact, ground impact, airburst) to choose the effect.
- Do not report more than the game confirms. NuclearDelivery found that the game's `hitArmor` report is a collision-material classification, not confirmed target damage. Map Detonation results from the game data accordingly.

### Damage

The producer is the only authority. It publishes damage through the appearance Damage bits of the target's Entity State PDU. Viewers only display it.

### Event loss

Fire and Detonation are one-time events. WebSocket delivery is reliable, so viewers do not lose them. On the optional UDP output, a lost datagram means a missed effect; if that matters, send each event twice there, and receivers drop the duplicate by event ID.

## Buildings through CoT (decided)

- Each building has a stable `uid`, a CoT `type` and a long `stale` time.
- Late joiners receive all buildings in the connection snapshot (see Transport).

## Transport (decided)

### WebSocket server in the mod

- The game thread only encodes PDUs and CoT messages and puts them on a queue. It does no network work.
- One background thread runs a WebSocket server. It:
  - accepts viewer connections;
  - keeps the current state of each entity (the last full Entity State PDU, with later Entity State Updates merged into it) and the CoT message of each building;
  - sends a snapshot on connect: all current entities as full Entity State PDUs, and all buildings;
  - then streams live traffic;
  - for slow viewers, keeps only the newest movement update per entity and never drops Fire, Detonation or deactivation messages.
- Framing: binary WebSocket frames carry DIS PDUs. Text frames carry CoT XML.
- Check the `Origin` header against the allowed viewer origins. Chrome's permission blocks other websites but not other local programs.
- Listen address and port are configurable: loopback only by default, optionally the LAN interface for other devices. Default port 39391. The first choice, 47391, failed in the first client test (2026-10-25): with WSL mirrored networking, Windows reserves the WSL Linux ephemeral range (44620-48715 on that PC), so the game could not listen there.

### Server implementation on Unity Mono

`HttpListener` cannot be used. In the game's own `NuclearOption_Data/Managed/System.dll`, every `HttpListenerContext.AcceptWebSocketAsync` overload throws `NotImplementedException`, and the `HttpListenerContext` constructor is a Unity `ThrowStub`.

Use `TcpListener` with a minimal HTTP upgrade handshake, then `WebSocket.CreateFromStream(stream, isServer: true, subProtocol, keepAliveInterval)`. The handshake reads the request headers, checks `Origin`, computes `Sec-WebSocket-Accept` (SHA-1 of the key plus the RFC 6455 GUID, Base64) and writes `101 Switching Protocols`. No third-party WebSocket library is needed.

Evidence (2026-10-25): in the game's `System.dll`, `CreateFromStream` returns `ManagedWebSocket.CreateFromConnectedStream`, and `TcpListener` and `ManagedWebSocket` contain no stubs or `NotImplementedException`. A test server built this way on system Mono 6.8 sent a 144-byte binary frame and a CoT text frame, received a text frame and closed normally with a Python `websockets` client. It has not run inside the game yet.

### Optional UDP output

When enabled, the same DIS PDUs (and CoT messages) are also sent as UDP datagrams to a configurable address and port (DIS convention: port 3000; unicast, broadcast or multicast). Native DIS tools and Wireshark can then listen. Off by default.

### Browser viewers and Local Network Access

The viewer is a hosted HTTPS web page that connects directly to the mod's WebSocket server. Chrome's Local Network Access permission allows this after the user allows it once per site:

- Same PC: `ws://127.0.0.1:<port>`, "Apps on device" (`loopback-network`) prompt. Loopback is not blocked as mixed content from HTTPS pages.
- Other LAN devices: `ws://<private IP>:<port>`, "Local network" (`local-network`) prompt. Chrome 147+ gates WebSockets with this permission and relaxes mixed content for private IP literals.
- The page must be HTTPS. The permission is per origin. A blocked or dismissed prompt fails silently, so the viewer must explain how to reset the site permission.

DCS Web Viewer uses the same approach: a DCS Lua hook serves HTTP on `127.0.0.1:31485` and the GitHub Pages viewer polls JSON every few seconds. NuclearLiveFeed pushes over WebSocket instead, for smooth dead-reckoned movement.

## Viewer

A new web app, separate from nomap, that uses the shared Nuclear Option map data. Map data and tiles must be hosted once and reused by every app; do not copy them per app. Candidate origin: `map.scramd.com` (HTTPS, needed for the Local Network Access prompt). Hosting is discussed separately.

## Version 2

- Viewer drawings shared between viewers. The producer never receives them.

## Implementation decisions (0.1.0)

- **Sampling:** `UnitRegistry.allUnits` at `Sample Rate Hz` on the game thread. Positions use `Unit.GlobalPosition()` (meters, X east, Y up, Z north from the map center). Velocity is the rigidbody velocity, otherwise a finite difference; both are scaled by `Time.timeScale`, so a paused game stops moving on the map. Scenery is skipped.
- **Visibility:** friendly (`NetworkHQ` equals the local HQ) and neutral units are exact. Opposing units appear only when the local HQ's `trackingDatabase` contains them: observed tracks are exact; other tracks use `lastKnownPosition`, static dead reckoning and the Frozen appearance bit. No local HQ means no faction units. Single player can reveal all units through the setting. Multiplayer never reveals more than the side knows.
- **Coordinates:** anchor latitude is the map's own `MapSettings.Latitude` (clamped to 80 degrees), longitude is a setting (default 0), height 0. ENU = (game X, game Z, game Y). The session CoT carries the anchor, so viewers invert it exactly.
- **Orientation:** DIS Euler angles come from the unit transform's forward, right and up axes mapped to ENU. Angular velocity comes from the rigidbody; Unity's left-handed pseudovector changes sign under the axis swap.
- **Types:** private table. Kind and domain follow DIS (aircraft 1/2, ground 1/1, ship 1/3, munition 2/0); country 0, category 0, and subcategory/specific carry a 16-bit index into the type table in the session CoT (code, name, class). The marking is the player's display name for player aircraft, otherwise the unit code.
- **Identifiers:** entity numbers are allocated per session from 1 to 65534 and reused after removal; the game `PersistentID` is the internal key. Event numbers count Fire/Detonation pairs per session.
- **Damage:** `UnitState` maps to appearance damage (Damaged: moderate, Abandoned: slight, Destroyed: destroyed). Removal sends a final Entity State with Deactivated.
- **Munitions:** `Missile.StartMissile` postfix sends Fire (firing entity = owner, target = `targetID`, munition = the missile entity). `Missile.UserCode_RpcDetonate_897349600` prefix sends Detonation and removes the munition. Result: unarmed = dud, unit-relative with `hitArmor` = entity impact, other unit-relative = entity proximate, `hitTerrain` = ground impact, otherwise detonation. Entity impact means the game's armor-contact report, not confirmed damage.
- **Buildings (open question 4, option a):** CoT `a-{f,h,n}-G-I` with `<nlf state force code>` in the detail, resent on change and every 10 minutes, `removed` when the building leaves the registry. Detonations on buildings carry no target entity.
- **Session:** CoT `b-x-nlf-session` with map name and size, anchor, forces (name, tag, color) and the type table. A new session ID tells viewers to clear their state (map change, mission end).

## Version 0.2: usefulness review (2026-10-25)

Three reviewers assessed a list of 19 ideas: a squadron pilot (second screen in flight), a UX minimalist, and an engineer who checked feasibility against the game code. Built from the consensus:

- **Fair-play fix:** unobserved enemy tracks sent the unit's live orientation and damage (only the position was last-known). Now they keep the last observed orientation and damage.
- **Ownship:** CoT `b-x-nlf-ownship` with `<nlf entity="site:app:entity"/>` from `GameManager.GetLocalAircraft`, republished on change and kept in the snapshot (uid-keyed like structures). Empty entity: no own aircraft.
- **Air-defense range per type:** session `<type airRange="m"/>` for ground units and ships: the longest `WeaponInfo.targetRequirements.maxRange` among stations with `effectiveness.antiAir > 0` (the turret AI's fire gate). It ignores terrain, radar detection and missile kinematics; viewers draw it as a ring, not a dome, and only for enemy sites the side tracks.
- **Airbases:** structure CoT with `<nlf kind="airbase">`, owner from `Airbase.CurrentHQ`, name from `SavedAirbase.DisplayName`, position `Airbase.center`; ship decks skipped. Capture progress is not sent: the capturing side is only known on the server.
- **Viewer:** ownship halo and label; north-up follow (auto when ownship appears, drag stops it, Recenter chip, C key) using the map library's new `setView`; bearing/range/height/closure from ownship in the details panel and at the cursor; enemy air-defense rings (dashed when stale, stronger when you are inside); airbases with owner; one-time pulse for new enemy contacts; collapsible layers panel; fullscreen; screen wake lock while following; containers hidden unless Structures is on; quieter own-side ground groups.

Rejected or deferred, with the reviewers' reasons: heading-up and auto-zoom (break spatial memory; heading-up also needs a large library change), radar coverage circles (detection is not a range circle), kills inferred from detonations (guessing; the game's own kill message is the honest source), enemy targeting or fuel (unfair), missile alerts (the cockpit warning is primary; an honest source would be `MissileWarning.knownMissiles`), replay, merged multi-player feeds, line of sight, audio. Candidates for later: kill feed from `MessageManager.RpcKillMessage`, bullseye reference, shared marks between squadmates, squadmate status strip, a feed access token for LAN/Tailscale viewers.

## Version 0.3: flying use (2026-10-26)

- **Target list, both ways.** Game to viewer: CoT `b-x-nlf-targets` with `<nlf editable entities="id id"/>` (newest first; index 0 is the primary target), checked at 10 Hz and sent on change. Viewer to game: one text frame `targets add|remove|set|clear [ids]`, at most 8 KB and 128 IDs. The mod applies it on the game thread like `CombatHUD.SelectUnit`/`DeSelectUnit`: only units with a HUD marker (own side and tracked units), not the own aircraft, not units excluded by `TargetListSelector`; bulk changes call `WeaponManager.TargetListChanged` once and play one sound. Each viewer gets a private CoT `b-x-nlf-hello` with `control="targets"` when it may send; `Server / Viewer Target Selection` (Off, ThisPC by loopback address, Lan). Commands from other viewers are dropped in the server.
- **Runways.** Airbase CoT carries `<runway name reverseName lat1 lon1 hae1 lat2 lon2 hae2 width landing reversible/>` from `Airbase.runways`: `Start`/`End` transforms, `GetName(false/true)`, private `width`, `Landing`, `Reversable`. Landing aircraft touch down near the start and roll toward the end (`RunwayUsage.GetTouchdownPoint`).
- **Viewer:** aircraft history up to 15 minutes (setting, default 3 minutes); projected paths from speed, DIS yaw rate and climb (constant-turn model; aircraft 10 s, 60 s with 10 s ticks for the selection, targets and you; surface units 30 s and 180 s; not for munitions); runways with 15 km extended centerlines and 2 km ticks on the landing side (own and neutral bases; enemy bases when selected); altitude tags on aircraft; a flight strip with the nearest own airbase, the nearest enemy aircraft with aspect, and missiles whose Fire PDU named you as the target; missile-to-target lines.
- **Viewer performance:** merged threat zones and tints on their own canvases, redrawn only on pixel changes; 24 frames per second when idle, full rate during input; follow recenters after 2 px of drift; no `backdrop-filter`; the hidden waiting panel leaves rendering. Measured in the user's Vivaldi with the live game: GPU process from 63% to 10% of one core.

## Open questions

1. Done 2026-11-01: the viewer and its map data are public at `https://map.scramd.com`, served from one R2 bucket (see `viewer/README.md`, Hosting).
2. Client test: run 0.1.0 in single player and multiplayer; check hooks, visibility, update rates, frame time and the viewer against the real game.
3. Done 2026-10-29: the mod is in `install-all-mods.sh`, the README build loops, ClientApiCheck and the modpack.

## Verification status

- Verified: Entity State Update fields and Remove Entity as a Simulation Management request, from Open-DIS Python `opendis` 1.0 `dis7.py` (docstrings cite IEEE 1278.1-2012 sections 7.2.5 and 7.5.3). Dead-reckoning algorithm values 1, 2 and 4, from the NPS DIS data dictionary.
- Verified (2026-10-25): all four PDUs written by the mod's encoder decode correctly with the independent Open-DIS Python library (`opendis` 1.0), including type 67 as Entity State Update, field offsets, identifiers, entity type, appearance, dead-reckoning algorithm and marking.
- Verified (2026-10-25): game API facts used above, from decompiled current client assemblies: `UnitRegistry.allUnits`, `GlobalPosition`, `FactionHQ.trackingDatabase` and `TrackingInfo.Observed()`, `GameManager.GetLocalHQ`, `MapSettings.Latitude`/`MapSize`, and the two Harmony targets with their parameter names.
- Not verified: the Deactivated (bit 23) and Frozen (bit 21) appearance bit positions, the 5 s heartbeat and 12 s timeout defaults, and the 1 m / 3 degree reference thresholds. Check them against IEEE 1278.1-2012 before other DIS tools rely on them.
- Verified (2026-10-25): DCS Web Viewer connection model, from its deployed live page code and the `dcs-web-editor/dcs-web-proxy` source.
- From secondary sources, not tested: Local Network Access versions (Chrome 142 fetch, 145 permission split, 147 WebSockets), mixed-content relaxation for `ws://` to private IPs, Firefox's similar prompt. Unknown: Safari and iOS behavior, whether Tailscale `100.x` addresses count as local network, and a reported extra WebSocket handshake check in Chrome 148. Test with a real browser before relying on them.
