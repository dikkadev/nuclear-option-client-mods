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
            Enabled = Config.Bind("General", "Enabled", true, "Run the live feed server.");
            Port = Config.Bind("Server", "Port", 39391, new ConfigDescription("WebSocket port viewers connect to. Avoid 44620-48715 when WSL uses mirrored networking: WSL reserves that range on Windows.", new AcceptableValueRange<int>(1024, 65535)));
            LanAccess = Config.Bind("Server", "Allow LAN Viewers", false, "Listen on all network interfaces so other devices on the LAN can connect. Off: this PC only (127.0.0.1).");
            Origins = Config.Bind("Server", "Allowed Origins", "https://map.scramd.com, http://localhost:*, http://127.0.0.1:*", "Comma-separated web origins allowed to connect. * matches a port or host part.");
            AllowMissingOrigin = Config.Bind("Server", "Allow Non-Browser Clients", true, "Accept connections without an Origin header, such as local tools.");
            Control = Config.Bind("Server", "Viewer Target Selection", ControlAccess.ThisPC, "Which viewers may change your target list. ThisPC: browsers on this PC only. Lan: any connected viewer. Off: view only. Selection follows the in-game map's rules.");
            UdpEnabled = Config.Bind("UDP", "Enabled", false, "Also send every DIS PDU (and optionally CoT) as UDP datagrams for native DIS tools and Wireshark.");
            UdpHost = Config.Bind("UDP", "Host", "127.0.0.1", "Destination address: unicast, broadcast or multicast.");
            UdpDisPort = Config.Bind("UDP", "DIS Port", 3000, new ConfigDescription("Destination port for DIS PDUs (3000 is the usual DIS port).", new AcceptableValueRange<int>(1, 65535)));
            UdpCotPort = Config.Bind("UDP", "CoT Port", 0, new ConfigDescription("Destination port for CoT XML; 0 disables CoT over UDP.", new AcceptableValueRange<int>(0, 65535)));
            Exercise = Config.Bind("DIS", "Exercise ID", 1, new ConfigDescription("DIS exercise identifier.", new AcceptableValueRange<int>(1, 255)));
            Site = Config.Bind("DIS", "Site ID", 1, new ConfigDescription("DIS site number of this producer.", new AcceptableValueRange<int>(1, 65534)));
            Application = Config.Bind("DIS", "Application ID", 1, new ConfigDescription("DIS application number of this producer.", new AcceptableValueRange<int>(1, 65534)));
            SampleHz = Config.Bind("Updates", "Sample Rate Hz", 20f, new ConfigDescription("How often the game state is checked.", new AcceptableValueRange<float>(1f, 60f)));
            MaxRateHz = Config.Bind("Updates", "Max Entity Rate Hz", 10f, new ConfigDescription("Maximum updates per second for one entity.", new AcceptableValueRange<float>(0.5f, 30f)));
            Heartbeat = Config.Bind("Updates", "Heartbeat Seconds", 5f, new ConfigDescription("Full Entity State refresh interval.", new AcceptableValueRange<float>(1f, 30f)));
            AirThreshold = Config.Bind("Updates", "Air Position Threshold", 10f, new ConfigDescription("Meters of dead-reckoning error before an aircraft or munition update.", new AcceptableValueRange<float>(0.5f, 200f)));
            GroundThreshold = Config.Bind("Updates", "Ground Position Threshold", 5f, new ConfigDescription("Meters of dead-reckoning error before a ground vehicle or ship update.", new AcceptableValueRange<float>(0.5f, 200f)));
            AngleThreshold = Config.Bind("Updates", "Orientation Threshold Degrees", 5f, new ConfigDescription("Degrees of orientation error before an update.", new AcceptableValueRange<float>(0.5f, 90f)));
            RevealAll = Config.Bind("Visibility", "Reveal All Units In Single Player", false, "Single player only: send every unit, including enemies your side has not detected. Multiplayer always sends only your side's knowledge.");
            AnchorLongitude = Config.Bind("Map", "Anchor Longitude", 0f, new ConfigDescription("Longitude given to the map center for DIS/CoT coordinates. Latitude comes from the map's own setting.", new AcceptableValueRange<float>(-180f, 180f)));

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
}
