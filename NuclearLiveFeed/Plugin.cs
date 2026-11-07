using System;
using System.Linq;
using System.Net;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using NuclearLiveFeed.Feed;
using UnityEngine;

namespace NuclearLiveFeed
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static Plugin? Instance;
        internal ManualLogSource Log => Logger;
        internal ConfigEntry<bool> Enabled = null!, LanAccess = null!, AllowMissingOrigin = null!, UdpEnabled = null!, RevealAll = null!;
        internal ConfigEntry<int> Port = null!, UdpDisPort = null!, UdpCotPort = null!, Exercise = null!, Site = null!, Application = null!;
        internal ConfigEntry<string> Origins = null!, UdpHost = null!;
        internal ConfigEntry<ControlAccess> Control = null!;
        internal ConfigEntry<float> SampleHz = null!, MaxRateHz = null!, Heartbeat = null!, AirThreshold = null!, GroundThreshold = null!, AngleThreshold = null!, AnchorLongitude = null!;
        internal FeedController? Controller;
        private Harmony? harmony;
        private FeedServer? server;
        private bool restartRequested;

        private void Awake()
        {
            Instance = this;
            // Player-facing settings first; the rest is for other tools and fine tuning (Advanced in the F1 window).
            Enabled = Config.Bind("General", "Enabled", true, Describe("Send your side's live picture to the live map. While a mission runs, open https://map.scramd.com in Chrome or Edge on this PC. How to use the map: https://map.scramd.com/guide", 10));
            Control = Config.Bind("Server", "Viewer Target Selection", ControlAccess.ThisPC, Describe("Who may change your in-game target list from the map (Shift-click, double-click, area drag, Target button). ThisPC: a map on this PC. Lan: also other PCs, with Allow LAN Viewers on. Off: the map only shows. The game's own rules apply: only units and buildings your side knows, never your own side.", 9));
            LanAccess = Config.Bind("Server", "Allow LAN Viewers", false, Describe("Let a map on another PC in your network connect: there, click the map's status and enter ws://<this PC's address>:<Port>, for example ws://192.168.1.20:39391. Off: only this PC.", 8));
            Port = Config.Bind("Server", "Port", 39391, Describe("Network port the map connects to. Change it only if another program uses 39391, then enter the same port on the map (click its status).", 7, new AcceptableValueRange<int>(1024, 65535)));
            RevealAll = Config.Bind("Visibility", "Reveal All Units In Single Player", false, Describe("Single player only: show every unit, also enemies your side has not found. Multiplayer always shows only what your side knows.", 6));
            Origins = Config.Bind("Server", "Allowed Origins", "https://map.scramd.com, http://localhost:*, http://127.0.0.1:*", Advanced("Comma-separated web origins allowed to connect. * matches a port or host part."));
            AllowMissingOrigin = Config.Bind("Server", "Allow Non-Browser Clients", true, Advanced("Accept connections without an Origin header, such as local tools."));
            UdpEnabled = Config.Bind("UDP", "Enabled", false, Advanced("Also send every DIS PDU (and optionally CoT) as UDP datagrams for native DIS tools and Wireshark."));
            UdpHost = Config.Bind("UDP", "Host", "127.0.0.1", Advanced("Destination address: unicast, broadcast or multicast."));
            UdpDisPort = Config.Bind("UDP", "DIS Port", 3000, Advanced("Destination port for DIS PDUs (3000 is the usual DIS port).", new AcceptableValueRange<int>(1, 65535)));
            UdpCotPort = Config.Bind("UDP", "CoT Port", 0, Advanced("Destination port for CoT XML; 0 disables CoT over UDP.", new AcceptableValueRange<int>(0, 65535)));
            Exercise = Config.Bind("DIS", "Exercise ID", 1, Advanced("DIS exercise identifier.", new AcceptableValueRange<int>(1, 255)));
            Site = Config.Bind("DIS", "Site ID", 1, Advanced("DIS site number of this producer.", new AcceptableValueRange<int>(1, 65534)));
            Application = Config.Bind("DIS", "Application ID", 1, Advanced("DIS application number of this producer.", new AcceptableValueRange<int>(1, 65534)));
            SampleHz = Config.Bind("Updates", "Sample Rate Hz", 20f, Advanced("How often the game state is checked.", new AcceptableValueRange<float>(1f, 60f)));
            MaxRateHz = Config.Bind("Updates", "Max Entity Rate Hz", 10f, Advanced("Maximum updates per second for one entity.", new AcceptableValueRange<float>(0.5f, 30f)));
            Heartbeat = Config.Bind("Updates", "Heartbeat Seconds", 5f, Advanced("Full Entity State refresh interval. At most 10: the map drops a unit it has not heard from for 12 seconds.", new AcceptableValueRange<float>(1f, 10f)));
            AirThreshold = Config.Bind("Updates", "Air Position Threshold", 10f, Advanced("Meters of dead-reckoning error before an aircraft or munition update.", new AcceptableValueRange<float>(0.5f, 200f)));
            GroundThreshold = Config.Bind("Updates", "Ground Position Threshold", 5f, Advanced("Meters of dead-reckoning error before a ground vehicle or ship update.", new AcceptableValueRange<float>(0.5f, 200f)));
            AngleThreshold = Config.Bind("Updates", "Orientation Threshold Degrees", 5f, Advanced("Degrees of orientation error before an update.", new AcceptableValueRange<float>(0.5f, 90f)));
            AnchorLongitude = Config.Bind("Map", "Anchor Longitude", 0f, Advanced("Longitude given to the map center for DIS/CoT coordinates. Latitude comes from the map's own setting.", new AcceptableValueRange<float>(-180f, 180f)));

            foreach (var entry in new ConfigEntryBase[] { Enabled, Port, LanAccess, Origins, AllowMissingOrigin, Control, UdpEnabled, UdpHost, UdpDisPort, UdpCotPort })
                Config.SettingChanged += (_, e) => { if (e.ChangedSetting == entry) restartRequested = true; };

            Controller = new FeedController(this);
            harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            harmony.PatchAll(typeof(Plugin).Assembly);
            StartServer();
            Logger.LogInfo("Loaded NuclearLiveFeed " + MyPluginInfo.PLUGIN_VERSION + " build=" + typeof(Plugin).Assembly.ManifestModule.ModuleVersionId + " owned patches=" + harmony.GetPatchedMethods().Count());
        }

        private void StartServer()
        {
            StopServer();
            if (!Enabled.Value) return;
            var options = new FeedServerOptions
            {
                ListenAddress = LanAccess.Value ? IPAddress.Any : IPAddress.Loopback,
                Port = Port.Value,
                AllowedOrigins = Origins.Value.Split(',').Select(o => o.Trim()).Where(o => o.Length > 0).ToArray(),
                AllowMissingOrigin = AllowMissingOrigin.Value,
                UdpHost = UdpEnabled.Value ? UdpHost.Value : null,
                UdpDisPort = UdpDisPort.Value,
                UdpCotPort = UdpCotPort.Value,
                Control = Control.Value,
            };
            try
            {
                server = new FeedServer(options, message => Logger.LogInfo(message));
                server.Start();
                var started = server;
                // Sampling every unit costs main-thread time; skip it while nobody receives the feed.
                Controller!.Attach(started, started, () => started.ClientCount > 0 || started.SendsUdp);
            }
            catch (Exception e)
            {
                Logger.LogError("Could not start the live feed server on port " + Port.Value + ": " + e.Message);
                server?.Dispose();
                server = null;
            }
        }

        private void StopServer()
        {
            Controller?.Detach();
            server?.Dispose();
            server = null;
        }

        private void Update()
        {
            if (restartRequested) { restartRequested = false; StartServer(); }
            if (server == null) return;
            try { Controller!.Tick(); }
            catch (Exception e) { Report("tick", e); }
        }

        private static ConfigDescription Describe(string text, int order, AcceptableValueBase? range = null) =>
            new ConfigDescription(text, range, new ConfigurationManagerAttributes { Order = order });
        private static ConfigDescription Advanced(string text, AcceptableValueBase? range = null) =>
            new ConfigDescription(text, range, new ConfigurationManagerAttributes { IsAdvanced = true });

        private int reports;
        internal void Report(string where, Exception e)
        {
            if (reports++ < 20) Logger.LogError("NuclearLiveFeed " + where + ": " + e);
        }

        private void OnDestroy()
        {
            StopServer();
            harmony?.UnpatchSelf();
        }
    }

    // Read by name by BepInEx Configuration Manager: Order sorts within a section (higher first), IsAdvanced hides a row until "Advanced settings" is on.
    internal sealed class ConfigurationManagerAttributes
    {
        public int? Order;
        public bool? IsAdvanced;
    }
}
