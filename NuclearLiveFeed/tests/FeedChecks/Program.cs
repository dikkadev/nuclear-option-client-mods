using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using NuclearLiveFeed.Feed;

// Engine-independent checks for the NuclearLiveFeed protocol code.
// Usage: dotnet run -- [--write-pdus DIR]

int failures = 0, passed = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) { passed++; return; }
    failures++;
    Console.WriteLine("FAIL " + name + (detail.Length > 0 ? ": " + detail : ""));
}

var anchor = new GeoAnchor(45, 0, 0);

// Geodesy.
{
    var enu = new Vec3(12345.6, -23456.7, 3210.5);
    var back = anchor.EcefToEnu(anchor.EnuToEcef(enu));
    Check("ENU round trip", (back - enu).Length < 1e-6, back.ToString());
    var ecef = anchor.EnuToEcef(enu);
    var (lat, lon, h) = GeoAnchor.EcefToGeodetic(ecef);
    Check("geodetic round trip", (GeoAnchor.GeodeticToEcef(lat, lon, h) - ecef).Length < 1e-3);
    var (alat, alon, ah) = GeoAnchor.EcefToGeodetic(anchor.EnuToEcef(Vec3.Zero));
    Check("anchor maps to itself", Math.Abs(alat - 45) < 1e-9 && Math.Abs(alon) < 1e-9 && Math.Abs(ah) < 1e-6, alat + " " + alon + " " + ah);
    var north = anchor.EnuToEcef(new Vec3(0, 1000, 0));
    Check("north increases latitude", GeoAnchor.EcefToGeodetic(north).Latitude > 45);
}

// Euler angles: forward axis survives the conversion for many attitudes.
{
    var rng = new Random(7);
    double worst = 0;
    for (int i = 0; i < 500; i++)
    {
        var (f, r, d) = Sim.Body(rng.NextDouble() * 360, rng.NextDouble() * 160 - 80, rng.NextDouble() * 360 - 180);
        var (psi, theta, phi) = anchor.EulerFromEnuAxes(f, r, d);
        worst = Math.Max(worst, (anchor.ForwardEnuFromEuler(psi, theta) - f).Length);
    }
    Check("Euler forward round trip", worst < 1e-5, worst.ToString("E2"));
}

