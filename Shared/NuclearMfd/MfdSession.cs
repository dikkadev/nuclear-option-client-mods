using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace NuclearMfd
{
    public sealed class MfdSession : IDisposable
    {
        private static readonly FieldInfo? CanvasField = typeof(TacScreen).GetField("canvas", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? AircraftField = typeof(TacScreen).GetField("aircraft", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? RadarCenterField = typeof(TacScreen).GetField("radarCenter", BindingFlags.Instance | BindingFlags.NonPublic);
        private readonly Dictionary<string, MfdSurface> surfaces = new Dictionary<string, MfdSurface>();
        private bool disposed;
        public TacScreen Screen { get; }
        public Aircraft Aircraft { get; }
        public RectTransform CanvasRoot { get; }
        public string AircraftName { get; }
        public string AircraftParametersName { get; }
        public string AircraftNameSource { get; }
        /// <summary>Raw stable definition display name; diagnostic metadata, never a guessed registry alias.</summary>
        public string AircraftDefinitionName { get; }
        /// <summary>Stable mission/definition lookup key, independent of aircraft instance names.</summary>
        public string AircraftDefinitionKey { get; }

        private MfdSession(TacScreen screen, Aircraft aircraft, RectTransform canvas, string parametersName, string definitionName, string definitionKey)
        {
            Screen = screen; Aircraft = aircraft; CanvasRoot = canvas; AircraftName = MfdLayouts.GetCanonicalName(parametersName);
            AircraftParametersName = parametersName; AircraftNameSource = "definition.aircraftParameters.aircraftName";
            AircraftDefinitionName = definitionName; AircraftDefinitionKey = definitionKey;
        }

        public static bool TryCreate(TacScreen screen, Aircraft aircraft, out MfdSession? session, out string reason)
        {
            session = null;
            if (!screen || !aircraft) { reason = "TacScreen or aircraft is unavailable."; return false; }
            if (AircraftField != null && !ReferenceEquals(AircraftField.GetValue(screen), aircraft))
            { reason = "TacScreen is not initialized for the supplied aircraft."; return false; }
            var canvas = CanvasField?.GetValue(screen) as Canvas;
            if (!canvas)
            {
                var child = screen.transform.Find("Canvas");
                if (child) canvas = child.GetComponent<Canvas>();
            }
            var root = canvas != null && canvas ? canvas.transform as RectTransform : null;
            if (root == null || !root || !(root == screen.transform || root.IsChildOf(screen.transform)))
            { reason = "TacScreen has no owned Canvas RectTransform."; return false; }
            // GetAircraftParameters returns definition.aircraftParameters in the current client.
            // Do not use aircraft.name or NetworkunitName: those describe runtime instances/players.
            var definition = aircraft.definition;
            var parameters = definition != null ? definition.aircraftParameters : null;
            session = new MfdSession(screen, aircraft, root, parameters != null ? parameters.aircraftName ?? "" : "Unknown",
                definition != null ? definition.unitName ?? "" : "", definition != null ? definition.jsonKey ?? "" : "");
            reason = "";
            return true;
        }

        public bool TryGetSurface(string role, out MfdSurface? surface)
        {
            surface = null;
            if (disposed || !Screen || !Aircraft || !CanvasRoot || string.IsNullOrWhiteSpace(role)) return false;
            if (surfaces.TryGetValue(role, out var existing))
            {
                if (existing.IsCustomMapping) return TryGetSurfaceAtPath(role, existing.Path, out surface);
                if (!existing.IsDisposed && existing.Root) { surface = existing; return true; }
                existing.Dispose();
                surfaces.Remove(role);
            }
            if (role != "main" && role != "weapons" && role != "radar") return false;
            if (role == "radar")
            {
                // Native references resolve nested bomber/helicopter panels as well as flat layouts.
                // The rotating icon layer is below the center; its parent is the stable radar rectangle.
                var center = RadarCenterField?.GetValue(Screen) as Transform;
                var region = center != null ? center.parent as RectTransform : null;
                if (region == null || !region || region == CanvasRoot || !region.IsChildOf(CanvasRoot)) return false;
                var segments = new List<string>();
                for (Transform node = region; node != CanvasRoot; node = node.parent) segments.Add(node.name);
                segments.Reverse();
                surface = new MfdSurface(region, string.Join("/", segments), false, false, native: true);
                surfaces.Add(role, surface);
                return true;
            }
            bool recognized = false;
            string path = role == "main" ? "" : MfdLayouts.GetWeaponPath(AircraftName, out recognized);
            var root = role == "main" ? CanvasRoot : CanvasRoot.Find(path) as RectTransform;
            if (root == null || !root) return false;
            surface = new MfdSurface(root, path, recognized, role == "weapons" && !recognized);
            surfaces.Add(role, surface);
            return true;
        }

        /// <summary>Resolves an exact consumer-defined region beneath this session's canvas.</summary>
        public bool TryGetSurfaceAtPath(string role, string canvasRelativePath, out MfdSurface? surface)
        {
            surface = null;
            if (disposed || !Screen || !Aircraft || !CanvasRoot ||
                !MfdPaths.IsValidCustomSurface(role, canvasRelativePath)) return false;
            var root = CanvasRoot.Find(canvasRelativePath) as RectTransform;
            if (root == null || !root || root == CanvasRoot || !root.IsChildOf(CanvasRoot)) return false;
            if (surfaces.TryGetValue(role, out var existing))
            {
                // A role's meaning stays stable for the session, even after disposing its surface.
                if (!existing.IsCustomMapping || existing.Path != canvasRelativePath) return false;
                if (!existing.IsDisposed && existing.Root == root) { surface = existing; return true; }
                existing.Dispose();
                surfaces.Remove(role);
            }
            surface = new MfdSurface(root, canvasRelativePath, false, false, custom: true);
            surfaces.Add(role, surface);
            return true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var surface in surfaces.Values) surface.Dispose();
            surfaces.Clear();
        }
    }
}
