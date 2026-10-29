# NuclearSatelliteMap

`NuclearSatelliteMap` is a client-side `Nuclear Option` BepInEx mod that replaces the in-game map image with satellite imagery. Water is transparent, so the sea shows the map background (expanded map) or the world behind the HUD (minimized map).

The imagery is rendered from the game's own terrain, roads, buildings and trees. The default data has four times the native map resolution: 5 m per pixel instead of 20 m, so it stays sharp when you zoom in.

## Features

- Satellite imagery on the expanded map, the minimized HUD map and NuclearMinimap's cockpit copy
- Water fully transparent, with a soft coastline
- Separate opacity for the expanded and minimized map, applied live
- Optional native map behind the imagery, visible through water and transparency
- Map icons, grid lines, labels and other map mods (trails, player names) stay above the imagery
- Imagery is chosen by the map's size and native image size; unknown maps keep the native map

## Requirements

- `Nuclear Option`
- `BepInEx 5`
## Installation

The release ZIP includes the map data for Heartland and Ignus (about 127 MiB). Install it with NOMM, or by hand:

1. Install BepInEx for `Nuclear Option`.
2. Extract the `NuclearSatelliteMap` folder into `BepInEx/plugins/`.

```text
BepInEx/
  plugins/
    NuclearSatelliteMap/
      NuclearSatelliteMap.dll
      DATA-NOTICE.md
      maps/
        heartland-46396.nosat
        ignus-archipelago-46395.nosat
```

The mod also reads `.nosat` files from `BepInEx/NuclearSatelliteMap/`, or from the folder set in `Data Folder`. If you used that folder for an earlier version, delete the old files there.

The map data is derived from Nuclear Option game data and is not covered by the GPL license of the code. See `DATA-NOTICE.md`.

## Configuration

Config file: `BepInEx/config/dev.dikka.nuclearsatellitemap.cfg`. All values apply while playing through Configuration Manager.

```ini
[General]
Enabled = true
Data Folder =
## Full (5 m per pixel) or Half (10 m per pixel, a quarter of the GPU memory)
Texture Detail = Full

[Visibility]
Use On Expanded Map = true
Use On Minimized Map = true
## Overall opacity of the satellite layer; water is always transparent
Expanded Map Opacity = 0.8
Minimized Map Opacity = 1
## Native map image behind the satellite layer
Native Map Opacity = 0
```

Tuning tips:

- Minimized map hides too much of the view: lower `Minimized Map Opacity`.
- You want a sea back: raise `Native Map Opacity` to show the native map's sea through the transparent water.
- Low on GPU memory or loading stutters: set `Texture Detail` to `Half`.

Version 0.1.1 changes the `Expanded Map Opacity` default from 1 to 0.8. An existing config keeps its saved value. It also removes the land/water opacity and color settings: imagery shows in its original colors, land is opaque and water is transparent. Old entries left in an existing config are ignored.

## Memory

The data files are deflate-compressed: about 103 MiB for Heartland and 24 MiB for Ignus, which is mostly water. When a mission loads its map, a worker thread reads and inflates the file. The main thread then uploads it as BC3 (DXT5) GPU textures with full mip chains.

| Map | Image | Textures | GPU memory, Full | GPU memory, Half |
|---|---|---|---|---|
| Heartland | 16384 x 16384 | 1 | 341 MiB | 85 MiB |
| Ignus | 32768 x 16384 | 2 (west/east halves) | 683 MiB | 171 MiB |

GPUs limit one texture to 16384 pixels per side, so larger maps are split into tiles. The tiles' mip levels are cut from one pyramid of the whole map, so the halves meet without a visible seam. The upload can cause a short hitch, normally during mission loading. Only the current map is kept, and later missions on the same map reuse it. Files from older versions (10 m, one texture) still load.

## Producing data

`tools/build_satellite_data.py` builds the `.nosat` files from the separate `nuclear-option-map` satellite dataset and terrain geometry:

```bash
uv run --script tools/build_satellite_data.py --map all --preview
```

Water is classified from the game's terrain, road, building and pavement geometry at the game's sea level (Y = 0), with 4x4 supersampling for soft coastlines. Structures in that geometry stay opaque above water; bridges that are not part of it, such as Ignus's long inter-island bridges, become transparent over water. The default is `--scale 4` (5 m per pixel) from source level 6 (4 m per pixel). Output goes to `dist/satellite-maps/`.

The data is derived from game data. Do not commit it. Only the release ZIP carries it: `tools/nomnom-mirrors.toml` pins each file's SHA-256, and the release tool copies the matching files from `dist/satellite-maps/` into `maps/`. After you rebuild the data, update those pins and bump the mod version.

## Local development install

```bash
./scripts/install-mod.sh -v
```

The install script copies only the plugin. Copy the data files to `BepInEx/NuclearSatelliteMap/` yourself.

## Building

```bash
dotnet build NuclearSatelliteMap.csproj -c Release
```
