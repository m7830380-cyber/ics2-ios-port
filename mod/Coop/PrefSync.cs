using System.Collections.Generic;
using ICS2Coop;
using ICS2Shim;
using LiteNetLib.Utils;
using UnityEngine;

namespace ICS2iOS
{
    // The game keeps its progress in FBPP (quests, rooms bought, skills, coins, family, staff, the in-game
    // PC's apps ...). Every write on one device is sent to the others (the patched FileBasedPrefs reports it),
    // a few times a second, last value per key. Not shared: each device's own settings, its inventory, the
    // player's own body (energy, hunger, sleep), and the per-item save slots (items have their own sync).
    public class PrefSync : MonoBehaviour
    {
        const float SendInterval = 0.15f;
        const int ChunkBytes = 1100;
        const byte Del = 0, Str = 1, Int = 2, Flt = 3, Bln = 4;

        static readonly Dictionary<string, object> s_Dirty = new Dictionary<string, object>();
        static readonly Dictionary<string, object> s_Known = new Dictionary<string, object>();
        static readonly object Deleted = new object();
        static bool s_Applying;
        static int s_Sent, s_Received;
        float m_Next;

        static readonly HashSet<string> LocalKeys = new HashSet<string>
        {
            "language", "FirstRun", "AA", "AO", "Bloom", "ChromaticAberration", "DOF", "FPS", "FullScreen", "Grass",
            "MotionBlur", "Reflections", "SSR", "Shadows", "SoftParticles", "Terrain", "TextureAnistropic",
            "TextureResolution", "VSync", "Vignette", "stSSR",
        };

        static readonly string[] LocalPrefixes = { "option", "saveslot", "playerstat" };

        /// The item save slots ("posX12" ...) and delivery box slots ("zamboxPosX3" ...).
        static readonly string[] SlotPrefixes =
        {
            "itemsaver", "posX", "posY", "posZ", "rotX", "rotY", "rotZ", "buildedValue", "rbKinematic", "backpackitemsave",
            "itemquaternionYSaver", "baseballBatDurability", "climateDegree", "climatecurrentchangedvalue", "climateisworking",
            "climateautomod", "fanisworking", "itemnamesave", "extinguisherfuel", "sprayCount", "minerriggraphiccardcount",
            "minerrigisworking", "btcminerisworking", "hasitem", "zambox",
        };

        public static bool IsDeviceSetting(string key) =>
            LocalKeys.Contains(key) || key.StartsWith("option", System.StringComparison.Ordinal);

        public static bool IsShared(string key)
        {
            if (string.IsNullOrEmpty(key) || LocalKeys.Contains(key)) return false;
            foreach (var p in LocalPrefixes) if (key.StartsWith(p, System.StringComparison.Ordinal)) return false;
            if (key.Length > 0 && char.IsDigit(key[key.Length - 1]))
                foreach (var p in SlotPrefixes) if (key.StartsWith(p, System.StringComparison.Ordinal)) return false;
            return true;
        }

        void OnEnable()
        {
            Platform.PrefSetHandler = OnSet;
            CoopSession.Received += OnMessage;
            CoopSession.Net.Disconnected += _ => Clear();
            CoopSession.Net.Stopped += Clear;
        }

        public static void Clear() { s_Dirty.Clear(); s_Known.Clear(); }

        static void OnSet(string key, object value)
        {
            if (s_Applying || CallSync.Replaying || !CoopSession.Active) return;
            // a guest writes only into its session save; before that (still in its own save) nothing is shared
            if (CoopSession.IsClient && !WorldSnapshot.InSessionSave) return;
            if (!IsShared(key)) return;
            object v = value ?? Deleted;
            if (s_Known.TryGetValue(key, out var known) && Equals(known, v)) return;
            s_Known[key] = v;
            s_Dirty[key] = v;
        }

        void Update()
        {
            if (!CoopSession.Active || s_Dirty.Count == 0 || Time.unscaledTime < m_Next) return;
            // a guest's cafe writes its loaded values while it starts: those are the host's already
            if (CoopSession.IsClient && !ItemSync.Ready) { s_Dirty.Clear(); return; }
            m_Next = Time.unscaledTime + SendInterval;
            NetDataWriter w = null;
            int count = 0, countPos = 0;
            foreach (var kv in s_Dirty)
            {
                if (w == null) { w = CoopSession.Net.Begin(Msg.PrefSet); countPos = w.Length; w.Put((short)0); count = 0; }
                w.Put(kv.Key);
                switch (kv.Value)
                {
                    case string s: w.Put(Str); w.Put(s); break;
                    case int i: w.Put(Int); w.Put(i); break;
                    case float f: w.Put(Flt); w.Put(f); break;
                    case bool b: w.Put(Bln); w.Put(b); break;
                    default: w.Put(Del); break;
                }
                count++;
                if (w.Length > ChunkBytes) { Flush(w, countPos, count, -1); w = null; }
            }
            if (w != null) Flush(w, countPos, count, -1);
            s_Sent += s_Dirty.Count;
            s_Dirty.Clear();
        }

        static void Flush(NetDataWriter w, int countPos, int count, int except)
        {
            w.Data[countPos] = (byte)(count & 0xff);
            w.Data[countPos + 1] = (byte)(count >> 8);
            CoopSession.Net.SendAll(LiteNetLib.DeliveryMethod.ReliableOrdered, except);
        }

        void OnMessage(int from, Msg id, NetDataReader r)
        {
            if (id != Msg.PrefSet) return;
            // a guest applies nothing until it plays in the host's world (session save)
            if (CoopSession.IsClient && !WorldSnapshot.InSessionSave) return;
            byte[] raw = r.GetRemainingBytes();
            if (CoopSession.IsHost && CoopSession.Net.PlayerCount > 2)
            {
                var w = CoopSession.Net.Begin(Msg.PrefSet);
                w.Put(raw);
                CoopSession.Net.SendAll(LiteNetLib.DeliveryMethod.ReliableOrdered, from);
            }
            var rr = new NetDataReader(raw);
            int n = rr.GetShort();
            s_Applying = true;
            try
            {
                for (int k = 0; k < n; k++)
                {
                    string key = rr.GetString();
                    byte type = rr.GetByte();
                    object v;
                    switch (type)
                    {
                        case Str: v = rr.GetString(); FBPP.SetString(key, (string)v); break;
                        case Int: v = rr.GetInt(); FBPP.SetInt(key, (int)v); break;
                        case Flt: v = rr.GetFloat(); FBPP.SetFloat(key, (float)v); break;
                        case Bln: v = rr.GetBool(); FBPP.SetBool(key, (bool)v); break;
                        default: v = Deleted; FBPP.DeleteKey(key); break;
                    }
                    s_Known[key] = v;
                    s_Dirty.Remove(key);   // theirs is newer than ours
                    s_Received++;
                }
            }
            catch (System.Exception e) { Log.Error("coop: applying shared progress failed: " + e); }
            finally { s_Applying = false; }
        }

        public static void Report(System.Text.StringBuilder sb)
        {
            sb.Append("prefs.sent=").Append(s_Sent).Append('\n');
            sb.Append("prefs.received=").Append(s_Received).Append('\n');
        }
    }
}
