using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NuclearMinimap
{
    internal sealed class NativeRadar : IDisposable
    {
        private static readonly FieldInfo? Center = AccessTools.Field(typeof(TacScreen), "radarCenter");
        private readonly List<KeyValuePair<GameObject, bool>> saved = new List<KeyValuePair<GameObject, bool>>();
        private readonly List<GameObject> visuals = new List<GameObject>();
        private bool replacing;
        internal NativeRadar(TacScreen screen, RectTransform region)
        {
            var center = Center?.GetValue(screen) as Transform;
            if (center == null || center.parent != region) throw new InvalidOperationException("Native radar center does not belong to this surface.");
            foreach (Transform child in region)
            {
                // Exact native prefab decoration only; never capture another mod's attached page.
                if (child == center || IsDecoration(child.name)) visuals.Add(child.gameObject);
            }
        }
        private static bool IsDecoration(string name) => name == "Border" || name == "border" ||
            name == "range" || name == "range (1)" || name == "range (2)" || name == "range (3)" ||
            name == "range10" || name == "range20" || name == "range30" || name == "range40" ||
            name == "divider (3)" || name == "divider (4)";
        internal void SetReplacing(bool value)
        {
            if (value == replacing) return;
            if (value)
            {
                foreach (var visual in visuals)
                    if (visual != null) { saved.Add(new KeyValuePair<GameObject, bool>(visual, visual.activeSelf)); visual.SetActive(false); }
            }
            else
            {
                foreach (var item in saved) if (item.Key != null) item.Key.SetActive(item.Value);
                saved.Clear();
            }
            replacing = value;
        }
        public void Dispose() => SetReplacing(false);
    }
}
