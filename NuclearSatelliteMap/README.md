# NuclearSatelliteMap

`NuclearSatelliteMap` is a client-side `Nuclear Option` BepInEx mod that replaces the in-game map image with satellite imagery. Water is transparent by default, so the sea shows the map background (expanded map) or the world behind the HUD (minimized map).

The imagery is rendered from the game's own terrain, roads, buildings and trees. The default data has twice the native map resolution: 10 m per pixel instead of 20 m, so it stays sharper when you zoom in.

## Features

- Satellite imagery on the expanded map, the minimized HUD map and NuclearMinimap's cockpit copy
- Water fully transparent by default, with a soft coastline; optional water opacity shows the rendered sea
- Live tuning: overall opacity per map view, land/water opacity, brightness, contrast, saturation, gamma and tint
- Optional native map behind the imagery, visible through water and transparency
- Map icons, grid lines, labels and other map mods (trails, player names) stay above the imagery
- Imagery is chosen by the map's size and native image size; unknown maps keep the native map

## Requirements

- `Nuclear Option`
- `BepInEx 5`
- Satellite data files (`.nosat`), delivered separately. The mod does nothing without them.

## Installation

1. Install BepInEx for `Nuclear Option`.
2. Extract the `NuclearSatelliteMap` folder into `BepInEx/plugins/`.
3. Copy the `.nosat` data files into `BepInEx/NuclearSatelliteMap/`.

```text
BepInEx/
  plugins/
    NuclearSatelliteMap/
      NuclearSatelliteMap.dll
  NuclearSatelliteMap/
    heartland-46396.nosat
    ignus-archipelago-46395.nosat
```

The data folder is outside `plugins/`, so mod-manager reinstalls do not remove it. `BepInEx/plugins/NuclearSatelliteMap/maps/` also works, or set `Data Folder`.

## Configuration

Config file: `BepInEx/config/dev.dikka.nuclearsatellitemap.cfg`. All values apply while playing through Configuration Manager.

```ini
[General]
Enabled = true
Data Folder =

[Visibility]
Use On Expanded Map = true
Use On Minimized Map = true
## Overall opacity, applied immediately
Expanded Map Opacity = 1
Minimized Map Opacity = 1
## Applied to the texture (about a second for Ignus)
Land Opacity = 1
Water Opacity = 0
## Native map image behind the satellite layer
Native Map Opacity = 0

[Color]
## Applied to the texture
Brightness = 1
Contrast = 1
Saturation = 1
Gamma = 1
Tint = FFFFFFFF
```

Tuning tips:

- Icons hard to see: lower `Brightness` (for example 0.7) or `Saturation` (0.6).
- Minimized map hides too much of the view: lower `Minimized Map Opacity`.
- You want the sea back: raise `Water Opacity`, or raise `Native Map Opacity` to show the native map's sea.

Color and land/water settings re-read the data file and adjust it on a worker thread, then replace the texture. Slider drags are debounced. The adjustment changes only the two endpoint colors of each compressed 4x4 block. Brightness, contrast, saturation, tint and opacity are exact up to color quantization, as long as no color channel clips at black or white. Gamma, and any clipping, are applied to the endpoints, which is a close approximation. The order is gamma, contrast, brightness, saturation, then tint.

## Memory

The texture is BC3 (DXT5) compressed with a full mip chain: about 85 MiB of GPU memory for Heartland and 171 MiB for Ignus. Only the current map is loaded. Loading reads the file on a worker thread, then uploads it on the main thread. That upload can cause a short hitch, normally during mission loading.

## Producing data

`tools/build_satellite_data.py` builds the `.nosat` files from the separate `nuclear-option-map` satellite dataset and terrain geometry:

```bash
uv run --script tools/build_satellite_data.py --map all --preview
```

Water is classified from the game's terrain, road, building and pavement geometry at the game's sea level (Y = 0), with 4x4 supersampling for soft coastlines. Bridges and piers above the water stay opaque. Output goes to `dist/satellite-maps/`.

The data is derived from game assets. Keep it local: do not commit, package or publish it.

## Client testing

Offline logic and metadata checks do not establish in-game behavior. Imagery alignment, layering under icons, the minimized/cockpit maps and tuning behavior still require client acceptance.

## Local development install

```bash
./scripts/install-mod.sh -v
```

The install script copies only the plugin. Copy the data files to `BepInEx/NuclearSatelliteMap/` yourself.

## Building

```bash
dotnet build NuclearSatelliteMap.csproj -c Release
```
