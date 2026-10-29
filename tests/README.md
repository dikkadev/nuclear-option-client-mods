# Client logic regression checks

The source-snapshot/release CLI also has offline regression checks, including real
temporary Git histories, version discovery, source and bundle integrity, and
interrupted GitHub publication simulated without external writes:

```bash
uv run --with py7zr==1.1.3 python -B -m unittest discover -s tests -p 'test_*.py'
```

These checks do not publish anything or establish game behavior. File-symlink
coverage may be skipped when Windows does not grant symlink creation; the real
directory-junction escape check still runs there.

Map controls include seven linked-source cases for hold/release/cancellation, zoom limits, rate invariance, resume delta bounds, invalid inputs and heading/north orientation. The combined logic runner includes controller double presses, minimap controls, targeting, delivery and formatting checks. Physical gamepad input and map lifecycle still require client testing.

`ClientLogic.Tests` is a dependency-free .NET 10 console assertion runner. It links the actual mod source files for HUD unit formatting, track age text policy and fuel-time estimation. It exercises explicit conversions, secondary-speed suppression, track eligibility and time boundaries, annotation cleanup/idempotence, and valid/invalid fuel samples.

From the repository root in PowerShell:

```powershell
. ../Open-Workspace.ps1
dotnet run --project tests/ClientLogic.Tests -c Release
```

The parent workspace pins the SDK. No game installation or mod installation is needed. Failure returns a nonzero exit code.

SatelliteMapChecks links the production SatelliteBlocks and SatelliteMapFile sources. It covers BC3 full-mip-chain sizes and synthetic version 1 (uncompressed), version 2 (deflate) and version 3 (tiled) headers and payloads, Half-detail mip offsets, including every rejection path and short/overlong inflated data. No real `.nosat` imagery is read or generated. These checks do not validate Unity texture upload, visual quality or game lifecycle behavior.

`GameBoundaryStubs.cs` supplies small test-only game/Unity boundaries so the real calculations can execute without proprietary client assemblies. Passing these checks does **not** verify game API compatibility, Harmony hooks, Unity UI/layout/lifecycle behavior, audio, networking, or runtime performance. Those require builds against matching client references and an authorized game smoke test.

After building the fourteen active plugins, run [ClientApiCheck](ClientApiCheck/README.md) against real client metadata. It checks declared Harmony targets, compiled typed field references and the explicit reflection-field contract, without executing the game. Use [SMOKE-TESTS.md](SMOKE-TESTS.md) for the remaining runtime checks.

## Modpack producer checks

The Windows launcher is `./tools/Build-Modpack.ps1`. Process checks cover Windows-only console suppression, direct test DLL invocation, captured stdout/stderr and nonzero native exit propagation. The real build validates these launch paths; it does not replace game acceptance tests.

The source-only MFD registry and custom path validator have their own engine-independent checks:

```powershell
dotnet run --project Shared/NuclearMfd/tests/LayoutChecks.csproj -c Release
```

These check all upstream recognized names, safe fallback metadata and custom-region path validation. They do not test native Unity object lifetime, prefab paths or cockpit rendering. The workspace Test-Mods wrapper and modpack builder run them alongside the existing logic checks.

```bash
uv run --python 3.11 python -m unittest discover -s tests -p test_build_modpack.py
uv run --python 3.11 --with py7zr==1.1.3 python -m unittest discover -s tests -p "test_*modpack*.py"
```

Fixture tests cover NOMM layout/metadata, DLL/media selection, retired-mod rejection, invalid ID/version rejection, build failure and missing-payload preservation, environment handling, and truthful hash provenance. Full-pack tests add source/pin/dependency checks, archive path/type/size/collision safety, media/notices retention, changed registry independence, cached hash rejection, and preservation of both previous outputs on staging failure. Refresh tests cover origins, URL normalization/redirects, download bounds, hashes, dependencies/extends and preservation of the old lock on failure. They do not launch NOMM or the game. `uv run --script tools/build-modpack.py` builds and runs all five .NET verification programs once before producing both packs; use that default command for a new candidate.

## Configuration migration checks

`Shared/NuclearModMigration/tests/MigrationChecks.csproj` uses the real BepInEx ConfigFile against temporary files. It checks header-only first creation, typed legacy imports, existing destination preservation, missing source defaults, transient Audio Test exclusion and byte-for-byte preservation of the legacy file. Run after loading the workspace SDK/reference environment:

```powershell
dotnet run --project Shared/NuclearModMigration/tests/MigrationChecks.csproj -c Release
```

Fuel-time checks now link NuclearSounds production source. Offline migration checks do not validate the optional Targeting/Sounds event bridge in a game.

### Delivery counters

NuclearDelivery 0.1.2 (`dev.dikka.nucleardelivery`) adds independent missile/bomb tracking and additive displays. Defaults enable both camera views (NOTT vertical M-left/B-right counters above bottom readouts) and cockpit radar rows (horizontal, bottom-left, sized to match the camera). Zero statuses and empty categories are hidden. Yellow means in flight, green means reported `hitArmor` contact, and red means no reported armor contact, removal or flight expiry; green does not verify damage or destruction. Completed results remain for three seconds and flight tracking expires after 120 seconds by default. Master, target-camera, landing-camera and cockpit toggles, sizes and offsets are configurable. Tracking does not depend on target selection or camera availability.

The target overlay owns children under the exact aircraft's lazily created TargetScreenUI; the cockpit overlay uses the source-only NuclearMfd radar surface without replacing stock children. Keep it separate from the weapons replacement and loadout popup, preserve radar behavior, and clean up on aircraft/scene changes. The user accepted the Targeting/Sounds extraction and target-camera counters; normal radar rendering now works but was too small; size matching and landing counters still require client acceptance. Build and metadata checks do not establish delivery game behavior.

NuclearMinimap policy checks cover minimized HUD presentation independently of expanded-map availability. ClientApiCheck includes its standalone source-only MFD contract. Rendering, source refresh, native stock restoration and coexistence with Delivery/Weapons require the client checks below; runtime acceptance is pending.


ControllerGestureChecks links the pure production DoublePressGesture: 15 grouped sequence cases cover second-release firing, exact timing limits, late second release after timely down, slow-tap restart, long presses, neutral/release gates, triple bursts, cooldown, focus cancellation, invalid/backwards clocks, invalid settings and physical-control isolation. Controller actions and native replay still require client acceptance.

NuclearMinimap/tests/MinimapMigrationChecks.csproj checks legacy configuration migration and destination preservation with real BepInEx configuration files; the producer runs it alongside the four existing verification programs.
