# NuclearMfd source library

This library supplies scoped cockpit MFD surfaces to regular Nuclear Option client mods. It compiles into each consumer's plugin; it is neither a BepInEx plugin nor a separately installed dependency. Weapon data, fonts, colors, navigation/input and layout belong to the consumer.

Import it in the consuming project:

```xml
<Import Project="../Shared/NuclearMfd/NuclearMfd.props" />
```

The consuming project supplies `Assembly-CSharp`, `UnityEngine.CoreModule` and `UnityEngine.UI` references and nullable C# support. Ship [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) with binaries containing this source: the aircraft mapping is adapted from George Pied's MIT-licensed NO_Tactitools at `b4db2288c668dd55ae596fa46b15fce029170efd`.

Call on Unity's main thread, after `TacScreen.Initialize` completes, passing that exact screen and aircraft. No scene search or global screen cache is used.

```csharp
if (MfdSession.TryCreate(screen, aircraft, out var session, out var reason))
{
    if (session!.TryGetSurface("weapons", out var weapons))
    {
        var page = weapons!.Attach("my-plugin.weapons", replaceStock: true);
        // Put UI beneath page.Root, using the aircraft-specific layout.
        page.SetVisible(false); // Restores saved stock flags and layout settings.
        page.SetVisible(true);  // Takes a fresh snapshot and hides stock again.
    }
    if (session.TryGetSurface("main", out var main))
        main!.Attach("my-plugin.loadout"); // Additive canvas overlay.
    // Retain session; Dispose when disabled, changing aircraft, or unloading.
}
```

`Screen`, `Aircraft`, `AircraftName` and `CanvasRoot` identify the session. `TryGetSurface` resolves the built-in `weapons`, `main` and `radar` roles and retrieves previously registered custom roles. Surfaces expose `Path`, `IsRecognizedAircraft`, `IsFallback`, `IsCustomMapping`, `IsNativeMapping` and `MappingProvenance`. Missing paths return false without changing stock UI. Unknown names try the checked `SystemStatus` path and report fallback even if that path exists. Recognition means an exact supported registry name match, not runtime-verified compatibility. The main surface is the actual canvas, not another independent physical screen.

The built-in `radar` role follows the exact screen's native `radarCenter.parent`, validated as a live RectTransform beneath its Canvas. It resolves nested bomber/helicopter panels without guessing from aircraft names, and reports `IsNativeMapping=true`. Supplied assets contain fourteen TacScreen prefabs with this structure; their radar rectangles are 512-by-512 with different scales and ancestry. Prefer this region for normal tactical overlays: the whole canvas bottom-left can be outside the visible radar panel. New graphic objects must use the native surface's render layer; Unity does not inherit GameObject layers on parenting.

Consumers can resolve additional aircraft-specific regions using exact paths beneath this session's canvas, without modifying the library's built-in aircraft registry:

```csharp
if (session.TryGetSurfaceAtPath("my-plugin.engine", "RightScreenBorder/EnginePanel", out var engine))
{
    var enginePage = engine!.Attach("my-plugin.engine-page");
    // Add aircraft-specific UI beneath enginePage.Root.
}
```

Custom roles use ASCII letters, digits, `.`, `-` and `_`; empty names, `.`/`..` and the reserved `main`/`weapons`/`radar` names are rejected. Paths must be nonempty canvas-relative paths using `/`, with no absolute prefix, backslash, colon, control characters, empty segments, `.`/`..` segments or padded segments. Interior spaces in actual object names are allowed. Resolution checks a live descendant `RectTransform`; invalid or absent paths return false without changing UI. A successfully registered role keeps its exact path for the session: attempting to rebind it to another path returns false. Custom surfaces report `IsCustomMapping=true`, consumer-supplied provenance, and neither built-in recognition nor fallback. Callers own the aircraft-specific choice and runtime acceptance of these paths.

Aircraft identification uses `aircraft.definition.aircraftParameters.aircraftName`, the same definition-owned field returned by the current client's `GetAircraftParameters()` and used by upstream. `MfdLayouts.GetCanonicalName` maps the exact current stock names `UH-90 Ibis`, `EW-25 Medusa` and `SFB-81 Darkreach` onto the existing `UH-80 Ibis`, `EW-1 Medusa` and `SFB-81` adapter names. All other names remain unchanged. No definition-display-name fallback, prefab instance name, `(Clone)` suffix, livery label, player-facing `NetworkunitName`, fuzzy matching or invented alias grants recognition.

