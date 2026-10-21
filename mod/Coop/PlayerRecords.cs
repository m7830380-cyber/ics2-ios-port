using System;
using System.Collections.Generic;
using System.IO;
using ICS2Coop;
using ICS2Shim;
using LiteNetLib.Utils;
using UnityEngine;

namespace ICS2iOS
{
    // Each guest's own state - where they stand, the PC they sit at, health, energy, hunger, sleep and their
    // pockets - kept by the host, so a guest who drops out or leaves comes back the way they left. Guests send it
    // every few seconds; the host keeps one record per device in its save ("coopplayer/<device key>"), so the
    // records also go into the host's save files: loading a save brings every player back as they were then.
    // Joining, the guest finds its record in the host's world (WorldSnapshot takes the stats and pockets from it)
    // and is put back at its place, and at its PC, once the cafe is up.
    public class PlayerRecords : MonoBehaviour
    {
        public const string Prefix = "coopplayer/";
        const float SendInterval = 3f;

        [Serializable]
        public class Record
        {
            public string Key, Name;
            public float X, Y, Z, Yaw;
            public bool AtPc;
            public float SeatX, SeatY, SeatZ;
            public float Health = 100f;
            public WorldSnapshot.Model Prefs = new WorldSnapshot.Model();
        }

        static readonly Dictionary<int, string> s_KeyOf = new Dictionary<int, string>();   // host: player -> device key
        static Record s_Pending;        // guest: to put back once the cafe is up
        static string s_DeviceKey;
        float m_NextSend;

        void OnEnable()
        {
            CoopSession.Received += OnMessage;
            CoopSession.Net.PlayerLeft += OnPlayerLeft;
            CoopSession.Net.Stopped += OnStopped;
            CoopSession.Net.Disconnected += OnDisconnected;
        }

        void OnDisable()
        {
            CoopSession.Received -= OnMessage;
            CoopSession.Net.PlayerLeft -= OnPlayerLeft;
            CoopSession.Net.Stopped -= OnStopped;
            CoopSession.Net.Disconnected -= OnDisconnected;
        }

        void OnStopped() => s_KeyOf.Clear();
        void OnDisconnected(string reason) => s_Pending = null;

        /// This device's id for the host's records: made once, kept next to the saves.
        public static string DeviceKey
        {
            get
            {
                if (s_DeviceKey != null) return s_DeviceKey;
                string path = Path.Combine(Platform.PersistentDataPath, "coop-player-id.txt");
                try
                {
                    if (File.Exists(path)) s_DeviceKey = File.ReadAllText(path).Trim();
                    if (string.IsNullOrEmpty(s_DeviceKey) || s_DeviceKey.Length > 40)
                    {
                        s_DeviceKey = Guid.NewGuid().ToString("N");
                        File.WriteAllText(path, s_DeviceKey);
                    }
                }
                catch (Exception e)
                {
                    Log.Warn("coop: player id file: " + e.Message);
                    s_DeviceKey = s_DeviceKey ?? Guid.NewGuid().ToString("N");
                }
                // test copies on one PC share the folder: the name tells them apart
                string name = Platform.CommandLineValue("-ics2name");
                if (!string.IsNullOrEmpty(name)) s_DeviceKey += "-" + name;
                return s_DeviceKey;
            }
        }

        // ---- guest ------------------------------------------------------------------------------------------
        /// This device's record in the host's world, or null.
        public static Record Find(WorldSnapshot.Model world)
        {
            string key = Prefix + DeviceKey;
            foreach (var x in world.StringData ?? new WorldSnapshot.StringItem[0])
            {
                if (x.Key != key) continue;
                try { return JsonUtility.FromJson<Record>(x.Value); }
                catch (Exception e) { Log.Warn("coop: own record unreadable: " + e.Message); return null; }
            }
            return null;
        }

        public static void SetPending(Record r)
        {
            s_Pending = r;
            Log.Info(r != null ? $"coop: the host kept this player's state; restoring it (at a PC: {r.AtPc})" : "coop: first visit to this world");
        }

        void Update()
        {
            if (!CoopSession.IsClient || !CoopSession.WorldReady) return;
            try
            {
                if (s_Pending != null)
                {
                    var r = s_Pending;
                    s_Pending = null;
                    Restore(r);
                    m_NextSend = Time.unscaledTime + SendInterval;
                    return;
                }
                if (Time.unscaledTime < m_NextSend) return;
                m_NextSend = Time.unscaledTime + SendInterval;
                var rec = Build();
                if (rec == null) return;
                var w = CoopSession.Net.Begin(Msg.PlayerRecord);
                w.PutLargeString(JsonUtility.ToJson(rec));
                CoopSession.Net.SendTo(0);
            }
            catch (Exception e)
            {
                Log.Error("coop: player record: " + e);
                m_NextSend = Time.unscaledTime + 30f;
            }
        }

