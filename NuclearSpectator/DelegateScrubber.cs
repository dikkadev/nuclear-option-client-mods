using NuclearOption.UIStyleSystem;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace NuclearSpectator
{
    // Native HUD components subscribe to the aircraft and expect to live until it is
    // disabled: several never unsubscribe, and their handlers touch the destroyed
    // component (Destroy(gameObject), enabled = ...). An exception there would stop the
    // rest of the aircraft's event, so before destroying spectator copies early, remove
    // every handler that targets them.
    internal static class DelegateScrubber
    {
        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static readonly Dictionary<Type, FieldInfo[]> InstanceFields = new Dictionary<Type, FieldInfo[]>();
        private static readonly Type[] StaticSources = { typeof(ThemeManager), typeof(CombatHUD), typeof(PlayerSettings) };

        internal static int Remove(Aircraft? aircraft, HashSet<object> owned)
        {
            if (owned.Count == 0) return 0;
            int removed = 0;
            if (aircraft != null)
                foreach (var source in aircraft.GetComponentsInChildren<MonoBehaviour>(true))
                    if (source != null) removed += ScrubInstance(source, owned);
            var options = SceneSingleton<HUDOptions>.i;
            if (options != null) removed += ScrubInstance(options, owned);
            foreach (var type in StaticSources)
                foreach (var field in type.GetFields(Declared | BindingFlags.Static))
                    if (!field.IsInitOnly && typeof(Delegate).IsAssignableFrom(field.FieldType))
                        removed += Scrub(field, null, owned);
            return removed;
        }

        private static int ScrubInstance(object source, HashSet<object> owned)
        {
            int removed = 0;
            foreach (var field in FieldsOf(source.GetType())) removed += Scrub(field, source, owned);
            return removed;
        }

        private static int Scrub(FieldInfo field, object? source, HashSet<object> owned)
        {
            if (!(field.GetValue(source) is Delegate value)) return 0;
            Delegate? result = value;
            int removed = 0;
            foreach (var handler in value.GetInvocationList())
            {
                if (handler.Target == null || !owned.Contains(handler.Target)) continue;
                result = Delegate.Remove(result, handler);
                removed++;
            }
            if (removed > 0) field.SetValue(source, result);
            return removed;
        }

        private static FieldInfo[] FieldsOf(Type type)
        {
            if (InstanceFields.TryGetValue(type, out var cached)) return cached;
            var fields = new List<FieldInfo>();
            for (var level = type; level != null && level != typeof(MonoBehaviour); level = level.BaseType)
                foreach (var field in level.GetFields(Declared | BindingFlags.Instance))
                    if (!field.IsInitOnly && typeof(Delegate).IsAssignableFrom(field.FieldType)) fields.Add(field);
            return InstanceFields[type] = fields.ToArray();
        }
    }

    // Unity objects compare equal after destruction; ownership needs reference identity.
    internal sealed class ReferenceComparer : IEqualityComparer<object>
    {
        internal static readonly ReferenceComparer Instance = new ReferenceComparer();
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