// PDU encoding: lengths, header and key fields at their spec offsets.
{
    var state = new EntityState
    {
        Id = new EntityId(1, 2, 3), Force = ForceId.Opposing, Type = new EntityType(1, 2, 0, 0, 0, 7, 0),
        Location = anchor.EnuToEcef(new Vec3(100, 200, 300)), Velocity = new Vec3(1, 2, 3), Acceleration = new Vec3(0.5, 0, 0),
        AngularVelocity = new Vec3(0, 0, 0.1), Psi = 1, Theta = 0.2f, Phi = -0.3f,
        Appearance = Appearance.DamageDestroyed | Appearance.Frozen, Algorithm = DeadReckoning.Rvw, Marking = "VIPER 1 long name",
    };
    var es = DisWriter.EntityState(state, 9, DisWriter.Timestamp(new DateTime(2026, 10, 25, 12, 30, 0, DateTimeKind.Utc)));
    Check("ES length", es.Length == 144 && BinaryPrimitives.ReadUInt16BigEndian(es.AsSpan(8)) == 144);
    Check("ES header", es[0] == 7 && es[1] == 9 && es[2] == 1 && es[3] == 1);
    Check("ES timestamp absolute half hour", (BinaryPrimitives.ReadUInt32BigEndian(es.AsSpan(4)) & 1) == 1
        && Math.Abs((BinaryPrimitives.ReadUInt32BigEndian(es.AsSpan(4)) >> 1) / 2147483648.0 - 0.5) < 1e-6);
    var d = Decode.Entity(es);
    Check("ES decode", d.Id.Equals(state.Id) && d.Force == ForceId.Opposing && d.Type.Specific == 7 && d.Type.Domain == 2
        && (d.Location - state.Location).Length < 1e-6 && Math.Abs(d.Psi - 1) < 1e-6 && d.Algorithm == DeadReckoning.Rvw
        && d.Appearance == state.Appearance && d.Marking == "VIPER 1 lon" && Math.Abs(d.Acceleration.X - 0.5) < 1e-6
        && Math.Abs(d.AngularVelocity.Z - 0.1) < 1e-6);
    var esu = DisWriter.EntityStateUpdate(state, 9, 1);
    var du = Decode.Entity(esu);
    Check("ESU length/type", esu.Length == 72 && esu[2] == 67 && esu[3] == 1);
    Check("ESU decode", du.Id.Equals(state.Id) && (du.Location - state.Location).Length < 1e-6 && du.Appearance == state.Appearance);
    var fire = DisWriter.Fire(9, 1, new EntityId(1, 2, 3), new EntityId(1, 2, 4), new EntityId(1, 2, 5), new EntityId(1, 2, 77), state.Location, state.Type, state.Velocity, 0);
    Check("Fire length/type", fire.Length == 96 && fire[2] == 2 && fire[3] == 2 && BinaryPrimitives.ReadUInt16BigEndian(fire.AsSpan(34)) == 77);
    var det = DisWriter.Detonation(9, 1, new EntityId(1, 2, 3), new EntityId(1, 2, 4), new EntityId(1, 2, 5), new EntityId(1, 2, 77), state.Velocity, state.Location, state.Type, new Vec3(1, 0, 0), DetonationResult.EntityImpact);
    Check("Detonation length/type/result", det.Length == 104 && det[2] == 3 && det[100] == 1);

    int i = Array.IndexOf(args, "--write-pdus");
    if (i >= 0 && i + 1 < args.Length)
    {
        Directory.CreateDirectory(args[i + 1]);
        File.WriteAllBytes(Path.Combine(args[i + 1], "entity-state.bin"), es);
        File.WriteAllBytes(Path.Combine(args[i + 1], "entity-state-update.bin"), esu);
        File.WriteAllBytes(Path.Combine(args[i + 1], "fire.bin"), fire);
        File.WriteAllBytes(Path.Combine(args[i + 1], "detonation.bin"), det);
    }
}

// Dead reckoning: a viewer that extrapolates the sent states stays within the threshold while traffic stays low.
{
    var tracker = new EntityTracker();
    var sink = new RecordingSink();
    var viewer = new ViewerModel(anchor);
    var t0 = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
    double worst = 0; int samples = 0;
    for (double t = 0; t <= 180; t += 0.05)
    {
        var s = Sim.CirclingAircraft(t, radius: 6000, speed: 230, altitude: 3000, rollStart: 60);
        tracker.BeginFrame();
        tracker.Observe(s, t, t0.AddSeconds(t), anchor, sink);
        tracker.EndFrame(t0.AddSeconds(t), sink);
        foreach (var pdu in sink.Take()) viewer.Apply(pdu, t);
        if (t > 1) { worst = Math.Max(worst, (viewer.Predict(t) - s.Position).Length); samples++; }
    }
    Check("dead-reckoning error bounded", worst < 10 * 1.6, worst.ToString("0.0") + " m");
    double rate = viewer.Received / 180.0;
    Check("turning aircraft rate below cap", rate <= 10.5 && rate > 0.1, rate.ToString("0.00") + " PDU/s");

    tracker = new EntityTracker(); sink = new RecordingSink(); viewer = new ViewerModel(anchor);
    for (double t = 0; t <= 120; t += 0.05)
    {
        var s = Sim.StraightAircraft(t);
        tracker.BeginFrame(); tracker.Observe(s, t, t0.AddSeconds(t), anchor, sink); tracker.EndFrame(t0.AddSeconds(t), sink);
        foreach (var pdu in sink.Take()) viewer.Apply(pdu, t);
    }
    Check("straight flight sends mostly heartbeats", viewer.Received <= 120 / 5.0 + 3, viewer.Received + " PDUs in 120 s");

    tracker.BeginFrame(); tracker.EndFrame(t0, sink);
    var removed = sink.Take().ToList();
    Check("removal sends deactivated state", removed.Count == 1 && (Decode.Entity(removed[0]).Appearance & Appearance.Deactivated) != 0);
}