Session `AircraftName` is the canonical layout name. `AircraftParametersName` and `AircraftDefinitionName` expose the raw parameter and definition display strings; `AircraftNameSource` identifies the definition-owned parameter field. `AircraftDefinitionKey` exposes the stable mission lookup `jsonKey`, for bounded diagnostics; that key never grants recognition. A logged prefab identity such as `VTOLTrainer1` alone does not establish the actual registry name or a safe surface adapter.

Page roots use the parent's local origin, rotation identity and scale one, with parent-pivot anchors. They start at the parent's current rect size and ignore layout groups; there is no imposed artboard scaling. Consumers own adaptive positioning and resizing. Native object references can become Unity-null after destruction; check them before rendering and dispose the session promptly. Disposal is idempotent and safe when native objects have disappeared.

A visible replacement snapshots direct stock child `activeSelf` flags and parent `LayoutGroup`/`ContentSizeFitter.enabled` flags, then disables them. `Attach(id, true, hideStockDecoration: true)` additionally snapshots and disables the parent's `Image.enabled` flag; the default preserves it. Hiding/disposal restores exact snapshots. Already inactive children remain inactive. Content created after the snapshot is not hidden. While replacing, changes made by other code to captured stock flags are overwritten on restore; avoid competing owners. The library never destroys stock objects and owns only its page roots.

Duplicate IDs on one surface and simultaneous overlapping replacements throw `InvalidOperationException`. All sessions in the same compiled source copy share replacement ownership and exclude that copy's owned pages from stock snapshots. Independent plugin assemblies embed independent copies: there is no cross-plugin arbitration. Consumers must dispose pages; sessions do not auto-detect lifecycle changes or install hooks. A replacement on `main` can hide ancestor stock containing other surfaces, so prefer additive main overlays.

## Mapping coverage

The upstream weapons recognition switch has 18 exact names, including three Alkyon aliases and names outside the current stock roster. Ten names have special destination paths; the remaining eight use upstream's default `SystemStatus`, explicitly recorded here. All 18 are retained in this registry. Unknown names are not marked recognized.

The [official Steam store roster](https://store.steampowered.com/app/2168680/Nuclear_Option/?l=english), checked on 2026-10-08, lists thirteen stock aircraft: Cricket, Compass, Vagrant, Ibis, Chicane, Brawler, Revoker, Vortex, Tarantula, Ifrit, Medusa, Darkreach and Alkyon. Tests contain all thirteen current full names, separately from the upstream extra names and old aliases. `VT-7 Vagrant` uses the checked `SystemStatus` adapter; panel resolution alone does not establish acceptance of custom rendering. The three current-name aliases above preserve the corresponding existing aircraft adapters. Current stock names plus the retained upstream names do not imply every panel path has been verified in-game.

| Names | Weapons destination |
| --- | --- |
| EW-1 Medusa | engPanel1 |
| CI-22 Cricket | EngPanel |
| SAH-46 Chicane | BasicFlightInstrument |
| VL-49 Tarantula | RightScreenBorder/WeaponPanel |
| SFB-81 | weaponPanel |
| FastBomber1, AB-4 Alkyon, Alkyon AB-4 | weaponPanel/frontProfile |
| MiG-15 | StatusGauges/FrontView |
| F-16M King Viper | SystemsPanel |
| T/A-30 Compass, FS-3 Ternion, FS-12 Revoker, FS-20 Vortex, KR-67 Ifrit, UH-80 Ibis, A-19 Brawler, FQ-106 Kestrel | SystemStatus |
| VT-7 Vagrant | SystemStatus |

Upstream's weapon visual-layout switch omits explicit cases for FS-3 Ternion, FQ-106 Kestrel, MiG-15 and F-16M King Viper despite recognizing them; their visuals use defaults. That is a layout gap, separate from successful surface resolution. Per-aircraft runtime prefab and rendering acceptance remains necessary. Assembly inspection proves the `TacScreen` hooks/fields, not these prefab paths.

Run the 27 pure registry, 13 current stock roster and 31 custom path validation checks with `dotnet run --project Shared/NuclearMfd/tests/LayoutChecks.csproj` using the workspace SDK. The checks cover every current stock aircraft name, retained aliases, canonical adapter identity and rejection of prefab/clone/livery/player labels as guessed aliases. Build a consuming mod against actual client references for API validation. These checks do not test Unity layout, stock restoration or in-game rendering.
