using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NuclearMfd;
using NuclearOption.UIStyleSystem;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
namespace NuclearMfdWeapons
{
    internal sealed partial class DisplayController : IDisposable
    {
        // Current-client nested station fields are inspected as objects; no invalid typed FieldRefs.
        private static readonly FieldInfo StationsField = AccessTools.Field(typeof(CountermeasureManager), "countermeasureStations") ?? throw new MissingFieldException("countermeasureStations");
        private static readonly FieldInfo SupplyField = AccessTools.Field(typeof(RadarJammer), "powerSupply") ?? throw new MissingFieldException("powerSupply");
        private readonly Dictionary<Type, FieldInfo> countermeasureFields = new Dictionary<Type, FieldInfo>();
        private readonly List<InstrumentState> instruments = new List<InstrumentState>();
        private readonly List<KeyValuePair<Behaviour, bool>> instrumentLayouts = new List<KeyValuePair<Behaviour, bool>>();
        private bool instrumentsAdjusted;
        private readonly MfdSession session;
        private readonly MfdPage weapons;
        private MfdPage? preview;
        private readonly Aircraft aircraft;
        private readonly Plugin plugin;
        private readonly WeaponLayout layout;
        private TextMeshProUGUI name = null!, ammo = null!, flare = null!, jammer = null!;
        private Image icon = null!, divider = null!, previewBackground = null!;
        private readonly List<TextMeshProUGUI> rows = new List<TextMeshProUGUI>();
        private readonly StockHudVisibility stockHud = new StockHudVisibility();
        private CockpitNozzleLayout? nozzle;
        private CapacitorHudLayout? capacitor;
        private WeaponStation? lastStation;
        private float previewUntil;
        private Color clear = Color.green, alert = Color.red, textColor = Color.white;
        private Gradient valueGradient = new Gradient();
        private int previewFont;
        private readonly List<Image> previewBorders = new List<Image>();
        private bool disposed;
        private TMP_FontAsset fontAsset = null!;
        private DisplayController(MfdSession session, MfdPage weapons, Aircraft aircraft, Plugin plugin, WeaponLayout layout)
        { this.session = session; this.weapons = weapons; this.aircraft = aircraft; this.plugin = plugin; this.layout = layout; }
        internal static DisplayController? Create(TacScreen screen, Aircraft aircraft, Plugin plugin)
        {
            if (!MfdSession.TryCreate(screen, aircraft, out var session, out var reason) || session == null) { plugin.Unavailable(reason); return null; }
            DisplayController? result = null;
            try
            {
                string mapping = "aircraftParameters='" + session.AircraftParametersName + "' definition='" + session.AircraftDefinitionName + "' key='" + session.AircraftDefinitionKey + "' layout='" + session.AircraftName + "'";
                if (!session.TryGetSurface("weapons", out var surface) || surface == null) { session.Dispose(); plugin.Unavailable("No weapons surface for this cockpit; " + mapping); return null; }
                if (surface.IsFallback) { session.Dispose(); plugin.Unavailable("Unrecognized cockpit layout; stock readouts retained; " + mapping); return null; }
                var layout = new WeaponLayout(session.AircraftName);
                result = new DisplayController(session, surface.Attach("weapons", plugin.Replace.Value && layout.removeOriginalMFDContent, session.AircraftName == "SFB-81" || session.AircraftName == "MiG-15" || session.AircraftName == "FastBomber1"), aircraft, plugin, layout);
                result.Build(); result.Refresh(true); plugin.DisplayReady(mapping + " panel='" + surface.Path + "' dimensions=" + surface.Root.rect.size + "; " + result.nozzle?.Diagnostic); return result;
            }
            catch { if (result != null) result.Dispose(); else session.Dispose(); throw; }
        }
        private void Build()
        {
            CacheHud();
            nozzle = new CockpitNozzleLayout(session);
            capacitor = new CapacitorHudLayout();
            var root = weapons.Root;
            ResolveFont();
            if (session.AircraftName == "SAH-46 Chicane") CaptureInstruments(root.parent);
            if (session.AircraftName == "SFB-81") root.localRotation = Quaternion.Euler(0, 0, -90);
            name = Label(root, "Weapon", layout.weaponNamePos, layout.weaponNameFont, new Vector2(WeaponWidth(), layout.weaponNameFont * 2));
            ammo = Label(root, "Ammo", layout.weaponAmmoPos, layout.weaponAmmoFont, new Vector2(session.AircraftName == "VL-49 Tarantula" ? 160 : WeaponWidth(), layout.weaponAmmoFont * 2));
            flare = Label(root, "Flares", layout.flarePos, layout.flareFont, new Vector2(CountermeasureWidth(), layout.flareFont * 2));
            jammer = Label(root, "Jammer", layout.jammerPos, layout.jammerFont, new Vector2(CountermeasureWidth(), layout.flareFont * 2));
            icon = CreateWeaponIcon(root);
            if (layout.rotateWeaponImage) icon.rectTransform.localRotation = Quaternion.Euler(0, 0, -90);
            var delta = layout.lineEnd - layout.lineStart;
            divider = Image(root, "Divider", (layout.lineStart + layout.lineEnd) / 2, new Vector2(delta.magnitude, 1));
            divider.rectTransform.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
            if (session.TryGetSurface("main", out var main) && main != null && aircraft.weaponStations.Count > 1)
            {
                preview = main.Attach("loadout-preview", false);
                var pos = PreviewPosition(session.AircraftName, out var font);
                preview.Root.anchoredPosition = pos;
                previewFont = font;
                BuildPreview();
                previewUntil = Time.unscaledTime + plugin.Duration.Value;
            }
            ThemeChanged(); ThemeManager.ThemeGroupChanged += ThemeChanged;
        }
        private void ThemeChanged() { try { ApplyTheme(); } catch { } }
        internal void Refresh(bool custom)
        {
            weapons.SetVisible(custom); AdjustInstruments(custom); nozzle?.Apply(custom);
            // Detach the independently controlled indicator before hiding its stock parent.
            capacitor?.Apply(custom, plugin.Capacitor.Value);
            if (!custom) { preview?.SetVisible(false); RestoreHud(); return; }
            var station = aircraft.weaponManager.currentWeaponStation;
            if (station != lastStation) { lastStation = station; previewUntil = Time.unscaledTime + plugin.Duration.Value; }
            if (station != null) { SetText(name, Name(station)); SetText(ammo, station.Reloading ? Mathf.RoundToInt((1 - Mathf.Clamp01(station.GetReloadStatusMax())) * 100) + "%" : station.GetAmmoReadout().Replace(" ", "")); icon.sprite = station.WeaponInfo.weaponIcon; name.color = textColor; ammo.color = valueGradient.Evaluate(station.Ammo > 0 ? 1 : 0); icon.color = valueGradient.Evaluate(station.Ammo > 0 ? 1 : 0); icon.enabled = icon.sprite != null; }
            else { SetText(name, "NO WEAPONS"); SetText(ammo, "—"); icon.enabled = false; name.color = ammo.color = textColor; }
            SetFont(ammo, layout.weaponAmmoFont - (ammo.text.Contains("/") ? 15 : 0));
            ReadCountermeasures();
            bool show = plugin.Preview.Value && Time.unscaledTime < previewUntil;
            preview?.SetVisible(show);
            if (show) RefreshPreview(station);
            if (plugin.Stock.Value) RestoreHud(); else HideHud();
        }
        private void ReadCountermeasures()
        {
            int flares = 0, max = 0; float charge = 0; bool hasFlare = false, hasJammer = false, selectedFlare = false, selectedJammer = false;
            var manager = aircraft.countermeasureManager;
            var stations = StationsField.GetValue(manager) as IList;
            if (stations != null) for (int i = 0; i < stations.Count; i++)
            {
                var st = stations[i]; if (st == null) continue;
                if (!countermeasureFields.TryGetValue(st.GetType(), out var field)) { field = AccessTools.Field(st.GetType(), "countermeasures") ?? throw new MissingFieldException("countermeasures"); countermeasureFields.Add(st.GetType(), field); }
                var cms = field.GetValue(st) as IList;
                if (cms == null) continue;
                foreach (var cm in cms)
                {
                    if (cm is FlareEjector ejector) { hasFlare = true; flares += ejector.ammo; max += ejector.GetMaxAmmo(); selectedFlare |= i == manager.activeIndex; }
                    if (cm is RadarJammer radar) { hasJammer = true; var supply = SupplyField.GetValue(radar) as PowerSupply; if (supply != null) charge = Mathf.Max(charge, supply.GetCharge()); selectedJammer |= i == manager.activeIndex; }
                }
            }
            SetText(flare, hasFlare ? "IR:" + flares : "IR:—"); SetText(jammer, hasJammer ? "EW:" + (int)(charge * 100) + "%" : "");
            flare.color = valueGradient.Evaluate(max > 0 ? Mathf.Clamp01((float)flares / max) : 0); jammer.color = valueGradient.Evaluate(Mathf.Clamp01(charge));
            flare.fontStyle = selectedFlare ? FontStyles.Bold : FontStyles.Normal; jammer.fontStyle = selectedJammer ? FontStyles.Bold : FontStyles.Normal;
            SetFont(flare, layout.flareFont + (selectedFlare ? 10 : 0)); SetFont(jammer, layout.jammerFont + (selectedJammer ? 10 : 0));
        }
        private void CacheHud() { stockHud.Cache(aircraft); }
        private void HideHud() { stockHud.Hide(); }
        private void RestoreHud() { stockHud.Restore(); }
        private float WeaponWidth() => 200;
        private float CountermeasureWidth() => 200;
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            ThemeManager.ThemeGroupChanged -= ThemeChanged;
            try { AdjustInstruments(false); }
            finally
            {
                try { nozzle?.Dispose(); }
                finally
                {
                    try { capacitor?.Dispose(); }
                    finally
                    {
                        try { RestoreHud(); }
                        finally
                        {
                            try { preview?.Dispose(); }
                            finally { try { weapons.Dispose(); } finally { session.Dispose(); } }
                        }
                    }
                }
            }
        }
        // Preserve the stock Chicane flight instruments and restore exact transforms on page changes.
        private sealed class InstrumentState
        {
            internal Transform Transform = null!;
            internal Vector3 Position, Scale, Offset;
        }
        private void CaptureInstruments(Transform parent)
        {
            foreach (var layoutGroup in parent.GetComponents<LayoutGroup>()) instrumentLayouts.Add(new KeyValuePair<Behaviour, bool>(layoutGroup, layoutGroup.enabled));
            foreach (var fitter in parent.GetComponents<ContentSizeFitter>()) instrumentLayouts.Add(new KeyValuePair<Behaviour, bool>(fitter, fitter.enabled));
            var names = new[] { "Heading", "Airspeed", "RadarAlt", "Horizon", "ClimbRate", "VerticalLadder", "AoAlLadder" };
            var offsets = new[] { new Vector3(-40, 40), new Vector3(40, 60), new Vector3(-40, 80), new Vector3(0, 60), new Vector3(40, 60), new Vector3(0, 55), new Vector3(0, 55) };
            for (int i = 0; i < names.Length; i++) { var child = parent.Find(names[i]); if (child != null) instruments.Add(new InstrumentState { Transform = child, Position = child.localPosition, Scale = child.localScale, Offset = offsets[i] }); }
        }
        private void AdjustInstruments(bool active)
        {
            if (active == instrumentsAdjusted) return;
            if (active) foreach (var item in instrumentLayouts) if (item.Key != null) item.Key.enabled = false;
            foreach (var item in instruments) if (item.Transform != null) { item.Transform.localPosition = item.Position + (active ? item.Offset : Vector3.zero); item.Transform.localScale = item.Scale * (active ? 0.8f : 1f); }
            if (!active) foreach (var item in instrumentLayouts) if (item.Key != null) item.Key.enabled = item.Value;
            instrumentsAdjusted = active;
        }
        private static void SetText(TextMeshProUGUI label, string value) { if (label.text != value) label.text = value; label.rectTransform.sizeDelta = new Vector2(label.preferredWidth, label.fontSize); }
        private static string Name(WeaponStation st) => string.IsNullOrEmpty(st.WeaponInfo.shortName) ? st.WeaponInfo.weaponName : st.WeaponInfo.shortName;
        private TextMeshProUGUI Label(RectTransform root, string id, Vector2 pos, float font, Vector2 size) { var go = new GameObject(id, typeof(RectTransform), typeof(TextMeshProUGUI)); go.transform.SetParent(root, false); var text = go.GetComponent<TextMeshProUGUI>(); text.rectTransform.anchoredPosition = pos; text.rectTransform.sizeDelta = size; text.font = fontAsset; text.fontSize = font; text.enableAutoSizing = false; text.alignment = TextAlignmentOptions.Center; text.raycastTarget = false; text.enableWordWrapping = false; text.overflowMode = TextOverflowModes.Overflow; return text; }
        private static Image Image(RectTransform root, string id, Vector2 pos, Vector2 size) { var go = new GameObject(id, typeof(RectTransform), typeof(Image)); go.transform.SetParent(root, false); var image = go.GetComponent<Image>(); image.rectTransform.anchoredPosition = pos; image.rectTransform.sizeDelta = size; image.raycastTarget = false; return image; }
        private static Vector2 PreviewPosition(string platformName, out int font) { font = 34; switch (platformName) { case "CI-22 Cricket": font = 44; return new Vector2(-105, 0); case "SAH-46 Chicane": case "KR-67 Ifrit": return new Vector2(-130, 65); case "T/A-30 Compass": case "VT-7 Vagrant": return new Vector2(0, 80); case "FS-3 Ternion": font = 30; return new Vector2(-215, 80); case "FS-12 Revoker": case "FS-20 Vortex": return new Vector2(0, 75); case "VL-49 Tarantula": font = 28; return new Vector2(-255, 60); case "EW-1 Medusa": return new Vector2(-225, 65); case "SFB-81": case "FastBomber1": case "AB-4 Alkyon": case "Alkyon AB-4": return new Vector2(-180, 60); case "UH-80 Ibis": return new Vector2(-245, 65); case "A-19 Brawler": return new Vector2(0, 70); case "FQ-106 Kestrel": return new Vector2(0, 75); case "MiG-15": font = 20; return new Vector2(-250, 120); case "F-16M King Viper": return new Vector2(0, 80); default: return Vector2.zero; } }
    }
}