// CoT additions: anti-air range in the type table, ownship message, structure kinds.
{
    var types = new TypeTable();
    types.Resolve("sam", UnitClass.Ground, "SAM", "Linebreaker SAM");
    int v = types.Version;
    types.SetAirRange("sam", 12000);
    var xml = CotWriter.Session(new SessionInfo { MapName = "Heartland", MapWidth = 81920, MapHeight = 81920 }, anchor, types, DateTime.UtcNow);
    Check("air range published", types.Version > v && xml.Contains("airRange=\"12000\""));
    types.SetAirRange("sam", 12000.4f);
    Check("unchanged air range keeps version", types.Version == v + 1);
    var own = CotWriter.Ownship(new EntityId(1, 1, 42), true, anchor, DateTime.UtcNow);
    var none = CotWriter.Ownship(EntityId.None, false, anchor, DateTime.UtcNow);
    Check("ownship message", own.Contains("b-x-nlf-ownship") && own.Contains("entity=\"1:1:42\"") && none.Contains("entity=\"\""));
    var airbase = CotWriter.Building("nlf-ab-x", ForceId.Friendly, 45, 0, 0, DateTime.UtcNow, TimeSpan.FromHours(1), "North Field", "", "active", "airbase");
    Check("airbase kind", airbase.Contains("kind=\"airbase\"") && airbase.Contains("a-f-G-I"));
    var withRunway = CotWriter.Building("nlf-ab-y", ForceId.Friendly, 45, 0, 0, DateTime.UtcNow, TimeSpan.FromHours(1), "South Field", "", "active", "airbase",
        new[] { new RunwayInfo("09", "27", 45, 0, 10, 45, 0.03, 10, 45, true, true) });
    var runwayDoc = System.Xml.Linq.XDocument.Parse(withRunway).Descendants("runway").Single();
    Check("airbase runway", (string?)runwayDoc.Attribute("name") == "09" && (string?)runwayDoc.Attribute("reverseName") == "27" && (string?)runwayDoc.Attribute("lon2") == "0.0300000" && (string?)runwayDoc.Attribute("landing") == "true");
}

// Viewer commands: target list edits, reverse ID lookup, and the CoT the viewer reads.
{
    Check("parse targets add", TargetCommand.TryParse("targets add 1:1:5 1:1:6 1:1:5 junk", out var add) && add.Op == TargetOp.Add && add.Ids.Count == 2);
    Check("parse targets set empty", TargetCommand.TryParse("targets set", out var set) && set.Op == TargetOp.Set && set.Ids.Count == 0);
    Check("parse targets clear", TargetCommand.TryParse("targets clear", out var clear) && clear.Op == TargetOp.Clear);
    Check("reject unknown commands", !TargetCommand.TryParse("targets fire 1:1:5", out _) && !TargetCommand.TryParse("hello", out _) && !TargetCommand.TryParse("targets remove", out _));
    var many = "targets add " + string.Join(" ", Enumerable.Range(1, 300).Select(i => "1:1:" + i));
    Check("target command capped", TargetCommand.TryParse(many, out var capped) && capped.Ids.Count == TargetCommand.MaxIds);
    var t = new EntityTracker();
    t.BeginFrame();
    var sample = Sim.StraightAircraft(0);
    sample.Key = 777;
    t.Observe(sample, 0, DateTime.UtcNow, anchor, new RecordingSink());
    Check("entity ID maps back to game key", t.TryGetId(777, out var id) && t.TryGetKey(id, out var key) && key == 777 && !t.TryGetKey(new EntityId(9, 9, id.Entity), out _));
    t.Remove(777, DateTime.UtcNow, new RecordingSink());
    Check("removed entity has no key", !t.TryGetKey(id, out _));
    var targets = CotWriter.Targets(new[] { new EntityId(1, 1, 5), new EntityId(1, 1, 9) }, true, anchor, DateTime.UtcNow);
    Check("targets message", targets.Contains("b-x-nlf-targets") && targets.Contains("entities=\"1:1:5 1:1:9\"") && targets.Contains("editable=\"true\""));
}

