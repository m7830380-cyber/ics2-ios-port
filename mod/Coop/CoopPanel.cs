using System;
using System.Collections.Generic;
using System.Net;
using ICS2Coop;
using ICS2Shim;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ICS2iOS
{
    // "Co-op" panel (button next to "iOS" on the main menu and in the pause menu): name, host, find games on
    // the Wi-Fi, join by address, leave. Drawn with IMGUI like the iOS settings panel; touches come from
    // TouchInput, mouse clicks (Windows test copies) from OnGUI.
    public class CoopPanel : MonoBehaviour
    {
        public static bool Open;

        sealed class Btn { public Rect Rect; public string Label; public Action OnTap; public bool Primary; }

        static readonly List<Btn> s_Buttons = new List<Btn>();
        static readonly List<string> s_Lines = new List<string>();
        static readonly Dictionary<string, (string name, float seen)> s_Found = new Dictionary<string, (string, float)>();
        static Rect s_Close;
        static float s_NextSearch;
        static string s_Editing;            // "name" / "address" while typing
        static TouchScreenKeyboard s_Keyboard;
        static string s_Typed = "";
        static string s_Status = "";
        GUIStyle m_Title, m_Text, m_Small, m_Center;

        void Start()
        {
            CoopSession.Net.HostFound += (name, ep) => s_Found[ep.Address.ToString()] = (name, Time.unscaledTime);
        }

        public static string PlayerName => string.IsNullOrEmpty(IosSettings.CoopName) ? DefaultName() : IosSettings.CoopName;

        static string DefaultName()
        {
            string n = SystemInfo.deviceName;
            return string.IsNullOrEmpty(n) || n == "iPhone" || n == "<unknown>" ? "Player" : n;
        }

        public static void Toggle()
        {
            Open = !Open;
            if (Open) SettingsPanel.Open = false;
            s_Status = "";
        }

        void Update()
        {
            if (!Open) return;
            bool inMenu = SceneManager.GetActiveScene().name == "MainMenu";
            // look for games while the panel is open in the main menu and we are not connected
            if (inMenu && !CoopSession.Active && Time.unscaledTime >= s_NextSearch)
            {
                s_NextSearch = Time.unscaledTime + 2f;
                try { CoopSession.Net.Discover(); CoopSession.Net.DiscoverSweep(); }
                catch (Exception e) { Log.Warn("coop: search failed: " + e.Message); }
            }
            UpdateTyping();
        }

        // ---- typing --------------------------------------------------------------------------------------
        static void BeginTyping(string what, string current)
        {
            s_Editing = what;
            s_Typed = current ?? "";
            if (TouchScreenKeyboard.isSupported)
                s_Keyboard = TouchScreenKeyboard.Open(s_Typed, what == "address" ? TouchScreenKeyboardType.NumbersAndPunctuation : TouchScreenKeyboardType.Default,
                                                      false, false, false, false, what == "address" ? "192.168.1.20" : "Your name");
        }

        static void UpdateTyping()
        {
            if (s_Editing == null) return;
            if (s_Keyboard != null)
            {
                s_Typed = s_Keyboard.text;
                if (s_Keyboard.status == TouchScreenKeyboard.Status.Done) FinishTyping(true);
                else if (s_Keyboard.status == TouchScreenKeyboard.Status.Canceled || s_Keyboard.status == TouchScreenKeyboard.Status.LostFocus) FinishTyping(false);
                return;
            }
            foreach (char ch in Input.inputString)
            {
                if (ch == '\b') { if (s_Typed.Length > 0) s_Typed = s_Typed.Substring(0, s_Typed.Length - 1); }
                else if (ch == '\n' || ch == '\r') { FinishTyping(true); return; }
                else if (ch == 27) { FinishTyping(false); return; }
                else if (s_Typed.Length < 40) s_Typed += ch;
            }
        }

        static void FinishTyping(bool ok)
        {
            string what = s_Editing;
            s_Editing = null;
            s_Keyboard = null;
            if (!ok) return;
            string text = (s_Typed ?? "").Trim();
            if (what == "name")
            {
                IosSettings.CoopName = text.Length > 24 ? text.Substring(0, 24) : text;
                IosSettings.Save();
                CoopSession.Net.LocalName = PlayerName;
            }
            else if (what == "address" && text.Length > 0)
            {
                IosSettings.CoopLastAddress = text;
                IosSettings.Save();
                JoinAddress(text);
            }
        }

        // ---- actions ---------------------------------------------------------------------------------------
        static void JoinAddress(string address)
        {
            if (!IPAddress.TryParse(address, out _)) { s_Status = "That is not an address like 192.168.1.20 or 26.12.34.56"; return; }
            CoopSession.Net.LocalName = PlayerName;
            CoopSession.Join(address);
            s_Status = "Joining " + address + " ...";
        }

        static void Build()
        {
            s_Buttons.Clear();
            s_Lines.Clear();
            var net = CoopSession.Net;
            bool inMenu = SceneManager.GetActiveScene().name == "MainMenu";
            bool inCafe = SceneManager.GetActiveScene().name == "MainScene";

            s_Buttons.Add(new Btn { Label = "Name: " + (s_Editing == "name" ? s_Typed + "_" : PlayerName), OnTap = () => BeginTyping("name", IosSettings.CoopName) });

            if (CoopSession.IsHost)
            {
                var addresses = CoopNet.LocalAddresses();
                if (addresses.Count == 0) s_Lines.Add("You are hosting. Your address: unknown (are you on Wi-Fi?)");
                else s_Lines.Add("You are hosting. Your address: " + string.Join("   ", addresses.ConvertAll(a => a.ToString())));
                if (addresses.Exists(a => a.Label == "Radmin VPN"))
                    s_Lines.Add("Radmin VPN friends: join your Radmin network, then Co-op > Join by your 26.x address.");
                s_Lines.Add("Players: " + PlayerList());
                s_Lines.Add("Friends: main menu > Co-op > tap your game, or Join by address.");
                s_Buttons.Add(new Btn { Label = "Stop hosting", OnTap = () => CoopSession.Leave() });
            }
            else if (CoopSession.IsClient)
            {
                s_Lines.Add(net.LocalId > 0 ? "Playing in " + net.NameOf(0) + "'s cafe. Players: " + PlayerList() : "Connecting ...");
                if (inMenu && net.LocalId > 0) s_Lines.Add("Loading the host's cafe ...");
                s_Buttons.Add(new Btn { Label = "Leave", OnTap = () => CoopSession.Leave() });
            }
            else if (inCafe)
            {
                s_Lines.Add("Let friends on the same Wi-Fi or Radmin VPN network join this cafe.");
                s_Buttons.Add(new Btn { Label = "Host this cafe", Primary = true, OnTap = () => { net.LocalName = PlayerName; CoopSession.Host(); } });
            }
            else if (inMenu)
            {
                s_Buttons.Add(new Btn { Label = "Host my cafe", Primary = true, OnTap = () => { net.LocalName = PlayerName; CoopSession.HostFromMenu(); Open = false; } });
                s_Buttons.Add(new Btn
                {
                    Label = "Join by address" + (s_Editing == "address" ? ": " + s_Typed + "_" : ""),
                    OnTap = () => BeginTyping("address", IosSettings.CoopLastAddress),
                });
                var now = Time.unscaledTime;
                int n = 0;
                foreach (var kv in s_Found)
                {
                    if (now - kv.Value.seen > 6f) continue;
                    string addr = kv.Key;
                    s_Buttons.Add(new Btn { Label = "Join " + kv.Value.name + "  (" + addr + ")", Primary = true, OnTap = () => JoinAddress(addr) });
                    n++;
                }
                s_Lines.Add(n == 0 ? "Looking for games on your Wi-Fi / Radmin VPN ..." : "Games found:");
            }
            if (!string.IsNullOrEmpty(s_Status)) s_Lines.Add(s_Status);
        }

        static string PlayerList()
        {
            var names = new List<string>();
            foreach (int id in CoopSession.Net.Players) names.Add(CoopSession.Net.NameOf(id));
            return string.Join(", ", names);
        }

        // ---- layout / input ----------------------------------------------------------------------------------
        /// The safe area in GUI coordinates (TouchHud only computes it when touch input runs).
        static Rect Safe
        {
            get
            {
                if (TouchHud.Safe.width > 0) return TouchHud.Safe;
                Rect s = Screen.safeArea;
                return new Rect(s.x, Screen.height - s.yMax, s.width, s.height);
            }
        }

        static void Layout()
        {
            Build();
            float h = Screen.height;
            Rect safe = Safe;
            float x = safe.xMin + safe.width * 0.04f, cw = safe.width * 0.92f, rowH = h * 0.085f, y = h * 0.05f;
            s_Close = new Rect(x + cw - cw * 0.18f, y, cw * 0.18f, rowH);
            y += rowH * 1.3f;
            // buttons in the left half, text in the right half
            float bw = cw * 0.5f;
            foreach (var b in s_Buttons) { b.Rect = new Rect(x, y, bw, rowH * 0.9f); y += rowH; }
        }

        /// Touch handling while the panel is open (screen coordinates, origin bottom-left).
        public static void HandleTouch(Vector2 pos, TouchPhase phase)
        {
            if (phase != TouchPhase.Began) return;
            Tap(new Vector2(pos.x, Screen.height - pos.y));
        }

        static void Tap(Vector2 gui)
        {
            Layout();
            if (s_Close.Contains(gui)) { Open = false; FinishTyping(false); return; }
            foreach (var b in s_Buttons)
                if (b.Rect.Contains(gui))
                {
                    try { b.OnTap(); }
                    catch (Exception e) { Log.Error("coop panel: " + e); s_Status = "Something went wrong (see the log)"; }
                    return;
                }
        }

        void OnGUI()
        {
            if (!Open) return;
            var ev = Event.current;
            if (ev.type == EventType.MouseDown && !VInput.Virtual) { Tap(ev.mousePosition); ev.Use(); return; }
            if (ev.type != EventType.Repaint) return;
            GUI.depth = -200;
            Layout();
            float h = Screen.height, w = Screen.width;
            int fs = Mathf.RoundToInt(h * 0.03f);
            if (m_Title == null)
            {
                m_Title = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
                m_Text = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, wordWrap = true };
                m_Small = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, wordWrap = true };
                m_Center = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = true };
            }
            m_Title.fontSize = Mathf.RoundToInt(fs * 1.4f);
            m_Text.fontSize = m_Center.fontSize = fs;
            m_Small.fontSize = Mathf.RoundToInt(fs * 0.8f);

            Fill(new Rect(0, 0, w, h), new Color(0.02f, 0.02f, 0.04f, 0.92f));
            var safe = Safe;
            float x = safe.xMin + safe.width * 0.04f, cw = safe.width * 0.92f;
            GUI.Label(new Rect(x, s_Close.y, s_Close.x - x, s_Close.height), "Co-op (Wi-Fi / Radmin VPN)", m_Title);
            Fill(s_Close, new Color(0.3f, 0.6f, 1f, 0.9f));
            GUI.Label(s_Close, "Close", m_Center);
            foreach (var b in s_Buttons)
            {
                Fill(b.Rect, b.Primary ? new Color(0.3f, 0.6f, 1f, 0.9f) : new Color(1, 1, 1, 0.15f));
                GUI.Label(b.Rect, b.Label, m_Center);
            }
            float tx = x + cw * 0.54f, ty = s_Close.yMax + h * 0.03f;
            foreach (var line in s_Lines)
            {
                float lh = m_Text.CalcHeight(new GUIContent(line), cw * 0.46f);
                GUI.Label(new Rect(tx, ty, cw * 0.46f, lh), line, m_Text);
                ty += lh + h * 0.01f;
            }
            GUI.Label(new Rect(tx, Mathf.Max(ty + h * 0.02f, h * 0.7f), cw * 0.46f, h * 0.28f),
                "Play on the same Wi-Fi, or (PC) the same Radmin VPN network. iPhone: allow \"Local Network\" when iOS asks. " +
                "The whole cafe is shared; each player has their own character and camera. The host's cafe is saved on the host.", m_Small);
        }

        static void Fill(Rect r, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;
        }
    }
}
