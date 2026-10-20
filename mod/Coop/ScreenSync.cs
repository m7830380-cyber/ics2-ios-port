using System.Collections.Generic;
using DG.Tweening;
using ICS2Coop;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ICS2iOS
{
    // The office PC's screen is part of the world: what the player sitting at it opens (windows, app pages,
    // the boot screen, the taskbar, text typed into fields) shows on the same monitor for everybody else.
    //
    // The screen is a big UI tree. Every device lists the PC's UI objects right when the cafe scene loads
    // (before anything opened or reordered windows), so object i is the same one everywhere; a hash of the
    // names checks that. The player at the PC sends which objects are shown, the window order, sizes and
    // taskbar icons, and the text fields; the others show it, unless they sit at that PC themselves. The host
    // repeats the screen every few seconds while nobody of its own sits there, so a guest that joins later
    // sees it too.
    public class ScreenSync : MonoBehaviour
    {
        const float ScanInterval = 0.2f, DriverRepeat = 2f, HostRepeat = 5f;
        const int MaxNodes = 60000;

        sealed class Screen
        {
            public Computer Pc;
            public Transform[] Nodes;
            public Dictionary<Transform, int> Index;
            public InputField[] Fields;
            public int Hash;
            public byte[] LastSent;
            public float NextRepeat;
            public bool WarnedMismatch;
        }

        static readonly List<Screen> s_Screens = new List<Screen>();
        static int s_Sent, s_Applied;
        float m_NextScan;

        void OnEnable()
        {
            CoopSession.Received += OnMessage;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        /// Runs before the objects' Start and before the save loads (0.5 s later): the tree is as built.
        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            s_Screens.Clear();
            if (scene.name != "MainScene") return;
            try
            {
                var pcs = new List<Computer>();
                foreach (var c in FindObjectsOfType<Computer>(true)) if (c.gameObject.scene.IsValid()) pcs.Add(c);
                pcs.Sort((a, b) => string.CompareOrdinal(NetRef.PathOf(a.transform), NetRef.PathOf(b.transform)));
                foreach (var pc in pcs)
                {
                    var nodes = pc.GetComponentsInChildren<Transform>(true);
                    if (nodes.Length > MaxNodes) { Log.Warn($"coop: PC screen {pc.name} has {nodes.Length} parts: not shared"); continue; }
                    var s = new Screen { Pc = pc, Nodes = nodes, Index = new Dictionary<Transform, int>(nodes.Length) };
                    int hash = nodes.Length;
                    for (int i = 0; i < nodes.Length; i++)
                    {
                        s.Index[nodes[i]] = i;
                        hash = hash * 31 + StableHash(nodes[i].name);
                    }
                    s.Hash = hash;
                    s.Fields = pc.GetComponentsInChildren<InputField>(true);
                    s_Screens.Add(s);
                    Log.Info($"coop: PC screen {pc.name}: {nodes.Length} parts, {s.Fields.Length} text fields");
                }
            }
            catch (System.Exception e) { Log.Error("coop: listing the PC screen failed: " + e); s_Screens.Clear(); }
        }

        static int StableHash(string s)
        {
            int h = 17;
            foreach (char ch in s) h = h * 31 + ch;
            return h;
        }

        /// The local player sits at this PC.
        static bool AtPc(Computer pc)
        {
            var ray = GameManager.Instance != null ? GameManager.Instance.playerRay : null;
            var at = ray != null ? ray.currentPlayerComputer : null;
            return at != null && at.enterPc && (at.computer == pc || pc.playerComputer == at);
        }

        void Update()
        {
            if (!CoopSession.WorldReady || CoopSession.Net.PlayerCount < 2 || s_Screens.Count == 0) return;
            float now = Time.unscaledTime;
            if (now < m_NextScan) return;
            m_NextScan = now + ScanInterval;
            for (int i = 0; i < s_Screens.Count; i++)
            {
                var s = s_Screens[i];
                if (s.Pc == null) continue;
                try
                {
                    bool driving = AtPc(s.Pc);
                    if (!driving && !CoopSession.IsHost) continue;
                    byte[] data = Capture(s);
                    bool changed = driving && (s.LastSent == null || !Same(data, s.LastSent));
                    if (!changed && now < s.NextRepeat) continue;
                    s.NextRepeat = now + (driving ? DriverRepeat : HostRepeat);
                    s.LastSent = data;
                    var w = CoopSession.Net.Begin(Msg.ScreenState);
                    w.Put((byte)i);
                    w.Put(s.Hash);
                    w.Put(data.Length);
                    w.Put(data);
                    CoopSession.Net.SendAll();
                    s_Sent++;
                }
                catch (System.Exception e) { Log.Error("coop: PC screen sync: " + e); s.NextRepeat = now + 10f; }
            }
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ---- the screen as bytes ---------------------------------------------------------------------------
        static byte[] Capture(Screen s)
        {
            var w = new NetDataWriter();
            var pc = s.Pc;
            w.Put(pc.playerComputer != null && pc.playerComputer.computerIsStarted);
            // shown / hidden, one bit per object
            var bits = new byte[(s.Nodes.Length + 7) / 8];
            for (int i = 0; i < s.Nodes.Length; i++)
                if (s.Nodes[i] != null && s.Nodes[i].gameObject.activeSelf) bits[i >> 3] |= (byte)(1 << (i & 7));
            w.Put(bits.Length);
            w.Put(bits);
            // windows: size (open / closing animation), taskbar icon, minimized
            var panels = pc.allPanels ?? new PanelStats[0];
            w.Put((short)panels.Length);
            foreach (var p in panels)
            {
                if (p == null) { w.Put(0f); w.Put((byte)0); continue; }
                w.Put(p.transform.localScale.x);
                byte f = 0;
                if (p.panelbottomIcon != null) { f |= 1; if (p.panelbottomIcon.panelminimized) f |= 2; }
                w.Put(f);
            }
            // window order: the windows' parent's children, by object index
            var parent = panels.Length > 0 && panels[0] != null ? panels[0].transform.parent : null;
            var order = new List<int>();
            if (parent != null)
                for (int c = 0; c < parent.childCount; c++)
                    if (s.Index.TryGetValue(parent.GetChild(c), out int idx)) order.Add(idx);
            w.Put((short)order.Count);
            foreach (int idx in order) w.Put(idx);
            // typed text
            w.Put((short)s.Fields.Length);
            foreach (var f in s.Fields) w.Put(f != null ? f.text ?? "" : "");
            return w.CopyData();
        }

        void OnMessage(int from, Msg id, NetDataReader r)
        {
            if (id != Msg.ScreenState) return;
            int i = r.GetByte();
            int hash = r.GetInt();
            var data = new byte[r.GetInt()];
            r.GetBytes(data, data.Length);
            if (!CoopSession.WorldReady || i >= s_Screens.Count) return;
            var s = s_Screens[i];
            if (s.Pc == null || AtPc(s.Pc)) return;   // sitting there: this player's own use counts
            if (hash != s.Hash)
            {
                if (!s.WarnedMismatch) Log.Warn($"coop: PC screen {s.Pc.name} differs from {CoopSession.Net.NameOf(from)}'s game: not shared");
                s.WarnedMismatch = true;
                return;
            }
            try
            {
                Apply(s, new NetDataReader(data));
                s_Applied++;
                // the host now shows it, so its repeats carry it on to other guests
                if (CoopSession.IsHost) s.NextRepeat = 0f;
            }
            catch (System.Exception e) { Log.Error("coop: showing the PC screen failed: " + e); }
        }

        static void Apply(Screen s, NetDataReader r)
        {
            var pc = s.Pc;
            bool started = r.GetBool();
            if (pc.playerComputer != null) pc.playerComputer.computerIsStarted = started;
            var bits = new byte[r.GetInt()];
            r.GetBytes(bits, bits.Length);
            int n = Mathf.Min(s.Nodes.Length, bits.Length * 8);
            for (int k = 0; k < n; k++)
            {
                var t = s.Nodes[k];
                if (t == null) continue;
                bool on = (bits[k >> 3] & (1 << (k & 7))) != 0;
                if (t.gameObject.activeSelf == on) continue;
                try { t.gameObject.SetActive(on); }
                catch (System.Exception e) { Log.Warn($"coop: PC screen part {t.name}: {e.Message}"); }
            }
            var panels = pc.allPanels ?? new PanelStats[0];
            int count = r.GetShort();
            for (int k = 0; k < count; k++)
            {
                float scale = r.GetFloat();
                byte f = r.GetByte();
                var p = k < panels.Length ? panels[k] : null;
                if (p == null) continue;
                if (Mathf.Abs(p.transform.localScale.x - scale) > 0.02f)
                {
                    p.transform.DOKill();
                    p.transform.localScale = Vector3.one * scale;
                }
                SetIcon(pc, p, (f & 1) != 0, (f & 2) != 0);
            }
            int orderCount = r.GetShort();
            var parent = panels.Length > 0 && panels[0] != null ? panels[0].transform.parent : null;
            for (int k = 0; k < orderCount; k++)
            {
                int idx = r.GetInt();
                var t = idx >= 0 && idx < s.Nodes.Length ? s.Nodes[idx] : null;
                if (t != null && parent != null && t.parent == parent) t.SetAsLastSibling();
            }
            int fields = r.GetShort();
            for (int k = 0; k < fields; k++)
            {
                string text = r.GetString();
                var f = k < s.Fields.Length ? s.Fields[k] : null;
                if (f != null && f.text != text) f.SetTextWithoutNotify(text);
            }
        }

        /// The taskbar icon of an open window, made the way Computer.OpenPanel makes it.
        static void SetIcon(Computer pc, PanelStats p, bool has, bool minimized)
        {
            if (has && p.panelbottomIcon == null && pc.panelIcon != null && pc.panelIconParent != null)
            {
                var go = Instantiate(pc.panelIcon.gameObject, pc.panelIconParent);
                var icon = go.GetComponent<PanelBottomIcon>();
                icon._panelName = p.panelName;
                icon.iconImage.sprite = p.panelIcon;
                icon.panel = p.gameObject;
                p.panelbottomIcon = icon;
            }
            else if (!has && p.panelbottomIcon != null)
            {
                Destroy(p.panelbottomIcon.gameObject);
                p.panelbottomIcon = null;
            }
            if (p.panelbottomIcon != null) p.panelbottomIcon.panelminimized = minimized;
        }

        public static void Report(System.Text.StringBuilder sb)
        {
            sb.Append("screen.count=").Append(s_Screens.Count).Append('\n');
            sb.Append("screen.sent=").Append(s_Sent).Append('\n');
            sb.Append("screen.applied=").Append(s_Applied).Append('\n');
        }
    }
}
