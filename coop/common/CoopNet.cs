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
    }

    /// Network layer shared by the game (Unity) and the headless test bot (.NET). It knows nothing about the
    /// game: it connects players, finds hosts on the LAN and moves typed messages. The host is player 0.
    public sealed class CoopNet : INetEventListener
    {
        public const int ProtocolVersion = 1;
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
        }

        public void Stop()
        {
            if (Net.IsRunning) Net.Stop(true);
            m_PlayerOfPeer.Clear();
            m_PeerOfPlayer.Clear();
            m_Names.Clear();
            LocalId = -1;
            m_NextPlayerId = 1;
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