// Server: origin policy, snapshot ordering and live delivery over a real WebSocket.
await ServerChecks();

Console.WriteLine(failures == 0 ? "PASS " + passed + " checks" : failures + " failed, " + passed + " passed");
return failures == 0 ? 0 : 1;

async Task ServerChecks()
{
    var logs = new List<string>();
    using var server = new FeedServer(new FeedServerOptions { Port = 0, AllowedOrigins = new[] { "https://map.scramd.com", "http://localhost:*" }, AllowMissingOrigin = false }, m => { lock (logs) logs.Add(m); });
    server.Start();
    var info = new SessionInfo { MapName = "Heartland", MapWidth = 81920, MapHeight = 81920 };
    var types = new TypeTable();
    types.Resolve("x", UnitClass.Aircraft, "T-1", "Test Jet");
    server.Session(CotWriter.Session(info, anchor, types, DateTime.UtcNow), true);
    server.Building("nlf-b-1", CotWriter.Building("nlf-b-1", ForceId.Friendly, 45, 0, 10, DateTime.UtcNow, TimeSpan.FromHours(1), "Hangar <A>", "HGR", "active"), false);
    var tracker = new EntityTracker();
    tracker.BeginFrame();
    tracker.Observe(Sim.StraightAircraft(0), 0, DateTime.UtcNow, anchor, server);
    await Task.Delay(100);

    using (var bad = new ClientWebSocket())
    {
        bad.Options.SetRequestHeader("Origin", "https://evil.example");
        bool rejected = false;
        try { await bad.ConnectAsync(new Uri("ws://127.0.0.1:" + server.Port), CancellationToken.None); }
        catch (WebSocketException) { rejected = true; }
        Check("foreign origin rejected", rejected);
    }

    using var ws = new ClientWebSocket();
    ws.Options.SetRequestHeader("Origin", "http://localhost:5173");
    await ws.ConnectAsync(new Uri("ws://127.0.0.1:" + server.Port), CancellationToken.None);
    var first = await Receive(ws); var second = await Receive(ws); var third = await Receive(ws);
    var fourth = await Receive(ws);
    Check("hello first, loopback may control", first.Type == WebSocketMessageType.Text && Encoding.UTF8.GetString(first.Data).Contains("b-x-nlf-hello") && Encoding.UTF8.GetString(first.Data).Contains("control=\"targets\""));
    Check("hello carries the protocol version", Encoding.UTF8.GetString(first.Data).Contains("protocol=\"" + CotWriter.ProtocolVersion + "\""));
    Check("snapshot starts with session", second.Type == WebSocketMessageType.Text && Encoding.UTF8.GetString(second.Data).Contains("b-x-nlf-session"));
    Check("snapshot then building", third.Type == WebSocketMessageType.Text && Encoding.UTF8.GetString(third.Data).Contains("Hangar &lt;A&gt;"));
    Check("snapshot then entity", fourth.Type == WebSocketMessageType.Binary && fourth.Data.Length == 144);

    await ws.SendAsync(Encoding.UTF8.GetBytes("targets add 1:1:1"), WebSocketMessageType.Text, true, CancellationToken.None);
    string? command = null;
    for (int i = 0; i < 50 && !server.TryTakeCommand(out command); i++) await Task.Delay(20);
    Check("viewer command reaches the game queue", command == "targets add 1:1:1", command ?? "none");

    var s = Sim.StraightAircraft(0);
    s.Damage = Appearance.DamageDestroyed;
    tracker.BeginFrame();
    tracker.Observe(s, 0.5, DateTime.UtcNow, anchor, server);
    var live = await Receive(ws);
    Check("live appearance update delivered", live.Type == WebSocketMessageType.Binary && (Decode.Entity(live.Data).Appearance & Appearance.DamageDestroyed) == Appearance.DamageDestroyed);
    Check("one viewer registered", server.ClientCount == 1);
    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    await Task.Delay(200);
    Check("viewer unregistered after close", server.ClientCount == 0);
    lock (logs) Check("origin rejection logged", logs.Any(l => l.Contains("Rejected origin https://evil.example")));

    using var viewOnly = new FeedServer(new FeedServerOptions { Port = 0, Control = ControlAccess.Off }, _ => { });
    viewOnly.Start();
    using var v = new ClientWebSocket();
    await v.ConnectAsync(new Uri("ws://127.0.0.1:" + viewOnly.Port), CancellationToken.None);
    var hello = await Receive(v);
    Check("view-only hello offers no control", Encoding.UTF8.GetString(hello.Data).Contains("control=\"\""));
    await v.SendAsync(Encoding.UTF8.GetBytes("targets clear"), WebSocketMessageType.Text, true, CancellationToken.None);
    await Task.Delay(300);
    Check("view-only server drops commands", !viewOnly.TryTakeCommand(out _));
}

