using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NuclearLiveFeed.Feed
{
    /// <summary>Receives encoded feed messages from the game thread.</summary>
    public interface IFeedSink
    {
        void Entity(uint key, byte[] wire, byte[] snapshot);
        void Removed(uint key, byte[] wire);
        void Event(byte[] wire);
        void Building(string uid, string xml, bool removed);
        void Session(string xml, bool reset);
    }

    /// <summary>Viewer requests for the game thread, already filtered by the viewer's permission.</summary>
    public interface ICommandSource
    {
        bool TryTakeCommand(out string command);
    }

    /// <summary>Which viewers may change game state (the target list). Viewing is unaffected.</summary>
    public enum ControlAccess { Off, ThisPC, Lan }

    public sealed class FeedServerOptions
    {
        public IPAddress ListenAddress = IPAddress.Loopback;
        public int Port = 39391;
        public string[] AllowedOrigins = { "https://map.scramd.com", "http://localhost:*", "http://127.0.0.1:*" };
        public bool AllowMissingOrigin = true;
        public string? UdpHost;
        public int UdpDisPort, UdpCotPort;
        public int MaxClients = 16;
        public ControlAccess Control = ControlAccess.ThisPC;
    }

    /// <summary>
    /// WebSocket server on its own threads. The game thread only enqueues; the server thread keeps the snapshot
    /// state, fans messages out and coalesces movement for slow viewers. Binary frames carry DIS PDUs, text frames CoT XML.
    /// </summary>
    public sealed class FeedServer : IFeedSink, ICommandSource, IDisposable
    {
        private enum Kind : byte { Entity, Removed, Event, Building, Session, Snapshot, Hello }

        private readonly struct Message
        {
            public readonly Kind Kind;
            public readonly uint Key;
            public readonly string? Uid;
            public readonly byte[] Bytes;
            public readonly byte[]? Snapshot;
            public readonly bool Reset;
            public Message(Kind kind, uint key, string? uid, byte[] bytes, byte[]? snapshot, bool reset)
            { Kind = kind; Key = key; Uid = uid; Bytes = bytes; Snapshot = snapshot; Reset = reset; }
            public bool Text => Kind == Kind.Building || Kind == Kind.Session || Kind == Kind.Hello;
        }

        private sealed class Client
        {
            public readonly WebSocket Socket;
            public readonly string Name;
            public readonly object Gate = new object();
            public readonly Queue<Message> Ordered = new Queue<Message>();
            public readonly Dictionary<uint, Message> Movement = new Dictionary<uint, Message>();
            public readonly SemaphoreSlim Signal = new SemaphoreSlim(0);
            public readonly CancellationTokenSource Stop = new CancellationTokenSource();
            public readonly bool CanControl;
            public Client(WebSocket socket, string name, bool canControl) { Socket = socket; Name = name; CanControl = canControl; }
        }

        private readonly FeedServerOptions options;
        private readonly Action<string> log;
        private readonly BlockingCollection<Message> inbox = new BlockingCollection<Message>(new ConcurrentQueue<Message>(), 20000);
        private readonly List<Client> clients = new List<Client>();
        private readonly ConcurrentQueue<string> commands = new ConcurrentQueue<string>();
        private readonly Dictionary<uint, byte[]> entities = new Dictionary<uint, byte[]>();
        private readonly Dictionary<string, byte[]> buildings = new Dictionary<string, byte[]>();
        private readonly Regex[] origins;
        private byte[]? session;
        private TcpListener? listener;
        private UdpClient? udp;
        private IPEndPoint? udpDis, udpCot;
        private Thread? pump, accept;
        private volatile bool running;
        private int dropped;

        public FeedServer(FeedServerOptions options, Action<string> log)
        {
            this.options = options;
            this.log = log;
            origins = options.AllowedOrigins.Where(o => !string.IsNullOrWhiteSpace(o))
                .Select(o => new Regex("^" + Regex.Escape(o.Trim().TrimEnd('/')).Replace("\\*", "[^/]*") + "$", RegexOptions.IgnoreCase))
                .ToArray();
        }

        public int ClientCount { get { lock (clients) return clients.Count; } }
        public int DroppedMessages => dropped;
        /// <summary>UDP output has no connection to observe, so it always counts as an audience.</summary>
        public bool SendsUdp => udp != null;

        public void Start()
        {
            listener = new TcpListener(options.ListenAddress, options.Port);
            listener.Start();
            if (!string.IsNullOrWhiteSpace(options.UdpHost) && (options.UdpDisPort > 0 || options.UdpCotPort > 0))
            {
                var address = Dns.GetHostAddresses(options.UdpHost!).First(a => a.AddressFamily == AddressFamily.InterNetwork);
                udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
                if (options.UdpDisPort > 0) udpDis = new IPEndPoint(address, options.UdpDisPort);
                if (options.UdpCotPort > 0) udpCot = new IPEndPoint(address, options.UdpCotPort);
            }
            running = true;
            pump = new Thread(Pump) { IsBackground = true, Name = "NuclearLiveFeed pump" };
            accept = new Thread(Accept) { IsBackground = true, Name = "NuclearLiveFeed accept" };
            pump.Start();
            accept.Start();
            log("Listening on ws://" + options.ListenAddress + ":" + ((IPEndPoint)listener.LocalEndpoint).Port + (udp != null ? ", UDP to " + options.UdpHost : ""));
        }

        public int Port => listener != null ? ((IPEndPoint)listener.LocalEndpoint).Port : options.Port;

        public void Entity(uint key, byte[] wire, byte[] snapshot) => Post(new Message(Kind.Entity, key, null, wire, snapshot, false));
        public void Removed(uint key, byte[] wire) => Post(new Message(Kind.Removed, key, null, wire, null, false));
        public void Event(byte[] wire) => Post(new Message(Kind.Event, 0, null, wire, null, false));
        public void Building(string uid, string xml, bool removed) => Post(new Message(Kind.Building, 0, uid, Encoding.UTF8.GetBytes(xml), null, removed));
        public void Session(string xml, bool reset) => Post(new Message(Kind.Session, 0, null, Encoding.UTF8.GetBytes(xml), null, reset));

        public bool TryTakeCommand(out string command)
        {
            if (commands.TryDequeue(out var next)) { command = next; return true; }
            command = ""; return false;
        }

        private void Post(Message m)
        {
            if (!running || !inbox.TryAdd(m)) Interlocked.Increment(ref dropped);
        }

        private void Pump()
        {
            try
            {
                foreach (var m in inbox.GetConsumingEnumerable())
                {
                    if (m.Kind == Kind.Snapshot) { RegisterWithSnapshot(); continue; }
                    Apply(m);
                    SendUdp(m);
                    Client[] targets;
                    lock (clients) targets = clients.ToArray();
                    foreach (var c in targets) Enqueue(c, m);
                }
            }
            catch (Exception e) when (!running) { _ = e; }
            catch (Exception e) { log("Pump stopped: " + e); }
        }

        private void Apply(in Message m)
        {
            switch (m.Kind)
            {
                case Kind.Entity: entities[m.Key] = m.Snapshot!; break;
                case Kind.Removed: entities.Remove(m.Key); break;
                case Kind.Building:
                    if (m.Reset) buildings.Remove(m.Uid!); else buildings[m.Uid!] = m.Bytes;
                    break;
                case Kind.Session:
                    if (m.Reset) { entities.Clear(); buildings.Clear(); }
                    session = m.Bytes;
                    break;
            }
        }

        private void SendUdp(in Message m)
        {
            if (udp == null) return;
            var target = m.Text ? udpCot : udpDis;
            if (target == null) return;
            try { udp.Send(m.Bytes, m.Bytes.Length, target); }
            catch (SocketException) { Interlocked.Increment(ref dropped); }
        }

        private static void Enqueue(Client c, in Message m)
        {
            lock (c.Gate)
            {
                switch (m.Kind)
                {
                    case Kind.Entity:
                        // Movement keeps only the newest message per entity; a full state replaces a pending update.
                        if (c.Movement.TryGetValue(m.Key, out var pending) && pending.Bytes.Length > m.Bytes.Length)
                            c.Movement[m.Key] = new Message(Kind.Entity, m.Key, null, m.Snapshot!, m.Snapshot, false);
                        else c.Movement[m.Key] = m;
                        break;
                    case Kind.Removed:
                        c.Movement.Remove(m.Key);
                        c.Ordered.Enqueue(m);
                        break;
                    case Kind.Session when m.Reset:
                        c.Movement.Clear();
                        c.Ordered.Clear();
                        c.Ordered.Enqueue(m);
                        break;
                    default:
                        c.Ordered.Enqueue(m);
                        break;
                }
            }
            if (c.Signal.CurrentCount == 0) c.Signal.Release();
        }

        private void Accept()
        {
            while (running)
            {
                TcpClient tcp;
                try { tcp = listener!.AcceptTcpClient(); }
                catch (Exception) when (!running) { return; }
                catch (Exception e) { log("Accept failed: " + e.Message); continue; }
                _ = Task.Run(() => Handshake(tcp));
            }
        }

        private async Task Handshake(TcpClient tcp)
        {
            string name = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
            try
            {
                tcp.NoDelay = true;
                var stream = tcp.GetStream();
                var headers = await ReadHeaders(stream).ConfigureAwait(false);
                if (headers == null) { tcp.Close(); return; }
                headers.TryGetValue("origin", out var origin);
                if (!headers.TryGetValue("sec-websocket-key", out var key)
                    || !headers.TryGetValue("upgrade", out var upgrade) || !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase))
                {
                    await Reply(stream, "400 Bad Request", "NuclearLiveFeed expects a WebSocket connection.").ConfigureAwait(false);
                    tcp.Close(); return;
                }
                if (!OriginAllowed(origin))
                {
                    log("Rejected origin " + origin + " from " + name);
                    await Reply(stream, "403 Forbidden", "Origin not allowed. Add it to Server / Allowed Origins.").ConfigureAwait(false);
                    tcp.Close(); return;
                }
                lock (clients)
                {
                    if (clients.Count >= options.MaxClients) { tcp.Close(); log("Too many viewers; rejected " + name); return; }
                }
                string accept;
                using (var sha = SHA1.Create())
                    accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                var response = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
                await stream.WriteAsync(response, 0, response.Length).ConfigureAwait(false);
                var socket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(20));
                var remote = tcp.Client.RemoteEndPoint as IPEndPoint;
                bool canControl = options.Control == ControlAccess.Lan
                    || (options.Control == ControlAccess.ThisPC && remote != null && IPAddress.IsLoopback(remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address));
                var client = new Client(socket, name + (origin != null ? " (" + origin + ")" : ""), canControl);
                // Snapshot and registration happen on the pump thread, so no live message is lost or duplicated.
                PostSnapshot(client);
                log("Viewer connected: " + client.Name);
                _ = Task.Run(() => Receive(client));
                await Send(client).ConfigureAwait(false);
            }
            catch (Exception e) { log("Viewer " + name + " failed: " + e.Message); }
            finally { tcp.Close(); }
        }

        private readonly Queue<(Client Client, ManualResetEventSlim Done)> snapshotRequests = new Queue<(Client, ManualResetEventSlim)>();

        /// <summary>Registers a viewer on the pump thread, so its snapshot and the live stream neither overlap nor leave a gap.</summary>
        private void PostSnapshot(Client client)
        {
            using (var done = new ManualResetEventSlim())
            {
                lock (snapshotRequests) snapshotRequests.Enqueue((client, done));
                Post(new Message(Kind.Snapshot, 0, null, Array.Empty<byte>(), null, false));
                done.Wait(TimeSpan.FromSeconds(5));
            }
        }

        private void RegisterWithSnapshot()
        {
            (Client Client, ManualResetEventSlim Done) request;
            lock (snapshotRequests) { if (snapshotRequests.Count == 0) return; request = snapshotRequests.Dequeue(); }
            var c = request.Client;
            lock (c.Gate)
            {
                c.Ordered.Enqueue(new Message(Kind.Hello, 0, null, Encoding.UTF8.GetBytes(CotWriter.Hello(c.CanControl, DateTime.UtcNow)), null, false));
                if (session != null) c.Ordered.Enqueue(new Message(Kind.Session, 0, null, session, null, false));
                foreach (var b in buildings) c.Ordered.Enqueue(new Message(Kind.Building, 0, b.Key, b.Value, null, false));
                foreach (var e in entities) c.Movement[e.Key] = new Message(Kind.Entity, e.Key, null, e.Value, e.Value, false);
            }
            lock (clients) clients.Add(c);
            if (c.Signal.CurrentCount == 0) c.Signal.Release();
            try { request.Done.Set(); } catch (ObjectDisposedException) { }
        }

        private async Task Send(Client c)
        {
            var token = c.Stop.Token;
            try
            {
                while (!token.IsCancellationRequested && c.Socket.State == WebSocketState.Open)
                {
                    await c.Signal.WaitAsync(token).ConfigureAwait(false);
                    while (true)
                    {
                        Message m;
                        lock (c.Gate)
                        {
                            if (c.Ordered.Count > 0) m = c.Ordered.Dequeue();
                            else if (c.Movement.Count > 0)
                            {
                                var first = c.Movement.First();
                                c.Movement.Remove(first.Key);
                                m = first.Value;
                            }
                            else break;
                        }
                        await c.Socket.SendAsync(new ArraySegment<byte>(m.Bytes), m.Text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException || e is WebSocketException || e is IOException || e is ObjectDisposedException) { }
            finally { Drop(c); }
        }

        private async Task Receive(Client c)
        {
            // Reading also keeps ping/pong and close handling alive. Text frames are commands (see DESIGN.md).
            var buffer = new byte[4096];
            var text = new MemoryStream();
            try
            {
                while (c.Socket.State == WebSocketState.Open)
                {
                    var r = await c.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), c.Stop.Token).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        await c.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
                        break;
                    }
                    if (r.MessageType != WebSocketMessageType.Text) continue;
                    if (text.Length + r.Count <= MaxCommandBytes) text.Write(buffer, 0, r.Count);
                    else text.SetLength(MaxCommandBytes + 1);
                    if (!r.EndOfMessage) continue;
                    if (c.CanControl && text.Length <= MaxCommandBytes && commands.Count < 64)
                        commands.Enqueue(Encoding.UTF8.GetString(text.GetBuffer(), 0, (int)text.Length));
                    text.SetLength(0);
                }
            }
            catch (Exception) { }
            finally { Drop(c); }
        }

        private const int MaxCommandBytes = 8192;

        private void Drop(Client c)
        {
            bool removed;
            lock (clients) removed = clients.Remove(c);
            if (!removed) return;
            c.Stop.Cancel();
            try { c.Socket.Abort(); } catch (Exception) { }
            log("Viewer disconnected: " + c.Name);
        }

        private bool OriginAllowed(string? origin)
        {
            if (string.IsNullOrEmpty(origin)) return options.AllowMissingOrigin;
            return origins.Any(r => r.IsMatch(origin!));
        }

        private static async Task<Dictionary<string, string>?> ReadHeaders(NetworkStream stream)
        {
            var data = new List<byte>(1024);
            var one = new byte[1];
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                while (data.Count < 16384)
                {
                    int n = await stream.ReadAsync(one, 0, 1, timeout.Token).ConfigureAwait(false);
                    if (n == 0) return null;
                    data.Add(one[0]);
                    int c = data.Count;
                    if (c >= 4 && data[c - 4] == 13 && data[c - 3] == 10 && data[c - 2] == 13 && data[c - 1] == 10) break;
                }
            }
            var lines = Encoding.ASCII.GetString(data.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0 || !lines[0].StartsWith("GET ", StringComparison.Ordinal)) return null;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                int i = line.IndexOf(':');
                if (i > 0) headers[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
            return headers;
        }

        private static async Task Reply(NetworkStream stream, string status, string body)
        {
            var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: text/plain\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n" + body);
            await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (!running) return;
            running = false;
            try { listener?.Stop(); } catch (Exception) { }
            inbox.CompleteAdding();
            Client[] all;
            lock (clients) { all = clients.ToArray(); clients.Clear(); }
            foreach (var c in all) { c.Stop.Cancel(); try { c.Socket.Abort(); } catch (Exception) { } }
            pump?.Join(1000);
            udp?.Dispose();
        }
    }
}
