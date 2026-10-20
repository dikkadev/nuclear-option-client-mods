# Nuclear Option client mods

Source for regular (non-VR) BepInEx 5 client mods for Nuclear Option.
This repository holds published source snapshots and releases only, not the
private development history.

## Mods

| Mod | Version | Plugin ID | Description |
| --- | --- | --- | --- |
| `NuclearConnectivity` | 0.2.3 | `dev.dikka.nuclearconnectivity` | Client-side Nuclear Option mod for external integrations, currently Steam Timeline combat markers. |
| `NuclearControllerGestures` | 0.1.1 | `dev.dikka.nuclearcontrollergestures` | Configurable additive controller double-press bindings for native button actions; original controls remain available. |
| `NuclearDeathBlackout` | 0.2.3 | `dev.dikka.nucleardeathblackout` | Client-side Nuclear Option mod that cuts to black and briefly mutes audio when the local main pilot dies. |
| `NuclearDelivery` | 0.1.3 | `dev.dikka.nucleardelivery` | Missile and bomb delivery counters on the target camera and cockpit MFD: flight, reported armor contact and lost deliveries. |
| `NuclearMapPlayerNames` | 0.2.3 | `dev.dikka.nuclearmapplayernames` | Client-side Nuclear Option mod that shows multiplayer usernames next to player-controlled aircraft on the dynamic map. |
| `NuclearMfdWeapons` | 0.1.4 | `dev.dikka.nuclearmfdweapons` | Cockpit weapon, ammunition and countermeasure display with a brief loadout preview and optional stock HUD. |
| `NuclearMinimap` | 0.3.2 | `dev.dikka.nuclearminimap` | Native minimap orientation and hold zoom, with an optional cockpit map display and configurable HUD visibility. |
| `NuclearSounds` | 0.1.1 | `dev.dikka.nuclearsounds` | Client-side designation tones, voice callouts, and fuel/terrain warnings with estimated fuel time. |
| `NuclearTargeting` | 0.2.1 | `dev.dikka.nucleartargeting` | Client-side alternative target selection with optional cycling and tap-selection caps. |
| `NuclearTrackAge` | 0.1.3 | `dev.dikka.nucleartrackage` | Client-side Nuclear Option mod that annotates stale enemy target tracks with how old the last known position is. |
| `NuclearUnits` | 1.2.4 | `dev.dikka.nuclearunits` | Client-side Nuclear Option mod that lets players choose units per HUD readout instead of relying only on the game's global metric/imperial setting. |

Each publication contains all of these mods at the versions shown.

## Requirements

- The .NET 10 SDK.
- A Nuclear Option client with BepInEx 5 installed.
- The game and BepInEx reference assemblies from your own installation. They are
  proprietary, are not part of this repository, and must not be committed or
  redistributed. Point the build at them with the two settings below.

## Build

```bash
git clone https://github.com/dikkadev/nuclear-option-client-mods.git
cd nuclear-option-client-mods
cp .env.example .env   # then edit the paths for this machine
set -a; source .env; set +a
```

Set `NO_MANAGED_DIR` to the game `NuclearOption_Data/Managed` folder and
`NO_BEPINEX_CORE_DIR` to the `BepInEx/core` folder, as environment variables or
as the MSBuild properties `NuclearOptionManagedDir` and `BepInExCoreDir`:

```bash
dotnet build NuclearConnectivity/NuclearConnectivity.csproj -c Release \
  -p:NuclearOptionManagedDir="/path/to/NuclearOption_Data/Managed" \
  -p:BepInExCoreDir="/path/to/BepInEx/core"
```

Build every project listed above the same way. Building does not install
anything: copy a built plugin into `BepInEx/plugins/<ModName>/` yourself.

## Checks

The `tests/` folder holds dependency-free logic checks, a metadata check against
your own client assemblies, and runtime acceptance notes in `tests/SMOKE-TESTS.md`.
Passing offline checks does not prove in-game behavior; see the notes for what
still needs a real client.

## Licensing

This README does not grant a license. Rights in this source are governed by a
`LICENSE` file where one is supplied, and third-party material by the
`THIRD-PARTY-NOTICES.txt` files beside the affected code.