static async Task<(WebSocketMessageType Type, byte[] Data)> Receive(ClientWebSocket ws)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var buffer = new byte[65536];
    var ms = new MemoryStream();
    WebSocketReceiveResult r;
    do { r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token); ms.Write(buffer, 0, r.Count); } while (!r.EndOfMessage);
    return (r.MessageType, ms.ToArray());
}

sealed class RecordingSink : IFeedSink
{
    private readonly List<byte[]> pending = new();
    public void Entity(uint key, byte[] wire, byte[] snapshot) => pending.Add(wire);
    public void Removed(uint key, byte[] wire) => pending.Add(wire);
    public void Event(byte[] wire) => pending.Add(wire);
    public void Building(string uid, string xml, bool removed) { }
    public void Session(string xml, bool reset) { }
    public IEnumerable<byte[]> Take() { var copy = pending.ToArray(); pending.Clear(); return copy; }
}

/// <summary>Independent decoder reading fields at their IEEE 1278.1-2012 offsets.</summary>
static class Decode
{
    public static EntityState Entity(byte[] b)
    {
        var s = new EntityState { Id = Id(b, 12) };
        if (b[2] == DisWriter.EntityStateUpdateType)
        {
            s.Velocity = F3(b, 20); s.Location = D3(b, 32); s.Psi = F(b, 56); s.Theta = F(b, 60); s.Phi = F(b, 64);
            s.Appearance = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(68));
            return s;
        }
        s.Force = (ForceId)b[18];
        s.Type = new EntityType(b[20], b[21], BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(22)), b[24], b[25], b[26], b[27]);
        s.Velocity = F3(b, 36); s.Location = D3(b, 48); s.Psi = F(b, 72); s.Theta = F(b, 76); s.Phi = F(b, 80);
        s.Appearance = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(84));
        s.Algorithm = (DeadReckoning)b[88];
        s.Acceleration = F3(b, 104); s.AngularVelocity = F3(b, 116);
        s.Marking = Encoding.ASCII.GetString(b, 129, 11).TrimEnd('\0');
        return s;
    }
    static EntityId Id(byte[] b, int o) => new(BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o)), BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o + 2)), BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o + 4)));
    static float F(byte[] b, int o) => BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(o));
    static Vec3 F3(byte[] b, int o) => new(F(b, o), F(b, o + 4), F(b, o + 8));
    static Vec3 D3(byte[] b, int o) => new(BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan(o)), BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan(o + 8)), BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan(o + 16)));
}

