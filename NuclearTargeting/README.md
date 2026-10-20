# NuclearTargeting

A client-side BepInEx 5 mod for Nuclear Option that provides the alternative HUD target selector formerly included in NuclearTacticalTools.

Quick target-select taps add or refresh the highest-priority eligible target near the designator in a bounded selection buffer. The newest target is first; a new addition removes the oldest selections when necessary. Tapping an already selected target moves it to newest without duplicating it. Holding the game's Select input still adds an eligible unselected target on each stock painting invocation, without the tap cap. The default selection radius is 200 HUD-space units; optional cycling retains the game's priority order.

The default tap cap is the aircraft's actual laser-designation capacity, read from its native designator rather than an aircraft-name table. In the supplied aircraft data, Brawler has three slots, Chicane four, and VT-7 Vagrant one. A missing or zero-capacity designator falls back to one. A fixed override can use the same number for every aircraft. Changing the override does not remove selections immediately; the next eligible tap enforces the cap, including selections previously added by holding. Tapping with no eligible target leaves the list unchanged. Disabling **Enable Alternative Targeting** restores stock selection and disables the mode shortcut.

This selector is separate from NOTT's target-list sorting, recall and MFD navigation features.

## Installation

Install the NuclearTargeting plugin through its modpack or place `NuclearTargeting.dll` in `BepInEx/plugins/NuclearTargeting/` with BepInEx 5 already installed. Disable or remove the retired NuclearTacticalTools plugin first: the plugins declare an incompatibility to prevent duplicate targeting overrides.

NuclearSounds is optional and supplies designation tones and other audio features separately. NuclearTargeting does not require it.

## Settings

Settings are generated in `BepInEx/config/dev.dikka.nucleartargeting.cfg`. Matching settings from the retired Tactical Tools mod import only into a new configuration; existing destination settings take precedence and the old file is preserved. Upgrading from Targeting 0.1.0 converts an active fixed cap into the new fixed override. The inactive factory placeholder of two becomes one; custom fixed values remain. The old disabled/per-aircraft cap modes and reselect switch are replaced by normal capped taps with native capacity. Explicit new override settings take precedence.

- Enable Alternative Targeting: enabled by default.
- Next Target Order: disabled by default.
- Selection Radius: 200 by default.
- Use Fixed Tap Target Cap: disabled by default; enable to override native capacity on every aircraft.
- Fixed Tap Target Cap: 1 by default, configurable from 1 to the native synchronization limit of 128.
- Toggle Fixed Tap Target Cap: keyboard shortcut, unbound by default. Switches between native capacity and the fixed override, updating the same saved setting. Ignored in menus, text entry, outside a valid aircraft and without game focus. A shortcut shared with a stock game action does not consume that action.

The settings menu shows whether the fixed override is active. This mod adds no HUD indicator and requires no other mod or HUD integration framework.

The selector uses the game's visible markers, exclusion settings and priority assessment. Its painting eligibility differs from stock painting: it does not add the stock enemy-faction and weapon-opportunity restrictions. Supplied-client API inspection supports the hooks; that inspection alone does not establish tested game behavior.