        static Record Build()
        {
            var gm = GameManager.Instance;
            var ray = gm != null ? gm.playerRay : null;
            if (ray == null || gm.player == null) return null;
            var rec = new Record { Key = DeviceKey, Name = CoopSession.Net.LocalName, Health = ray.health };
            var t = gm.player;
            rec.X = t.position.x; rec.Y = t.position.y; rec.Z = t.position.z; rec.Yaw = t.eulerAngles.y;
            var pc = ray.currentPlayerComputer;
            if (pc != null && pc.enterPc && pc.chairPos != null)
            {
                rec.AtPc = true;
                rec.SeatX = pc.chairPos.position.x; rec.SeatY = pc.chairPos.position.y; rec.SeatZ = pc.chairPos.position.z;
            }
            // the pockets as the game saves them (into memory only; the session save file is not written)
            var sm = SaveManager.Instance;
            if (sm != null && sm.inventory != null) sm.inventory.SaveAllSlots();
            var all = JsonUtility.FromJson<WorldSnapshot.Model>(FBPP.GetSaveFileAsJson()) ?? new WorldSnapshot.Model();
            rec.Prefs.StringData = Array.FindAll(all.StringData ?? new WorldSnapshot.StringItem[0], x => WorldSnapshot.IsInventoryKey(x.Key));
            rec.Prefs.IntData = Array.FindAll(all.IntData ?? new WorldSnapshot.IntItem[0], x => WorldSnapshot.IsInventoryKey(x.Key));
            rec.Prefs.BoolData = Array.FindAll(all.BoolData ?? new WorldSnapshot.BoolItem[0], x => WorldSnapshot.IsInventoryKey(x.Key));
            var floats = new List<WorldSnapshot.FloatItem>(Array.FindAll(all.FloatData ?? new WorldSnapshot.FloatItem[0], x => WorldSnapshot.IsInventoryKey(x.Key)));
            // energy, hunger and sleep as they are now (the game writes them to the save only when it saves)
            floats.Add(new WorldSnapshot.FloatItem { Key = "playerstatenergy", Value = ray.statEnergy });
            floats.Add(new WorldSnapshot.FloatItem { Key = "playerstathungery", Value = ray.statHunger });
            floats.Add(new WorldSnapshot.FloatItem { Key = "playerstatsleep", Value = ray.statSleep });
            rec.Prefs.FloatData = floats.ToArray();
            return rec;
        }

        /// Back where the record says: place, facing, health, and the PC if nobody else sits at it now.
        static void Restore(Record r)
        {
            var gm = GameManager.Instance;
            var ray = gm != null ? gm.playerRay : null;
            if (ray == null || gm.player == null) return;
            var t = gm.player;
            var cc = t.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            t.position = new Vector3(r.X, r.Y, r.Z);
            t.rotation = Quaternion.Euler(0f, r.Yaw, 0f);
            if (cc != null) cc.enabled = true;
            if (r.Health > 0f) ray.health = Mathf.Min(r.Health, 100f);
            Log.Info($"coop: put back at {t.position} health={ray.health:0}");
            string msg = "Welcome back: your place and things are as you left them";
            if (r.AtPc)
            {
                PlayerComputer best = null;
                float bestDist = 1.5f * 1.5f;
                var seat = new Vector3(r.SeatX, r.SeatY, r.SeatZ);
                foreach (var pc in FindObjectsOfType<PlayerComputer>())
                {
                    if (pc.chairPos == null) continue;
                    var off = pc.chairPos.position - seat; off.y = 0f;   // the seat's height is the sitter's feet
                    float d = off.sqrMagnitude;
                    if (d < bestDist) { bestDist = d; best = pc; }
                }
                if (best != null && !best.enterPc)
                {
                    string who = PlayerSync.SeatTakenBy(best);
                    if (who == null)
                    {
                        ray.currentPlayerComputer = best;
                        best.EnterComputer();
                        Log.Info($"coop: back at PC '{best.name}'");
                    }
                    else msg = $"Welcome back ({who} is at your PC now)";
                }
            }
            DebugOverlay.Toast(msg);
        }

        // ---- host -------------------------------------------------------------------------------------------
        void OnMessage(int from, Msg id, NetDataReader r)
        {
            if (id != Msg.PlayerRecord || !CoopSession.IsHost) return;
            string json = r.GetLargeString();
            Record rec;
            try { rec = JsonUtility.FromJson<Record>(json); }
            catch (Exception e) { Log.Warn($"coop: record from player {from} unreadable: {e.Message}"); return; }
            if (rec == null || string.IsNullOrEmpty(rec.Key) || rec.Key.Length > 64) return;
            if (!s_KeyOf.ContainsKey(from)) Log.Info($"coop: keeping the state of player {from} '{CoopSession.Net.NameOf(from)}'");
            s_KeyOf[from] = rec.Key;
            rec.Name = CoopSession.Net.NameOf(from);
            Store(rec);
        }

        /// Into the host's save (memory; it reaches the file when the game saves). PrefSync does not share these keys.
        static void Store(Record rec) => FBPP.SetString(Prefix + rec.Key, JsonUtility.ToJson(rec));

        /// A guest left (or dropped out): its place as last seen, which is newer than its last record.
        void OnPlayerLeft(int player, string name)
        {
            if (!CoopSession.IsHost || !s_KeyOf.TryGetValue(player, out string key)) return;
            s_KeyOf.Remove(player);
            try
            {
                var rec = JsonUtility.FromJson<Record>(FBPP.GetString(Prefix + key));
                if (rec == null) return;
                if (PlayerSync.LastState(player, out var s))
                {
                    rec.X = s.X; rec.Y = s.Y; rec.Z = s.Z; rec.Yaw = s.Yaw;
                    rec.AtPc = (s.Flags & PlayerFlags.AtComputer) != 0;
                    if (rec.AtPc) { rec.SeatX = s.SeatX; rec.SeatY = s.SeatY; rec.SeatZ = s.SeatZ; }
                }
                Store(rec);
                Log.Info($"coop: kept the state of '{name}' for when they come back (at a PC: {rec.AtPc})");
            }
            catch (Exception e) { Log.Warn("coop: record of a leaving player: " + e.Message); }
        }
    }
}