/// <summary>What a viewer does: keep DR parameters from full states, positions from every state, extrapolate.</summary>
sealed class ViewerModel
{
    private readonly GeoAnchor anchor;
    private Vec3 position, velocity, acceleration; private double at; private DeadReckoning algorithm;
    public int Received;
    public ViewerModel(GeoAnchor anchor) { this.anchor = anchor; }
    public void Apply(byte[] pdu, double now)
    {
        Received++;
        var s = Decode.Entity(pdu);
        position = anchor.EcefToEnu(s.Location);
        velocity = anchor.EcefToEnu(anchor.EnuToEcef(Vec3.Zero) + s.Velocity);
        if (pdu[2] == DisWriter.EntityStateType)
        {
            algorithm = s.Algorithm;
            acceleration = anchor.EcefToEnu(anchor.EnuToEcef(Vec3.Zero) + s.Acceleration);
        }
        at = now;
    }
    public Vec3 Predict(double now)
    {
        double dt = now - at;
        return algorithm switch
        {
            DeadReckoning.Rvw => position + velocity * dt + acceleration * (0.5 * dt * dt),
            DeadReckoning.Fpw => position + velocity * dt,
            _ => position,
        };
    }
}

static class Sim
{
    public static (Vec3 Forward, Vec3 Right, Vec3 Down) Body(double headingDeg, double pitchDeg, double rollDeg)
    {
        double h = headingDeg * Math.PI / 180, p = pitchDeg * Math.PI / 180, r = rollDeg * Math.PI / 180;
        var f = new Vec3(Math.Sin(h) * Math.Cos(p), Math.Cos(h) * Math.Cos(p), Math.Sin(p));
        var right0 = new Vec3(Math.Cos(h), -Math.Sin(h), 0);
        var up0 = right0.Cross(f);
        var right = right0 * Math.Cos(r) - up0 * Math.Sin(r);
        var up = up0 * Math.Cos(r) + right0 * Math.Sin(r);
        return (f, right, -up);
    }

    /// <summary>Straight for rollStart seconds, then a constant right-hand circle.</summary>
    public static UnitSample CirclingAircraft(double t, double radius, double speed, double altitude, double rollStart)
    {
        Vec3 pos, vel; double heading, yawRate = 0, roll = 0;
        if (t < rollStart) { pos = new Vec3(-radius, -speed * (rollStart - t), altitude); vel = new Vec3(0, speed, 0); heading = 0; }
        else
        {
            double w = speed / radius, a = (t - rollStart) * w;
            pos = new Vec3(-radius * Math.Cos(a), radius * Math.Sin(a), altitude);
            vel = new Vec3(speed * Math.Sin(a), speed * Math.Cos(a), 0);
            heading = a * 180 / Math.PI; yawRate = w; roll = Math.Atan(speed * w / 9.81) * 180 / Math.PI;
        }
        var (f, r, d) = Body(heading, 0, roll);
        return new UnitSample
        {
            Key = 1, Class = UnitClass.Aircraft, Type = new EntityType(1, 2, 0, 0, 0, 1, 0), Force = ForceId.Friendly,
            Position = pos, Velocity = vel, HasVelocity = true, Forward = f, Right = r, Down = d,
            AngularVelocity = new Vec3(0, 0, -yawRate), Marking = "TEST",
        };
    }

    public static UnitSample StraightAircraft(double t)
    {
        var (f, r, d) = Body(90, 0, 0);
        return new UnitSample
        {
            Key = 2, Class = UnitClass.Aircraft, Type = new EntityType(1, 2, 0, 0, 0, 1, 0), Force = ForceId.Opposing,
            Position = new Vec3(-20000 + 250 * t, 5000, 6000), Velocity = new Vec3(250, 0, 0), HasVelocity = true,
            Forward = f, Right = r, Down = d, Marking = "BANDIT",
        };
    }
}
