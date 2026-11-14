<img src="docs/assets/icon.png" alt="NuclearMapFocus" width="96" height="96">

# NuclearMapFocus

`NuclearMapFocus` is a client-side `Nuclear Option` mod. While you use the expanded map, the mouse works the map and leaves the camera alone.

In the game, the camera behind the map still reacts to the mouse. The wheel zooms the map and, in some views, the camera at the same time. Holding the right mouse button (Free Look) turns the camera although you are looking at the map.

## Features

- The mouse wheel zooms only the map while the expanded map is open
- The camera does not turn while the expanded map is open: Free Look does nothing there, and the flyby view no longer follows the mouse
- The mouse wheel does not zoom the camera behind a menu, a dialog or the Configuration Manager window (F1)
- Each of the three rules has its own switch
- Flight controls, the map itself and the camera with the map closed are not changed

## Settings

`BepInEx/config/dev.dikka.nuclearmapfocus.cfg`, or F1 with Configuration Manager. Changes apply at once.

| Setting | Default | Meaning |
|---|---|---|
| General / Enabled | true | Off keeps the game's behaviour |
| Expanded Map / Wheel Zooms Only The Map | true | With the expanded map open, the wheel does not zoom the camera |
| Expanded Map / No Camera Look | true | With the expanded map open, Free Look and the flyby view's mouse look do not turn the camera |
| Windows / Wheel Does Not Zoom Under Windows | true | The wheel does not zoom the camera while a menu or dialog shows the mouse cursor, or while the Configuration Manager window is open |

## How it works

The game's camera views read the Zoom View, Pan View and Tilt View axes and the Free Look button on their own. The chase and orbit views already ignore them while the mouse cursor is visible. The cockpit view only checks for the expanded map before it zooms, the flyby view checks nothing, and the views that use Free Look do not check for the map. A Harmony transpiler adds a step behind those reads in the cockpit, flyby, free, controlled and aircraft-selection views. The step turns the value into "no input" while the rule applies. Nothing else in those views changes.

The wheel rule comes from Kerbiter's [Map Mouse Select](https://github.com/Metadorius/MapMouseSelect), which first fixed it for the cockpit and flyby views.

## Requirements

- `Nuclear Option`
- `BepInEx 5`

## Installation

1. Install BepInEx for `Nuclear Option`.
2. Build the mod or download a release archive.
3. Extract the `NuclearMapFocus` folder into `BepInEx/plugins/`.

## Build

From the repository root:

```bash
set -a; source .env; set +a
dotnet build NuclearMapFocus/NuclearMapFocus.csproj -c Release
```

`scripts/install-mod.sh` builds the mod and copies it into the game's plugin folder.