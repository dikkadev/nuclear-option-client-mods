using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Rewired;
using Rewired.Data;
using UnityEngine;

namespace NuclearMinimap
{
    // Feed short taps back through native Rewired evaluation, including timed-press actions.
    internal sealed class MapNativeTapReplay : IDisposable
    {
        private static readonly PropertyInfo UserDataProperty = typeof(ReInput).GetProperty("UserData", BindingFlags.Static | BindingFlags.NonPublic)!;
        private static readonly FieldInfo DefinitionsField = typeof(UserData).GetField("customControllers", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo SavedMapsField = FindSavedMapsField();
        private readonly List<Pulse> pulses = new List<Pulse>();
        private readonly ConditionalWeakTable<ControllerMap, object> ownedMaps = new ConditionalWeakTable<ControllerMap, object>();

        internal readonly struct Mapping
        {
            internal readonly int Action, Category, Layout;
            internal readonly Pole Pole;
            internal Mapping(ActionElementMap source)
            { Action = source.actionId; Pole = source.axisContribution; Category = source.controllerMap.categoryId; Layout = source.controllerMap.layoutId; }
        }
        private sealed class Pulse
        {
            internal CustomController Controller = null!;
            internal int Started;
        }

        internal void Emit(Rewired.Player player, List<Mapping> mappings)
        {
            if (mappings.Count == 0) return;
            var data = UserDataProperty.GetValue(null, null) as UserData ?? throw new InvalidOperationException("Rewired runtime UserData is unavailable.");
            var definitions = DefinitionsField.GetValue(data) as List<CustomController_Editor> ?? throw new InvalidOperationException("Rewired custom controller definitions are unavailable.");
            var guid = Guid.NewGuid();
            CustomController_Editor? definition = null;
            CustomController? controller = null;
            try
            {
                data.AddCustomController(guid);
                definition = data.GetCustomControllerByHardwareTypeGuid(guid);
                if (definition == null) throw new InvalidOperationException("Rewired did not create the transient source definition.");
                definition.AddButton();
                controller = ReInput.controllers.CreateCustomController(definition.id, "NuclearMinimap transient short tap");
                if (controller == null) throw new InvalidOperationException("Rewired did not create the transient controller.");
                player.controllers.AddController(controller, false);
                var maps = new Dictionary<string, ControllerMap>();
                var assignments = new HashSet<string>();
                foreach (var mapping in mappings)
                {
                    int layout = mapping.Layout;
                    if (ReInput.mapping.GetLayout(ControllerType.Custom, layout) == null)
                    {
                        var layouts = ReInput.mapping.CustomControllerLayouts;
                        if (layouts.Count == 0) throw new InvalidOperationException("Rewired has no native custom-controller layout.");
                        layout = layouts[0].id;
                    }
                    string key = mapping.Category + ":" + layout;
                    if (!maps.TryGetValue(key, out var map))
                    {
                        map = ControllerMap.Create(controller, mapping.Category, layout);
                        ownedMaps.Add(map, new object());
                        maps.Add(key, map);
                        player.controllers.maps.AddMap(controller, map, true);
                    }
                    if (!assignments.Add(key + ":" + mapping.Action + ":" + mapping.Pole)) continue;
                    if (!map.CreateElementMap(mapping.Action, mapping.Pole, definition.buttons[0].elementIdentifierId, ControllerElementType.Button, AxisRange.Full, false))
                        throw new InvalidOperationException("Rewired rejected a transient native tap mapping.");
                }
                controller.SetButtonValue(0, true);
                pulses.Add(new Pulse { Controller = controller, Started = Time.frameCount });
                controller = null;
            }
            finally
            {
                // CreateCustomController copies a complete hardware map. The source definition
                // is needed only during construction/assignment and never survives this call.
                if (definition != null) definitions.Remove(definition);
                if (controller != null) ReInput.controllers.DestroyCustomController(controller);
            }
        }

        internal void Update()
        {
            for (int i = pulses.Count - 1; i >= 0; i--)
            {
                var pulse = pulses[i];
                int elapsed = Time.frameCount - pulse.Started;
                pulse.Controller.SetButtonValue(0, elapsed == 0);
                // Leave the false frame to native evaluation before removing its source.
                if (elapsed < 3) continue;
                ReInput.controllers.DestroyCustomController(pulse.Controller);
                pulses.RemoveAt(i);
            }
        }

        internal bool Owns(ControllerMap map)
        {
            if (ownedMaps.TryGetValue(map, out _)) return true;
            foreach (var pulse in pulses) if (map.controllerType == ControllerType.Custom && map.controllerId == pulse.Controller.id) return true;
            return false;
        }
        internal void RememberCopy(ControllerMap map)
        {
            if (!ownedMaps.TryGetValue(map, out _)) ownedMaps.Add(map, new object());
        }

        internal void FilterSave(ref ControllerMapSaveData[] saved)
        {
            if (saved == null) return;
            var retained = new List<ControllerMapSaveData>();
            foreach (var item in saved) if (item == null || !Owns(item.map)) retained.Add(item!);
            if (retained.Count != saved.Length) saved = retained.ToArray();
        }

        internal void FilterSave(ref PlayerSaveData saved)
        {
            var original = saved.customControllerMapSaveData;
            if (original == null) return;
            var retained = new List<CustomControllerMapSaveData>();
            foreach (var item in original) if (item == null || !Owns(item.map)) retained.Add(item!);
            if (retained.Count == original.Length) return;
            object boxed = saved;
            SavedMapsField.SetValue(boxed, retained.ToArray());
            saved = (PlayerSaveData)boxed;
        }

        private static FieldInfo FindSavedMapsField()
        {
            FieldInfo? found = null;
            foreach (var field in typeof(PlayerSaveData).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                if (field.FieldType == typeof(CustomControllerMapSaveData[]))
                {
                    if (found != null) throw new InvalidOperationException("Rewired player save custom-map field is ambiguous.");
                    found = field;
                }
            return found ?? throw new InvalidOperationException("Rewired player save custom-map field is missing.");
        }

        public void Dispose()
        {
            foreach (var pulse in pulses) ReInput.controllers.DestroyCustomController(pulse.Controller);
            pulses.Clear();
        }
    }
}
