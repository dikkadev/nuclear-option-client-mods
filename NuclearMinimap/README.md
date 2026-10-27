![NuclearMinimap](docs/assets/cover.png)

# NuclearMinimap

Adds orientation and hold zoom to the native HUD minimap, plus an optional cockpit display of that same live minimap. Terrain, native icons, grid and enabled map labels stay native. Target and landing camera feeds remain available, and NuclearDelivery's cockpit counters stay above the cockpit map.

**Display / Show Cockpit Minimap** defaults to **on**. Turn it off to keep the stock cockpit radar and HUD placement while continuing to use minimap orientation and hold zoom. The HUD visibility choices below apply only while this cockpit feature is enabled.

In the F1 configuration menu, **Display / HUD Minimap** offers:

- **HideWhenMfdVisible** (default): hide the HUD minimap, its panel backing and small heading-reference dot while the cockpit map is displayed; show them again during camera feeds or if the cockpit map is unavailable.
- **AlwaysShow**: keep the HUD minimap alongside the cockpit map.
- **AlwaysHide**: hide the minimized HUD map, its panel backing and heading-reference dot.

The expanded map remains available in every mode. Disable **General / Enabled** to restore stock radar, HUD presentation, orientation and zoom controls.

**Display / Minimap Orientation** selects **NorthUp** (default) or **HeadingUp** (rotates with your aircraft). Both the HUD and MFD use the same orientation and zoom, centered on your aircraft even at high zoom. Existing saved orientation choices are preserved.

## Flight instruments on the cockpit minimap

The cockpit minimap also shows small flight instruments, so it can work as a moving-map display. Each one has its own toggle under **Flight Instruments** in the F1 menu:

- **Show Heading Tape** (on): a compass tape along the top, with the heading as a three-digit number in the centre box.
- **Show North Indicator** (on): a north arrow in the top-left corner. It appears only with **HeadingUp** orientation, because north is always up in NorthUp.
- **Show Attitude Indicator** (on): a small attitude ball (artificial horizon) with sky and ground, pitch lines every 10 degrees, a fixed bank scale (10, 20, 30, 45 and 60 degrees), a yellow bank pointer and a fixed aircraft symbol.
- **Show Bank Angle** (on): the bank angle beside the ball, for example **R30** (right wing down) or **L15**.
- **Attitude Indicator Corner** (BottomRight), **Attitude Indicator Size** (0.3 of the map's shorter side) and **Instrument Opacity** (0.9).

The bottom-left corner holds NuclearDelivery's counters, so it is not the default. A top-corner ball moves below the heading tape if the two would overlap. The instruments use the HUD's green theme colour and font, and appear only while the cockpit minimap is shown. The HUD minimap is unchanged.

## Zoom controls

Under **Controls / Device Zoom Bindings** in the F1 Configuration Manager menu:

1. Select a connected device.
2. Select **Add zoom in** or **Add zoom out**.
3. Let its axes settle. Then press a key/button, use a hat direction, or move a joystick axis past halfway in the required direction. Maintained switches can stay on. A button already held when capture starts, or pressed before capture is ready, must be released and pressed again.

You can save multiple bindings on each device and use several devices together, including a stick and throttle. Keyboard keys, mouse buttons, and any joystick buttons, hats and signed axes exposed by Rewired are available. Relative mouse movement and wheel pulses are excluded because they cannot express a held input. Axis zoom uses a fixed halfway threshold; it does not scale the zoom rate with deflection. Recenter an already deflected axis before moving it in that same direction for capture. A trigger that rests at an endpoint must move past the midpoint.

Each row shows its device, control and zoom direction. **Remove** deletes that row. Disconnected rows stay saved. Devices use Rewired's persistent device GUID when available. Without one, only a unique hardware match is accepted; indistinguishable devices remain inactive. Capture cancels after 15 seconds, on disconnect, or when the binding menu closes or loses focus.

Custom rows replace the older zoom defaults **on that device only**. Remove all its custom rows to restore those defaults. Existing saved settings remain unchanged: **Hold Zoom In / Out** accept keyboard shortcuts and start unbound; **Controller Hold Zoom In / Out** default to **DPadUp / DPadDown**. Assigned gamepads use their named Rewired template controls, with D-pad/POV support on other assigned joysticks.

The zoom range extends eight times farther out and 60% closer in than earlier versions. Hold past **Zoom Hold Delay** (default **0.3 seconds**) to zoom continuously; release stops immediately. Opposing directions cancel. **Zoom Speed** adjusts the rate.

These controls work on the minimized map whether shown in the HUD, MFD, or both. They pause during menus, text entry, the F1 configuration window and loss of focus. The expanded map keeps its native orientation and controls; returning to the minimap restores your chosen zoom.

**Controls / Consume Zoom Bindings** defaults to **on**: a **short tap** triggers the normal game binding on release; a **long hold** zooms without triggering that binding, including when released. For example, tap D-pad down for gear and hold it for zoom out. Normal taps wait until release so the mod can distinguish them from holds. **Zoom Hold Delay** is adjustable from 0.1 to 1 second. Other controls bound to the same action still work. Keyboard chords reserve their main key during the press; modifiers remain available. Turn consumption off to allow the native action during zoom holds too. Saved game assignments are preserved, and menus and the expanded map use their normal bindings.

For custom axis directions, consumption applies only to native **button actions** mapped to that same axis direction. Full-axis and analog actions remain active while zooming; the editor marks these shared axes. For example, binding a flight-stick axis to zoom also leaves its flight control active. A device without a native game assignment can still zoom, but has no native tap action to replay. Learning controls happens in the configuration menu; its normal game input rules still apply.

The mod uses the aircraft's native radar surface, including nested cockpit layouts, rather than a guessed aircraft name. It mirrors native UI graphics without cloning map controllers, adding a camera, or allocating a map render texture. No other mod is required; the MFD helper is compiled into this plugin as source. Existing map-label settings still decide which labels appear.

Install with NOMM Import Modpack or place `NuclearMinimap.dll` in a BepInEx 5 plugins folder. Configuration is generated as `dev.dikka.nuclearminimap.cfg` after loading.

This replaces NuclearMfdMap. Remove or disable its old DLL before loading NuclearMinimap; the plugins are declared incompatible to prevent duplicate map/input patches. On first load, all saved values from `dev.dikka.nuclearmfdmap.cfg` migrate if the new configuration is absent or contains only headers/comments/sections. Any saved setting in the new configuration prevents import; existing new preferences always win. The original configuration is retained unchanged.
