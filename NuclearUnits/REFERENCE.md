# NuclearUnits Reference

## Goal So Far

Based on `README.md` in this folder:

- Let the player choose preferred units independently per measurement.
- At minimum, support metric vs imperial.
- For speed, support `mph`, `km/h`, and `kts` separately.
- Likely targets include at least airspeed and altitude, and possibly every displayed measurement.
- Possible future expansion: show a secondary unit at the same time, with a smaller secondary readout.

## Existing Mod Notes (`../NuclearVOIP`)

- The existing mod is a BepInEx plugin written in C#.
- It targets `netstandard2.1` and references the game's local managed assemblies directly from `NuclearOption_Data/Managed`.
- It already uses BepInEx config entries, including `KeyboardShortcut`.
- It vendors a small `AtomicFramework` helper layer that uses Harmony runtime patches to hook game lifecycle events.
- `AtomicFramework/LoadingManager.cs` shows the current pattern for patching game methods with Harmony and then reacting once the game/network/mission is ready.

Practical implication for `NuclearUnits`:

- We will probably want the same basic project shape: a BepInEx C# plugin that references local game DLLs.
- For a units/UI mod, we will likely need Harmony patches unless the game already exposes unit preferences through a clean API.
- The first technical task later will be finding the game's measurement formatting code and/or HUD text update code.

## Likely Useful Docs

### BepInEx

- Basic plugin tutorial: https://docs.bepinex.dev/articles/dev_guide/plugin_tutorial/index.html
- Runtime patching overview: https://docs.bepinex.dev/articles/dev_guide/runtime_patching.html
- Useful modding/dev tools: https://docs.bepinex.dev/articles/dev_guide/dev_tools.html
- Troubleshooting: https://docs.bepinex.dev/articles/user_guide/troubleshooting.html
- BepInEx install guide for users: https://docs.bepinex.dev/articles/user_guide/installation/index.html

### Harmony / HarmonyX

- Harmony introduction and patching concepts: https://harmony.pardeike.net/articles/intro.html
- HarmonyX wiki home: https://github.com/BepInEx/HarmonyX/wiki

Why this matters:

- A per-measurement units mod will probably need to patch one or more formatting/display methods rather than just adding a standalone component.

### Config UI / Player Settings

- BepInEx ConfigurationManager: https://github.com/BepInEx/BepInEx.ConfigurationManager

Why this matters:

- If installed, it can expose unit preferences in-game without us building a custom settings UI first.

### Inspecting The Game At Runtime

- Runtime Unity Editor: https://github.com/ManlyMarco/RuntimeUnityEditor
- UnityExplorer: https://github.com/sinai-dev/UnityExplorer

Why this matters:

- These tools help inspect live UI objects, HUD hierarchies, components, and text fields so we can find where unit labels and formatted values are coming from.

Note:

- `UnityExplorer` is archived, but it is still commonly useful as a reference/tool for Mono Unity games.

### Decompiling / Browsing Game Assemblies

- dnSpyEx: https://github.com/dnSpyEx/dnSpy

Why this matters:

- We will almost certainly need to inspect `Assembly-CSharp.dll` to locate measurement enums, formatting helpers, HUD scripts, or settings code.

## Expected Investigation Path Later

1. Confirm whether `Nuclear Option` is Mono + BepInEx 5 for this install path and current mod setup.
2. Inspect `Assembly-CSharp.dll` for unit/measurement formatting code.
3. Identify whether the game already has a global units setting and where it is applied.
4. Decide whether to patch formatting functions, HUD presenters, or both.
5. Add config entries for each measurement category.
6. If needed, patch UI labels as well as numeric conversions.

## Early Implementation Guess

The cleanest first version is probably:

- independent config entries for categories like speed and altitude,
- patch the game's existing value-to-string/unit-label formatting path,
- reuse the game's own update flow instead of building a custom HUD.

The dual-unit display idea is probably a second phase, because it may require layout or text composition changes instead of only swapping conversion/label logic.
