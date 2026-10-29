# Client API metadata preflight

After building all fourteen active mods in Release against the current client references:

```powershell
. ../Open-Workspace.ps1
../Test-Mods.ps1 -Build
```

Run from the repository root. Optional program arguments are repository root, managed-reference directory, and field-contract JSON path. `NO_MANAGED_DIR` supplies the default managed directory. The checker directly references `Mono.Cecil.dll` from `NO_BEPINEX_CORE_DIR` (or the `BepInExCoreDir` build property). No NuGet package, game installation, or game execution is required. It reads assembly metadata and plugin IL without executing either assembly.

Checks:

- Every active Release DLL exists. Harmony-based mods declare targets; NuclearSatelliteMap is explicitly checked as an independent overlay without Harmony references, registrations or targets.
- The compiled `BepInPlugin` ID/version match the corresponding project properties, so stale DLLs cannot pass under a newer candidate's metadata.
- Every Harmony-based plugin bootstrap passes an explicit assembly to `Harmony.PatchAll`; parameterless registration depends on a stack frame and can silently select the wrong assembly when inlined.
- Sounds' anti-radiation warning key matches the string actually returned by the current client's `ARMSeeker.GetSeekerType`. Unsupported getter IL fails explicitly; this guards a compile-invisible seeker-name change.
- Class-level `HarmonyPatch` declarations resolve to one target method in the current game assembly or the explicitly supported Rewired_Core client dependency, including inherited methods. Rewired targets cover temporary map-input reservation save/copy guards. Supported constructor arguments are declaring type, method name, explicit argument types, and normal/getter/setter/constructor/static-constructor method kinds. Missing targets and unspecified ambiguous overloads fail. Unsupported declarations fail explicitly.
- Compiled literal `AccessTools.FieldRefAccess<T,F>(string)` calls use the exact field type the current game declares. Unsupported call forms fail explicitly.
- The explicit `tests/api-contract.json` records private field names and types used by active `Traverse` and field-reference code. Update that contract deliberately when reflection use changes; the checker does not automatically discover every arbitrary reflection expression.
- NuclearSatelliteMap's native map contracts cover `SceneSingleton<DynamicMap>.i`, `DynamicMap.mapImage` and the static `mapMaximized` getter. Unity metadata checks resolve the exact `Texture2D(int,int,TextureFormat,int,bool)`, `LoadRawTextureData(IntPtr,int)` and `Apply(bool,bool)` overloads without creating a texture.

Failure returns exit code 1. Rebuild the mods first: this tool inspects the existing DLLs, not whether they reflect current source. It does not check Harmony parameter injection, arbitrary reflection, public API calls beyond the explicit contracts above, method behavior, Unity UI/lifecycle/audio, timing, or runtime performance. A successful build and preflight still require an authorized client smoke test.

Negative checks can use an alternate scratch JSON contract: change a field name to a nonexistent name or its type to `System.String`, then pass that file as argument 3. The checker must report the mismatch and exit 1. Keep scratch contracts and results in ignored `.dev/tmp/`.

The active list includes NuclearTargeting and NuclearSounds and excludes the archived combined NuclearTacticalTools. The anti-radiation dispatch check targets NuclearSounds. Configuration migration uses a separate real-ConfigFile test runner; the optional designation event bridge still requires the runtime checks in SMOKE-TESTS.md.

### Delivery counters

NuclearDelivery 0.1.0 (`dev.dikka.nucleardelivery`) adds independent missile/bomb tracking and additive displays. Defaults enable both the target-camera display (NOTT vertical 36-by-20 native-coordinate blocks, M left/B right above the bearing) and cockpit MFD display (bottom-left, two horizontal M/B rows). Zero statuses and empty categories are hidden. Yellow means in flight, green means reported `hitArmor` contact, and red means no reported armor contact, removal or flight expiry; green does not verify damage or destruction. Completed results remain for three seconds and flight tracking expires after 120 seconds by default. Master, target-camera and cockpit toggles, sizes and offsets are configurable. Tracking does not depend on target selection or camera availability.

The target overlay owns children under the exact aircraft's lazily created TargetScreenUI; the cockpit overlay uses the source-only NuclearMfd main canvas without replacing stock children. Keep it separate from the weapons replacement and loadout popup, preserve radar behavior, and clean up on aircraft/scene changes. The user confirmed the Targeting/Sounds extraction works nicely; delivery rendering and outcomes still require client acceptance. Build and metadata checks do not establish delivery game behavior.

Minimap and ControllerGestures native replay share checks of the ReInput runtime UserData accessor, the private custom-definition list type and unique private PlayerSaveData custom-map array. Their GetAllMapSaveData(bool) selectors explicitly exclude the colliding generic overload; the checker validates each exact owned selector's bounded compiled contract and native target. Other dynamic target selectors remain unsupported. Native replay, persistence and physical input still need client acceptance.
