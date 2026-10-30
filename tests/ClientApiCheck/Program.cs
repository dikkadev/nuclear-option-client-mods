using Mono.Cecil;
using System.Text.Json;
using System.Xml.Linq;

// Metadata only: never load or execute a game/plugin assembly in the CLR.
string root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
string managed = args.Length > 1 ? args[1] : Environment.GetEnvironmentVariable("NO_MANAGED_DIR") ?? "";
string contractPath = args.Length > 2 ? args[2] : Path.Combine(root, "tests", "api-contract.json");
int passed = 0, failed = 0, patches = 0, fields = 0;
void Check(string name, Action action)
{
    try { action(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
}
void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
{
    foreach (var type in types) { yield return type; foreach (var child in AllTypes(type.NestedTypes)) yield return child; }
}
if (!Directory.Exists(managed)) { Console.Error.WriteLine("FAIL Set NO_MANAGED_DIR or pass the managed directory as argument 2."); return 1; }
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(managed);
var core = Environment.GetEnvironmentVariable("NO_BEPINEX_CORE_DIR");
if (!string.IsNullOrEmpty(core)) resolver.AddSearchDirectory(core);
using var game = ModuleDefinition.ReadModule(Path.Combine(managed, "Assembly-CSharp.dll"), new ReaderParameters { AssemblyResolver = resolver });
var gameTypes = AllTypes(game.Types).ToDictionary(t => t.FullName);
foreach (var (typeName, methodName, returnType) in new[] {
    ("Aircraft", "GetLaserDesignator", "LaserDesignator"),
    ("LaserDesignator", "GetMaxTargets", "System.Int32"),
    ("WeaponManager", "TargetListChanged", "System.Void") })
{
    Check("Targeting native " + typeName + "." + methodName, () => {
        var method = gameTypes[typeName].Methods.Single(m => m.Name == methodName && m.Parameters.Count == 0);
        Require(method.IsPublic && !method.IsStatic && method.ReturnType.FullName == returnType,
            "Native laser capacity or target-order notification API changed.");
    });
}
// Spectator rotor warm start replays these private initializers after the first network position.
foreach (var (typeName, methodName) in new[] {
    ("RotorShaft", "RotorShaft_OnSpawnedInPosition"),
    ("ConstantSpeedProp", "ConstantSpeedProp_OnInitialize") })
{
    Check("Spectator warm start " + typeName + "." + methodName, () => {
        var method = gameTypes[typeName].Methods.Single(m => m.Name == methodName && m.Parameters.Count == 0);
        Require(!method.IsStatic && method.ReturnType.FullName == "System.Void", "Native rotor/propeller start initializer changed.");
    });
}
// Spectator HUD transpilers rewrite exactly these native local-aircraft reads.
foreach (var (typeName, methodName, localCall) in new[] {
    ("HUDAppManager", "Start", false), ("MFDAppManager", "Start", false), ("ArtificialHorizon", "Refresh", false),
    ("EngineTelemetry", "Start", false), ("RadarWarning", "Start", false), ("HeadMountedDisplay", "Start", false),
    ("HeadMountedDisplay", "Update", false), ("PylonIndicator", "Start", true) })
{
    Check("Spectator HUD read " + typeName + "." + methodName, () => {
        var method = gameTypes[typeName].Methods.Single(m => m.Name == methodName && m.Parameters.Count == 0 && m.HasBody);
        Require(method.Body.Instructions.Any(i => localCall
            ? i.OpCode == Mono.Cecil.Cil.OpCodes.Call && i.Operand is MethodReference call && call.DeclaringType.FullName == "GameManager" && call.Name == "GetLocalAircraft"
            : i.OpCode == Mono.Cecil.Cil.OpCodes.Ldfld && i.Operand is FieldReference field && field.DeclaringType.FullName == "CombatHUD" && field.Name == "aircraft"),
            "The native local-aircraft read moved; the spectator rewrite would no longer apply.");
    });
}
Check("Spectator TacScreen target-camera subscription", () => {
    var method = gameTypes["TacScreen"].Methods.Single(m => m.Name == "Initialize" && m.HasBody);
    Require(method.Body.Instructions.Any(i => i.Operand is MethodReference call && call.DeclaringType.FullName == "TargetCam" && call.Name == "add_onCamToggle"),
        "TacScreen.Initialize no longer subscribes to TargetCam.onCamToggle; review the spectator guard.");
});
// Input reservation save guards patch the client's Rewired dependency as well.
using var rewired = ModuleDefinition.ReadModule(Path.Combine(managed, "Rewired_Core.dll"), new ReaderParameters { AssemblyResolver = resolver });
var rewiredTypes = AllTypes(rewired.Types).ToDictionary(t => t.FullName);
Check("Minimap/ControllerGestures runtime Rewired UserData accessor", () =>
{
    var accessor = rewiredTypes["Rewired.ReInput"].Properties.Single(p => p.Name == "UserData");
    Require(accessor.PropertyType.FullName == "Rewired.Data.UserData" && accessor.GetMethod is { IsStatic: true, IsPublic: false }, "Runtime UserData accessor contract changed.");
});
Check("Minimap/ControllerGestures temporary Rewired definitions", () =>
{
    fields++;
    var definitions = rewiredTypes["Rewired.Data.UserData"].Fields.Single(f => f.Name == "customControllers");
    Require(definitions.IsPrivate && definitions.FieldType.FullName == "System.Collections.Generic.List`1<Rewired.Data.CustomController_Editor>", "Runtime custom-controller definitions contract changed.");
});
Check("Minimap/ControllerGestures Rewired saved-controller isolation", () =>
{
    fields++;
    var saved = rewiredTypes["Rewired.PlayerSaveData"].Fields.Where(f => f.IsPrivate && f.FieldType.FullName == "Rewired.CustomControllerMapSaveData[]").ToArray();
    Require(saved.Length == 1, "Expected one private custom-map save array for transient-controller exclusion.");
});
IEnumerable<TypeDefinition> Hierarchy(TypeDefinition type)
{
    for (TypeDefinition? current = type; current != null;)
    {
        yield return current;
        if (current.BaseType == null) yield break;
        current = current.BaseType.Resolve();
    }
}
Check("SatelliteMap SceneSingleton<DynamicMap>.i", () =>
{
    var singleton = gameTypes["SceneSingleton`1"];
    var instance = singleton.Fields.Single(f => f.Name == "i");
    Require(instance.FieldType.FullName == "T" && instance.IsPublic && instance.IsStatic,
        "Scene singleton instance field contract changed.");
    Require(gameTypes["DynamicMap"].BaseType.FullName == "SceneSingleton`1<DynamicMap>",
        "DynamicMap no longer inherits SceneSingleton<DynamicMap>.");
});
Check("SatelliteMap DynamicMap.mapMaximized", () =>
{
    var property = gameTypes["DynamicMap"].Properties.Single(p => p.Name == "mapMaximized");
    Require(property.PropertyType.FullName == "System.Boolean" && property.GetMethod is { IsPublic: true, IsStatic: true },
        "Expanded-map accessor contract changed.");
});
using var unity = ModuleDefinition.ReadModule(Path.Combine(managed, "UnityEngine.CoreModule.dll"), new ReaderParameters { AssemblyResolver = resolver });
var textureType = AllTypes(unity.Types).Single(t => t.FullName == "UnityEngine.Texture2D");
foreach (var (name, parameters) in new[] {
    (".ctor", new[] { "System.Int32", "System.Int32", "UnityEngine.TextureFormat", "System.Int32", "System.Boolean" }),
    ("LoadRawTextureData", new[] { "System.IntPtr", "System.Int32" }),
    ("Apply", new[] { "System.Boolean", "System.Boolean" }) })
{
    Check("SatelliteMap Texture2D." + name + "(" + string.Join(",", parameters) + ")", () => {
        var method = textureType.Methods.Single(m => m.Name == name && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters));
        Require(method.IsPublic && !method.IsStatic && method.ReturnType.FullName == "System.Void",
            "Texture upload API contract changed.");
    });
}
var mods = new[] { "NuclearConnectivity", "NuclearControllerGestures", "NuclearDeathBlackout", "NuclearDelivery", "NuclearLiveFeed", "NuclearMapMarkings", "NuclearMapPlayerNames", "NuclearMapTrails", "NuclearMinimap", "NuclearMfdWeapons", "NuclearSatelliteMap", "NuclearTargeting", "NuclearSounds", "NuclearSpectator", "NuclearTrackAge", "NuclearUnits" };
foreach (var mod in mods)
{
    // Release snapshots can omit private mods. The build runner passes its exact set.
    if (args.Length > 3 && !args[3].Split(',').Contains(mod)) continue;
    string dll = Path.Combine(root, mod, "bin", "Release", mod + ".dll");
    Check(mod + " built assembly", () => Require(File.Exists(dll), $"Build Release first: {dll}"));
    if (!File.Exists(dll)) continue;
    using var plugin = ModuleDefinition.ReadModule(dll, new ReaderParameters { AssemblyResolver = resolver });
    if (mod == "NuclearControllerGestures") Check(mod + " standalone input ownership", () =>
    {
        Require(!plugin.AssemblyReferences.Any(reference => mods.Contains(reference.Name) || reference.Name is "NuclearMfdMap" or "NuclearMfd" or "NuclearTacticalTools"), "Controller gestures must not reference another mod DLL.");
        var bootstrap = AllTypes(plugin.Types).Single(t => t.CustomAttributes.Any(a => a.AttributeType.FullName == "BepInEx.BepInPlugin"));
        Require(!bootstrap.CustomAttributes.Any(a => a.AttributeType.FullName == "BepInEx.BepInDependency"), "Controller gestures must operate independently.");
    });
    // Legacy KeyboardShortcut reads Unity input, which never sees the game's controllers.
    Check(mod + " bindings use the shared binding editor", () =>
    {
        Require(!plugin.GetTypeReferences().Any(t => t.FullName == "BepInEx.Configuration.KeyboardShortcut"),
            "Bind keys and buttons with Shared/NuclearBindings InputBinding, not BepInEx KeyboardShortcut.");
        Require(!plugin.AssemblyReferences.Any(reference => reference.Name == "NuclearBindings"), "The binding helper must be compiled in as source.");
        bool consumer = AllTypes(plugin.Types).Any(t => t.FullName == "NuclearBindings.InputBinding");
        Require(consumer == (mod is "NuclearMinimap" or "NuclearControllerGestures" or "NuclearMfdWeapons" or "NuclearTargeting" or "NuclearSpectator"),
            "Unexpected shared binding consumer set; update this check deliberately when a mod gains or loses bindings.");
        if (!consumer) return;
        // ConfigurationManager discovers the drawer by this exact tag type and field name.
        var tag = AllTypes(plugin.Types).Single(t => t.FullName == "NuclearBindings.ConfigurationManagerAttributes");
        Require(tag.Fields.Any(f => f.IsPublic && f.Name == "CustomDrawer" && f.FieldType.FullName == "System.Action`1<BepInEx.Configuration.ConfigEntryBase>"),
            "ConfigurationManager must discover the binding drawer by exact tag type/field names.");
    });
    if (mod is "NuclearDelivery" or "NuclearMinimap") Check(mod + " standalone MFD consumer", () =>
    {
        Require(!plugin.AssemblyReferences.Any(reference => reference.Name is "NuclearMfd" or "NuclearMfdWeapons" or "NuclearTacticalTools" or "NuclearDelivery" or "NuclearMfdMap" or "NuclearMinimap"), "Standalone consumer must compile its MFD helper as source without a runtime mod/helper reference.");
        var bootstrap = AllTypes(plugin.Types).Single(t => t.CustomAttributes.Any(a => a.AttributeType.FullName == "BepInEx.BepInPlugin"));
        Require(!bootstrap.CustomAttributes.Any(a => a.AttributeType.FullName == "BepInEx.BepInDependency"), "Standalone consumer must operate without another plugin.");
        if (mod == "NuclearMinimap")
        {
            Require(bootstrap.CustomAttributes.Any(a => a.AttributeType.FullName == "BepInEx.BepInIncompatibility" &&
                a.ConstructorArguments.Count == 1 && (string)a.ConstructorArguments[0].Value == "dev.dikka.nuclearmfdmap"),
                "Renamed minimap must reject the legacy plugin to prevent duplicate input/display patches.");
        }
        Require(AllTypes(plugin.Types).Any(t => t.FullName == "NuclearMfd.MfdSession"), "Source-only MFD helper was not embedded.");
    });
    if (mod is "NuclearTargeting" or "NuclearSounds")
    {
        Check(mod + " independent split ownership", () =>
        {
            Require(!plugin.AssemblyReferences.Any(reference => reference.Name is "NuclearTargeting" or "NuclearSounds" or "NuclearTacticalTools"), "Split plugins must have no DLL reference to each other or the retired plugin.");
            var bootstrap = AllTypes(plugin.Types).Single(t => t.CustomAttributes.Any(a => a.AttributeType.FullName == "BepInEx.BepInPlugin"));
            Require(bootstrap.CustomAttributes.Any(a => a.AttributeType.FullName == "BepInEx.BepInIncompatibility" && (string)a.ConstructorArguments[0].Value == "dev.dikka.nucleartacticaltools"), "Retired plugin incompatibility is required to prevent duplicate patches.");
            var dependencies = bootstrap.CustomAttributes.Where(a => a.AttributeType.FullName == "BepInEx.BepInDependency").ToArray();
            if (mod == "NuclearTargeting")
            {
                Require(dependencies.Length == 0, "Targeting must operate independently.");
                var selectionEvent = bootstrap.Events.Single(e => e.Name == "TargetSelectionAdded");
                Require(selectionEvent.EventType.FullName == "System.Action`2<System.Int32,System.Int32>" && selectionEvent.AddMethod.IsPublic && selectionEvent.AddMethod.IsStatic, "Optional sound event contract changed.");
                Require(!AllTypes(plugin.Types).Any(t => t.Name == "MissileVoiceWarnings"), "Targeting must not own sound patches.");
            }
            else
            {
                Require(dependencies.Length == 1 && dependencies[0].ConstructorArguments.Count == 2 && (string)dependencies[0].ConstructorArguments[0].Value == "dev.dikka.nucleartargeting" && Convert.ToInt32(dependencies[0].ConstructorArguments[1].Value) == 2, "Sounds must use only an optional Targeting dependency for load ordering.");
                Require(!AllTypes(plugin.Types).Any(t => t.Name == "AlternativeTargetingPatch"), "Sounds must not own selection override patches.");
            }
        });
    }
    Check(mod + " plugin identity", () =>
    {
        var project = XDocument.Load(Path.Combine(root, mod, mod + ".csproj"));
        string Property(string name) => project.Descendants(name).Single().Value;
        var bootstrap = AllTypes(plugin.Types).SelectMany(t => t.CustomAttributes)
            .Single(a => a.AttributeType.FullName == "BepInEx.BepInPlugin");
        Require(bootstrap.ConstructorArguments.Count == 3, "Unsupported BepInPlugin declaration.");
        string id = (string)bootstrap.ConstructorArguments[0].Value;
        string version = (string)bootstrap.ConstructorArguments[2].Value;
        Require(id == Property("BepInExPluginGuid"), $"DLL plugin ID {id} does not match project.");
        Require(version == Property("Version"), $"DLL plugin version {version} does not match project version {Property("Version")}; rebuild the plugin.");
    });
    Check(mod + (mod == "NuclearSatelliteMap" ? " independent patch-free overlay" : " explicit patch assembly"), () =>
    {
        var bootstrap = AllTypes(plugin.Types).Single(t => t.CustomAttributes.Any(a => a.AttributeType.FullName == "BepInEx.BepInPlugin"));
        var awake = bootstrap.Methods.Single(m => m.Name == "Awake" && m.HasBody);
        var registrations = awake.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
            .Where(m => m.DeclaringType.FullName == "HarmonyLib.Harmony" && m.Name == "PatchAll").ToArray();
        if (mod == "NuclearSatelliteMap")
        {
            Require(!plugin.AssemblyReferences.Any(reference => reference.Name == "0Harmony" || mods.Contains(reference.Name)),
                "Satellite overlay must not reference Harmony or another mod DLL.");
            Require(!bootstrap.CustomAttributes.Any(a => a.AttributeType.FullName == "BepInEx.BepInDependency"),
                "Satellite overlay must operate independently.");
            Require(registrations.Length == 0, "Satellite overlay must not register Harmony patches.");
            return;
        }
        Require(registrations.Length == 1 && registrations[0].Parameters.Count == 1
            && registrations[0].Parameters[0].ParameterType.FullName == "System.Reflection.Assembly",
            "Awake must explicitly supply its plugin assembly to PatchAll; parameterless registration depends on an inlining-sensitive stack frame.");
    });
    if (mod == "NuclearSounds") Check(mod + " anti-radiation seeker dispatch", () =>
    {
        Require(gameTypes.TryGetValue("ARMSeeker", out var seeker), "ARMSeeker type missing.");
        var getter = seeker!.Methods.Single(m => m.Name == "GetSeekerType" && m.HasBody);
        var returnedNames = getter.Body.Instructions.Where(i => i.OpCode == Mono.Cecil.Cil.OpCodes.Ldstr
            && i.Next?.OpCode == Mono.Cecil.Cil.OpCodes.Ret).Select(i => (string)i.Operand).ToArray();
        Require(returnedNames.Length == 1, "Unsupported ARMSeeker.GetSeekerType return form; inspect the current client deliberately.");
        var warningType = AllTypes(plugin.Types).Single(t => t.FullName == "NuclearSounds.MissileVoiceWarnings");
        var dispatchKey = warningType.Fields.Single(f => f.Name == "ArmSeeker").Constant as string;
        Require(dispatchKey == returnedNames[0], $"Anti-radiation dispatch key {dispatchKey} differs from the game's {returnedNames[0]}; its custom callout would never be selected.");
    });
    int before = patches;
    foreach (var owner in AllTypes(plugin.Types))
    {
        foreach (var method in owner.Methods.Where(m => m.HasBody))
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.Operand is not GenericInstanceMethod call || call.Name != "FieldRefAccess"
                || call.DeclaringType.FullName != "HarmonyLib.AccessTools") continue;
            Check(mod + ":" + owner.FullName + " typed FieldRef", () =>
            {
                Require(call.GenericArguments.Count == 2 && call.Parameters.Count == 1
                    && instruction.Previous?.Operand is string, "Unsupported FieldRefAccess form; extend checker explicitly.");
                string targetName = call.GenericArguments[0].FullName, fieldName = (string)instruction.Previous!.Operand;
                Require(gameTypes.TryGetValue(targetName, out var targetType), $"Type missing: {targetName}");
                var field = Hierarchy(targetType!).SelectMany(t => t.Fields).FirstOrDefault(f => f.Name == fieldName);
                Require(field != null, $"Field missing: {targetName}.{fieldName}");
                Require(field!.FieldType.FullName == call.GenericArguments[1].FullName,
                    $"{targetName}.{fieldName}: plugin requests {call.GenericArguments[1].FullName}, game declares {field.FieldType.FullName}.");
            });
        }
        var attrs = owner.CustomAttributes.Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch").ToArray();
        foreach (var method in owner.Methods.Where(m => m.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch")))
            Check(mod + ":" + method.FullName, () => throw new InvalidOperationException("Method-level HarmonyPatch declarations are not supported; extend checker explicitly."));
        if (attrs.Length == 0) continue;
        patches++;
        Check(mod + ":" + owner.FullName, () =>
        {
            TypeReference? target = null;
            string? methodName = null;
            string[]? parameterTypes = null;
            int methodType = 0;
            foreach (var attr in attrs)
            {
                Require(!attr.HasFields && !attr.HasProperties, "Named HarmonyPatch arguments are unsupported.");
                foreach (var argument in attr.ConstructorArguments)
                {
                    switch (argument.Type.FullName)
                    {
                        case "System.Type": Require(target == null, "Repeated declaring type."); target = (TypeReference)argument.Value; break;
                        case "System.String": Require(methodName == null, "Repeated method name."); methodName = (string)argument.Value; break;
                        case "System.Type[]": Require(parameterTypes == null, "Repeated argument types."); parameterTypes = ((CustomAttributeArgument[])argument.Value).Select(a => ((TypeReference)a.Value).FullName).ToArray(); break;
                        case "HarmonyLib.MethodType": methodType = Convert.ToInt32(argument.Value); break;
                        default: throw new InvalidOperationException($"Unsupported Harmony argument {argument.Type.FullName}.");
                    }
                }
            }
            bool selectNonGeneric = false;
            if (target == null && ((mod == "NuclearMinimap" && owner.FullName == "NuclearMinimap.MapInputConsumption/SaveAllMapCollectionPatch") ||
                (mod == "NuclearControllerGestures" && owner.FullName == "NuclearControllerGestures.NativeSaveGuards/SaveAllMapCollectionPatch")))
            {
                // This one native overload collides with its generic counterpart. Verify
                // the bounded selector in compiled IL and inspect only its exact contract.
                var selector = owner.Methods.Single(m => m.Name == "TargetMethod" && m.IsStatic && m.HasBody);
                var references = selector.Body.Instructions.Select(i => i.Operand).OfType<TypeReference>().ToArray();
                target = references.Single(t => t.FullName == "Rewired.Player/ControllerHelper/MapHelper");
                Require(references.Any(t => t.FullName == "System.Boolean") && references.Any(t => t.FullName == "Rewired.ControllerMapSaveData[]"), "Save selector type contract changed.");
                Require(selector.Body.Instructions.Any(i => i.Operand is string value && value == "GetAllMapSaveData"), "Save selector method name changed.");
                Require(selector.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().Any(m => m.Name == "get_IsGenericMethod"), "Save selector must distinguish the generic overload.");
                methodName = "GetAllMapSaveData"; parameterTypes = new[] { "System.Boolean" }; selectNonGeneric = true;
            }
            Require(target != null, "Missing target type; unsupported dynamic TargetMethod/TargetMethods.");
            var targetTypes = target!.Scope.Name == "Rewired_Core" ? rewiredTypes : gameTypes;
            Require(targetTypes.TryGetValue(target.FullName, out var type), $"Target type missing: {target.FullName}");
            Require(methodType is >= 0 and <= 4, $"Unsupported MethodType {methodType}.");
            string name = methodType switch { 1 => "get_" + methodName, 2 => "set_" + methodName, 3 => ".ctor", 4 => ".cctor", _ => methodName ?? "" };
            Require(name.Length > 0, "Missing target method name.");
            // Harmony searches the closest declaring type first. Never choose an ambiguous overload.
            MethodDefinition[] candidates = [];
            foreach (var level in Hierarchy(type!))
            {
                candidates = level.Methods.Where(m => m.Name == name && (!selectNonGeneric || !m.HasGenericParameters) && (parameterTypes == null || m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameterTypes))).ToArray();
                if (candidates.Length > 0 || name is ".ctor" or ".cctor") break;
            }
            Require(candidates.Length == 1, $"{target.FullName}.{name}: expected one target, found {candidates.Length}; specify overload types when ambiguous.");
        });
    }
    if (mod == "NuclearSatelliteMap")
        Check(mod + " has no Harmony targets", () => Require(patches == before, "Satellite overlay must stay patch-free."));
    else
        Check(mod + " declares Harmony targets", () => Require(patches > before, "No supported Harmony targets found."));
}
Check("private-field contract", () =>
{
    using var json = JsonDocument.Parse(File.ReadAllText(contractPath));
    Require(json.RootElement.GetProperty("schemaVersion").GetInt32() == 1, "Unsupported contract schema.");
    foreach (var entry in json.RootElement.GetProperty("fields").EnumerateArray())
    {
        fields++;
        string typeName = entry.GetProperty("type").GetString()!, name = entry.GetProperty("name").GetString()!, expected = entry.GetProperty("fieldType").GetString()!;
        Check(typeName + "." + name, () =>
        {
            Require(gameTypes.TryGetValue(typeName, out var type), $"Type missing: {typeName}");
            FieldDefinition? field = Hierarchy(type!).SelectMany(t => t.Fields).FirstOrDefault(f => f.Name == name);
            Require(field != null, "Field missing.");
            Require(field!.FieldType.FullName == expected, $"Expected {expected}; found {field.FieldType.FullName}.");
        });
    }
    Require(fields > 0, "Empty field contract.");
});
Console.WriteLine($"Metadata preflight: {patches} Harmony targets, {fields} field contracts; {passed} passed, {failed} failed.");
Console.WriteLine("Does not verify patch parameter injection, method bodies, behavioral compatibility, Unity lifecycle/UI/audio, or runtime execution. Rebuild all active mods before running.");
return failed == 0 ? 0 : 1;
