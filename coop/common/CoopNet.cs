using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace ICS2Coop
{
    /// Message ids. One byte at the start of every packet.
    public enum Msg : byte
    {
        Hello = 1,        // client -> host: name
        Welcome = 2,      // host -> client: your player id, host name
        PlayerState = 10, // both ways: one player's position / view (unreliable, sequenced)
        Chat = 20,        // both ways: text line shown to the other player
        TestQuery = 200,  // bot -> game (test builds only): request a state report
        TestReport = 201, // game -> bot: key=value lines
        TestCommand = 202,// bot -> game (test builds only): run a named test action
        WorldSnapshot = 3, // host -> joining client: the whole world as save JSON (FBPP)
        MoneyDelta = 30,  // guest -> host: seq, amount the guest's game added to / took from the money
        MoneyState = 31,  // host -> guest: authoritative total, last MoneyDelta seq applied for this guest
        Clock = 32,       // host -> all: game day, Enviro day/year, hour of day (unreliable, 1/s)
        PlayerHit = 33,   // host -> one guest: one of the host's people hit that guest's body (damage)
        ItemIds = 40,     // host -> joining guest: network ids of the world's items (by save slot / scene path)
        ItemSpawn = 41,   // id, item state (the game's own save keys for one item), held by player or -1
        ItemTake = 42,    // id, player: picked up (shown in that player's hands)
        ItemPlace = 43,   // id, position, rotation, built, kinematic, rotation helper: put down / built
        ItemMove = 44,    // id, position, rotation, final: a thrown / pushed item moving (owner streams)
        ItemDestroy = 45, // id: gone (opened, eaten, sold, put into the inventory ...)
        BoxSpawn = 46,    // id, box prefab index, item tag, position, rotation: a delivery box arrived
        ShopOrder = 47,   // guest -> host: item tags ordered on the guest's in-game shop
        CustomerStates = 50, // host -> guests: customers' position / animation / state (unreliable, chunks)
        RequestOpen = 51,    // host -> guests: a customer asks to use a PC (table id, pc name, hours, extra, kind)
        RequestClose = 52,   // host -> guests: that request is gone (answered, timed out)
        RequestAnswer = 53,  // guest -> host: table id, 0 approve / 1 refuse
        CustomerAction = 54, // guest -> host: customer index, action (1 = cash taken), argument
        TableLinks = 55,     // host -> guests: which customer uses which PC table (1/s)
        Call = 60,           // a mirrored game call: action, seed, target object, arguments (CallSync)
        PrefSet = 61,        // FBPP keys written (progress: quests, rooms, skills ...), both ways
        ActorTable = 62,     // host -> joining guest: scene paths of the town's other people and animals
        ActorStates = 63,    // host -> guests: their on/off, pose, animators, props, ragdoll (unreliable, chunks)
        WatchAnim = 64,      // a scene animator's parameters changed (doors, trays ...), both ways
        WatchField = 65,     // a watched field changed (tip box, cash register, fridge ...), both ways
        WatchActive = 66,    // a watched scene object was shown / hidden (shop shelves ...), both ways
        Elevators = 67,      // host -> guests: elevator cars: position, moving, direction (unreliable, 20/s while one moves)
        Dirt = 68,           // host -> joining guest: the dirt on the floor
    }

    /// Network layer shared by the game (Unity) and the headless test bot (.NET). It knows nothing about the
    /// game: it connects players, finds hosts on the LAN and moves typed messages. The host is player 0.
    public sealed class CoopNet : INetEventListener
    {
        public const int ProtocolVersion = 4;
        public const int DefaultPort = 27815;
        const string ConnectKey = "ics2coop/";
        const string DiscoverAsk = "ICS2COOP?";
        const string DiscoverReply = "ICS2COOP!";

        public readonly NetManager Net;
        public bool IsHost { get; private set; }
        public bool Running => Net.IsRunning;
        public string LocalName = "Player";
        public int LocalId { get; private set; } = -1;
        public Action<string> Log = s => { };

        /// (player id, name) when a player has completed the handshake.
        public event Action<int, string> PlayerJoined;
        public event Action<int, string> PlayerLeft;
        /// (sender player id, message id, payload reader positioned after the id byte).
        public event Action<int, Msg, NetDataReader> Message;
        /// (host name, address) when a LAN host answers a discovery broadcast.
        public event Action<string, IPEndPoint> HostFound;
        public event Action<string> Disconnected;
        /// This device ended its session itself (Stop): LiteNetLib reports no disconnects then, so the session's
        /// leftovers (remote bodies, pending values) are cleared on this.
        public event Action Stopped;

        // peer id (LiteNetLib) <-> player id (ours, stable for the session)
        readonly Dictionary<int, int> m_PlayerOfPeer = new Dictionary<int, int>();
        readonly Dictionary<int, NetPeer> m_PeerOfPlayer = new Dictionary<int, NetPeer>();
        readonly Dictionary<int, string> m_Names = new Dictionary<int, string>();
        readonly NetDataWriter m_Writer = new NetDataWriter();
        int m_NextPlayerId = 1;
        public int MaxPlayers = 4;

        public CoopNet()
        {
            Net = new NetManager(this)
            {
                UnconnectedMessagesEnabled = true,
                BroadcastReceiveEnabled = true,
                DisconnectTimeout = 10000,
                UpdateTime = 15,
                AutoRecycle = true,
                IPv6Enabled = false,
            };
        }

        public IEnumerable<int> Players => m_Names.Keys;
        public string NameOf(int id) => m_Names.TryGetValue(id, out var n) ? n : "?";
        public int PlayerCount => m_Names.Count;

        public bool StartHost(int port = DefaultPort)
        {
            Stop();
            IsHost = true;
            if (!Net.Start(port)) { Log($"cannot listen on UDP {port}"); return false; }
            LocalId = 0;
            m_Names[0] = LocalName;
            Log($"hosting on UDP {Net.LocalPort} as '{LocalName}'");
            return true;
        }

        public bool Join(string address, int port = DefaultPort)
        {
            Stop();
            IsHost = false;
            if (!Net.Start()) { Log("cannot open a UDP socket"); return false; }
            Log($"joining {address}:{port} as '{LocalName}'");
            Net.Connect(address, port, ConnectKey + ProtocolVersion);
            return true;
        }

        /// Asks every host on the LAN to answer (HostFound). Needs a running socket: starts one if idle.
        public void Discover(int port = DefaultPort)
        {
            if (!Net.IsRunning) Net.Start();
            m_Writer.Reset();
            m_Writer.Put(DiscoverAsk);
            Net.SendBroadcast(m_Writer, port);
            // a broadcast to 255.255.255.255 only leaves through the main network card: also send one to each
            // network's own broadcast address, so hosts on virtual LANs (Radmin VPN, Hamachi, ZeroTier) answer
            foreach (var la in LocalAddresses())
            {
                if (la.Broadcast == null) continue;
                try { Net.SendUnconnectedMessage(m_Writer, new IPEndPoint(la.Broadcast, port)); }
                catch (Exception e) { Log($"discovery on {la.Label} failed: {e.Message}"); }
            }
        }

        /// One IPv4 address of this device: which network it is on, and that network's broadcast address.
        public sealed class LocalAddress
        {
            public string Label;
            public IPAddress Address, Broadcast;
            public override string ToString() => Label + ": " + Address;
        }

        /// Every IPv4 address this device has on a working network (not loopback). Virtual LANs are named:
        /// Radmin VPN uses 26.x.x.x, Hamachi 25.x.x.x. Nothing is sent to find them.
        public static List<LocalAddress> LocalAddresses()
        {
            var list = new List<LocalAddress>();
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up &&
                        ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Unknown) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    string desc = (ni.Description ?? "") + " " + (ni.Name ?? "");
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        var a = ua.Address;
                        if (a.AddressFamily != AddressFamily.InterNetwork) continue;
                        var b = a.GetAddressBytes();
                        if (b[0] == 127 || (b[0] == 169 && b[1] == 254)) continue;
                        list.Add(new LocalAddress { Label = NetworkLabel(desc, b), Address = a, Broadcast = BroadcastOf(ua, b) });
                    }
                }
            }
            catch (Exception) { }
            return list;
        }

        static string NetworkLabel(string desc, byte[] b)
        {
            if (desc.IndexOf("Radmin", StringComparison.OrdinalIgnoreCase) >= 0 || b[0] == 26) return "Radmin VPN";
            if (desc.IndexOf("Hamachi", StringComparison.OrdinalIgnoreCase) >= 0 || b[0] == 25) return "Hamachi";
            if (desc.IndexOf("ZeroTier", StringComparison.OrdinalIgnoreCase) >= 0) return "ZeroTier";
            if (desc.IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) >= 0) return "Tailscale";
            return IsPrivate(new IPAddress(b)) ? "Wi-Fi / LAN" : "Network";
        }

        static IPAddress BroadcastOf(System.Net.NetworkInformation.UnicastIPAddressInformation ua, byte[] b)
        {
            byte[] m = null;
            try { m = ua.IPv4Mask?.GetAddressBytes(); } catch (Exception) { }
            if (m == null || m.Length != 4 || (m[0] == 0 && m[1] == 0 && m[2] == 0 && m[3] == 0))
                m = b[0] == 26 || b[0] == 25 || b[0] == 10 ? new byte[] { 255, 0, 0, 0 } : new byte[] { 255, 255, 255, 0 };
            var r = new byte[4];
            for (int i = 0; i < 4; i++) r[i] = (byte)(b[i] | ~m[i]);
            return new IPAddress(r);
        }

        /// Also asks every address of this device's /24 network directly (works where broadcasts are blocked,
        /// e.g. without the iOS multicast entitlement).
        public void DiscoverSweep(int port = DefaultPort)
        {
            var ip = LocalIPv4();
            if (ip == null) return;
            if (!Net.IsRunning) Net.Start();
            var b = ip.GetAddressBytes();
            m_Writer.Reset();
            m_Writer.Put(DiscoverAsk);
            for (int i = 1; i < 255; i++)
            {
                if (i == b[3]) continue;
                Net.SendUnconnectedMessage(m_Writer, new IPEndPoint(new IPAddress(new byte[] { b[0], b[1], b[2], (byte)i }), port));
            }
        }

        /// This device's address on the local network (Wi-Fi), or null. Nothing is sent to find it.
        public static IPAddress LocalIPv4()
        {
            IPAddress best = null;
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up &&
                        ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Unknown) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        var a = ua.Address;
                        if (a.AddressFamily != AddressFamily.InterNetwork || !IsPrivate(a)) continue;
                        // Wi-Fi / Ethernet ("en0", "Ethernet", "Wi-Fi") before anything else (VPNs, hotspots)
                        if (best == null || ni.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase) ||
                            ni.Name.IndexOf("Wi", StringComparison.OrdinalIgnoreCase) >= 0) best = a;
                    }
                }
            }
            catch (Exception) { }
            if (best != null) return best;
            try
            {
                // a UDP "connect" only picks the outgoing interface; no packet leaves the device
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect(new IPAddress(new byte[] { 10, 255, 255, 254 }), 9);
                    var a = ((IPEndPoint)s.LocalEndPoint).Address;
                    if (IsPrivate(a)) return a;
                }
            }
            catch (Exception) { }
            return null;
        }

        public static bool IsPrivate(IPAddress a)
        {
            var b = a.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
        }

        public void Stop()
        {
            bool was = Net.IsRunning;
            if (was) Net.Stop(true);
            m_PlayerOfPeer.Clear();
            m_PeerOfPlayer.Clear();
            m_Names.Clear();
            LocalId = -1;
            m_NextPlayerId = 1;
            if (was) Stopped?.Invoke();
        }

        public void Poll() { if (Net.IsRunning) Net.PollEvents(); }

        // ---- sending --------------------------------------------------------------------------------------
        public NetDataWriter Begin(Msg id)
        {
            m_Writer.Reset();
            m_Writer.Put((byte)id);
            return m_Writer;
        }

        /// Sends the writer from Begin to one player (host: any client; client: 0 = the host).
        public void SendTo(int player, DeliveryMethod how = DeliveryMethod.ReliableOrdered)
        {
            if (IsHost) { if (m_PeerOfPlayer.TryGetValue(player, out var p)) p.Send(m_Writer, how); }
            else if (player == 0 && Net.FirstPeer != null) Net.FirstPeer.Send(m_Writer, how);
        }

        /// Host: to every client (except one). Client: to the host.
        public void SendAll(DeliveryMethod how = DeliveryMethod.ReliableOrdered, int except = -1)
        {
            if (!IsHost) { Net.FirstPeer?.Send(m_Writer, how); return; }
            foreach (var kv in m_PeerOfPlayer)
                if (kv.Key != except) kv.Value.Send(m_Writer, how);
        }

        /// Host only: forwards a received message unchanged to every other client.
        public void Relay(int from, Msg id, NetDataReader payload, DeliveryMethod how)
        {
            if (!IsHost) return;
            m_Writer.Reset();
            m_Writer.Put((byte)id);
            m_Writer.Put(payload.RawData, payload.Position, payload.AvailableBytes);
            SendAll(how, from);
        }

        public int RoundTripMs(int player = 0)
        {
            if (IsHost) return m_PeerOfPlayer.TryGetValue(player, out var p) ? p.Ping * 2 : 0;
            return Net.FirstPeer != null ? Net.FirstPeer.Ping * 2 : 0;
        }

        // ---- INetEventListener ----------------------------------------------------------------------------
        void INetEventListener.OnConnectionRequest(ConnectionRequest request)
        {
            if (!IsHost) { request.Reject(); return; }
            if (m_Names.Count >= MaxPlayers) { Log("join refused: session full"); request.Reject(); return; }
            var peer = request.AcceptIfKey(ConnectKey + ProtocolVersion);
            if (peer == null) Log($"join refused from {request.RemoteEndPoint}: different co-op version");
        }

        void INetEventListener.OnPeerConnected(NetPeer peer)
        {
            if (IsHost) return; // the client says Hello first; the player id is assigned then
            Begin(Msg.Hello).Put(LocalName);
            m_Writer.Put(ProtocolVersion);
            peer.Send(m_Writer, DeliveryMethod.ReliableOrdered);
        }

        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            if (IsHost)
            {
                if (!m_PlayerOfPeer.TryGetValue(peer.Id, out int id)) return;
                string name = NameOf(id);
                m_PlayerOfPeer.Remove(peer.Id);
                m_PeerOfPlayer.Remove(id);
                m_Names.Remove(id);
                Log($"player {id} '{name}' left ({info.Reason})");
                PlayerLeft?.Invoke(id, name);
                return;
            }
            Log($"disconnected from host ({info.Reason})");
            foreach (var kv in new List<KeyValuePair<int, string>>(m_Names))
                if (kv.Key != LocalId) PlayerLeft?.Invoke(kv.Key, kv.Value);
            m_Names.Clear();
            LocalId = -1;
            Disconnected?.Invoke(info.Reason.ToString());
        }

        void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError) => Log($"network error {socketError} ({endPoint})");

        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod how)
        {
            if (reader.AvailableBytes < 1) return;
            var id = (Msg)reader.GetByte();
            if (IsHost)
            {
                if (id == Msg.Hello)
                {
                    string name = Clean(reader.GetString(64));
                    int player = m_NextPlayerId++;
                    m_PlayerOfPeer[peer.Id] = player;
                    m_PeerOfPlayer[player] = peer;
                    m_Names[player] = name;
                    // tell the newcomer who it is and who is here, and everyone else about the newcomer
                    Begin(Msg.Welcome).Put(player);
                    m_Writer.Put(m_Names.Count);
                    foreach (var kv in m_Names) { m_Writer.Put(kv.Key); m_Writer.Put(kv.Value); }
                    peer.Send(m_Writer, DeliveryMethod.ReliableOrdered);
                    Log($"player {player} '{name}' joined from {peer.Address}:{peer.Port}");
                    PlayerJoined?.Invoke(player, name);
                    return;
                }
                if (!m_PlayerOfPeer.TryGetValue(peer.Id, out int from)) return; // no Hello yet
                Message?.Invoke(from, id, reader);
                return;
            }
            if (id == Msg.Welcome)
            {
                LocalId = reader.GetInt();
                int n = reader.GetInt();
                for (int i = 0; i < n; i++)
                {
                    int pid = reader.GetInt();
                    string name = reader.GetString();
                    m_Names[pid] = name;
                    if (pid != LocalId) PlayerJoined?.Invoke(pid, name);
                }
                Log($"joined as player {LocalId}; host is '{NameOf(0)}'");
                return;
            }
            Message?.Invoke(0, id, reader);
        }

        void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remote, NetPacketReader reader, UnconnectedMessageType type)
        {
            string text = reader.AvailableBytes > 0 ? reader.GetString(128) : "";
            if (IsHost && text == DiscoverAsk && Net.IsRunning)
            {
                m_Writer.Reset();
                m_Writer.Put(DiscoverReply);
                m_Writer.Put(LocalName);
                m_Writer.Put(ProtocolVersion);
                Net.SendUnconnectedMessage(m_Writer, remote);
            }
            else if (!IsHost && text == DiscoverReply)
            {
                string name = reader.GetString(64);
                int version = reader.GetInt();
                if (version == ProtocolVersion) HostFound?.Invoke(name, remote);
            }
        }

        void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency) { }

        static string Clean(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) s = "Player";
            return s.Length > 24 ? s.Substring(0, 24) : s;
        }
    }
}
