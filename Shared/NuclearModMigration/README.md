# Focused configuration migration

Import `NuclearModMigration.props` to compile the helper into a plugin; there is no runtime helper DLL. Construct `LegacyConfig` before binding any destination keys and use its `Bind` method for persisted preferences. Transient actions use `Config.Bind` directly.

An absent or header/comment/section-only destination imports matching typed values from `dev.dikka.nucleartacticaltools.cfg`. Any saved destination key protects the whole configuration from reimport. The original file is read without saving, and factory reset defaults remain the plugin's declared defaults.

Run `dotnet run --project Shared/NuclearModMigration/tests/MigrationChecks.csproj -c Release` with the workspace reference environment loaded to verify against the actual BepInEx configuration implementation.
