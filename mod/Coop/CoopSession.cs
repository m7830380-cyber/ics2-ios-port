using System;
using System.Text;
using ICS2Coop;
using ICS2Shim;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ICS2iOS
{
    // The game side of LAN co-op: owns the network (ICS2Coop.CoopNet), polls it every frame and routes
    // messages to the sync modules. Started from the co-op menu, or from the command line on Windows test
    // installs:
    //   -ics2coop host | -ics2coop join <address>   start a session when the cafe has loaded
    //   -ics2name <name>                            player name
    //   -ics2autostart                              main menu: load the save (or start a new game) by itself
    //   -ics2test                                   answer the test bot's state queries and commands
    public class CoopSession : MonoBehaviour
    {
        public static CoopSession Instance { get; private set; }
        public static CoopNet Net { get; private set; }
        public static bool Active => Net != null && Net.Running;
        public static bool IsHost => Active && Net.IsHost;
        public static bool IsClient => Active && !Net.IsHost;
        public static bool TestMode { get; private set; }

        public static event Action<int, Msg, NetDataReader> Received;

        string m_PendingRole, m_PendingAddress;
        bool m_AutoStart;
        float m_AutoStartAt = -1f;

        void Awake()
        {
            Instance = this;
            Net = new CoopNet { Log = s => Log.Info("coop: " + s) };
            Net.LocalName = Platform.CommandLineValue("-ics2name") ?? CoopPanel.PlayerName;
            Net.Message += OnMessage;
            Net.PlayerJoined += (id, name) => DebugOverlay.Toast(name + " joined");
            Net.PlayerLeft += (id, name) => DebugOverlay.Toast(name + " left");
            Net.Disconnected += reason =>
            {
                DebugOverlay.Toast(reason == "ConnectionRejected"
                    ? "The host refused: different co-op build (both players need the same one), or the session is full"
                    : "Disconnected from host");
                EndGuestSession();
            };
            WorldSnapshot.Init();

            TestMode = Platform.CommandLineFlag("-ics2test");
            if (TestMode)
            {
                // test copies run next to the owner's work: silent, and they keep simulating when unfocused
                AudioListener.volume = 0f;
                Application.runInBackground = true;
            }
            m_AutoStart = Platform.CommandLineFlag("-ics2autostart");
            m_PendingRole = Platform.CommandLineValue("-ics2coop");
            if (m_PendingRole == "join") m_PendingAddress = CommandLineAfter("-ics2coop", 2) ?? "127.0.0.1";
            if (m_PendingRole != null) Log.Info($"coop: command line asks to {m_PendingRole} {m_PendingAddress}");
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        void OnDestroy()
        {
            Net?.Stop();
        }

        void OnApplicationQuit()
        {
            Net?.Stop();
        }

        void OnSceneLoaded(Scene s, LoadSceneMode mode)
        {
            if (s.name == "MainMenu")
            {
                // leaving the cafe ends the session; a guest goes back to its own save
                bool wasInCafe = s_InCafe;
                s_InCafe = false;   // already in the menu: ending the session must not load it again
                if (wasInCafe && Active) Leave();
                if (s_RestoreOnMenu) { s_RestoreOnMenu = false; WorldSnapshot.Restore(); }
                WorldSnapshot.ReloadAfterSession();
                if (m_AutoStart) m_AutoStartAt = Time.unscaledTime + 3f;
                // a guest joins from the main menu: the host's world arrives, then the cafe loads
                if (m_PendingRole == "join") { Join(m_PendingAddress); m_PendingRole = null; }
            }
            if (s.name == "MainScene")
            {
                s_InCafe = true;
                s_CafeLoadedAt = Time.unscaledTime;
                NetRef.ClearCache();
                if (m_PendingRole == "host") { Host(); m_PendingRole = null; }
            }
        }

        static bool s_InCafe;
        static bool s_RestoreOnMenu;    // a guest's session ended in the cafe: back to the own save once in the menu
        static float s_CafeLoadedAt;

        /// The shared world is up on this device: the cafe has loaded (and, on a guest, matched the host's
        /// items). Calls the game makes while loading (elevators and lights setting themselves up) are not
        /// mirrored.
        public static bool WorldReady =>
            Active && s_InCafe && GameManager.Instance != null && Time.unscaledTime > s_CafeLoadedAt + 3f && (IsHost || ItemSync.Ready);

        public static void Host() { if (Net.StartHost()) DebugOverlay.Toast("Co-op: hosting"); }

        /// Main menu "Host my cafe": open the own cafe (the save, or a new game) and host once it has loaded.
        public static void HostFromMenu()
        {
            if (Instance == null) return;
            Instance.m_PendingRole = "host";
            AutoStart();
        }
        public static void Join(string address) { Net.Join(address); }

        public static void Leave()
        {
            Net.Stop();
            Log.Info("coop: session ended");
            EndGuestSession();
        }

        /// A guest's session is over (left or lost the host): back to the own save, and out of the host's
        /// world to the main menu.
        static void EndGuestSession()
        {
            ItemSync.EndSession();
            if (!WorldSnapshot.InSessionSave) return;
            // still in the host's world: the session save stays the open file until the main menu is up, so
            // whatever the closing cafe saves cannot land in this device's own save
            if (s_InCafe && Instance != null) { s_RestoreOnMenu = true; Instance.StartCoroutine(ToMainMenu()); }
            else WorldSnapshot.Restore();
        }

        static System.Collections.IEnumerator ToMainMenu()
        {
            yield return null; // not from inside the network callback
            Time.timeScale = 1f;
            SceneManager.LoadScene("MainMenu");
        }

        void Update()
        {
            if (TestMode) AudioListener.volume = 0f; // the game's own volume setting would turn it back on
            Platform.CoopActive = Active;
            if (Active && Time.timeScale < 0.01f && s_InCafe) Time.timeScale = 1f; // paused before the session began
            try
            {
                Net.Poll();
            }
            catch (Exception e)
            {
                Log.Error("coop: network poll failed: " + e);
            }
            if (TestMode && Time.unscaledTime >= m_NextFileReport) FileChannel();
            if (m_AutoStartAt > 0 && Time.unscaledTime >= m_AutoStartAt)
            {
                m_AutoStartAt = -1f;
                AutoStart();
            }
        }

        float m_NextFileReport;

        /// Test copies with two real games: the runner cannot reach every game over the network, so each game
        /// writes its report to <profile>/report.txt (details from report-detail.txt) once a second and runs
        /// the lines of commands.txt ("command arg"), which it then deletes.
        void FileChannel()
        {
            m_NextFileReport = Time.unscaledTime + 1f;
            string dir = Platform.PersistentDataPath;
            try
            {
                string cmds = System.IO.Path.Combine(dir, "commands.txt");
                if (System.IO.File.Exists(cmds))
                {
                    var lines = System.IO.File.ReadAllLines(cmds);
                    System.IO.File.Delete(cmds);
                    foreach (var line in lines)
                    {
                        if (line.Trim().Length == 0) continue;
                        int sp = line.IndexOf(' ');
                        CoopTestAgent.Run(-1, sp < 0 ? line.Trim() : line.Substring(0, sp), sp < 0 ? "" : line.Substring(sp + 1).Trim());
                    }
                }
                string detailFile = System.IO.Path.Combine(dir, "report-detail.txt");
                string detail = System.IO.File.Exists(detailFile) ? System.IO.File.ReadAllText(detailFile).Trim() : "";
                var sb = new StringBuilder();
                sb.Append("time=").Append(Time.unscaledTime.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                CoopTestAgent.Report(sb, detail);
                string tmp = System.IO.Path.Combine(dir, "report.tmp");
                System.IO.File.WriteAllText(tmp, sb.ToString());
                string rep = System.IO.Path.Combine(dir, "report.txt");
                if (System.IO.File.Exists(rep)) System.IO.File.Delete(rep);
                System.IO.File.Move(tmp, rep);
            }
            catch (Exception e)
            {
                Log.Warn("test: file channel: " + e.Message);
            }
        }

        static void AutoStart()
        {
            var menu = FindObjectOfType<MainMenuManager>();
            if (menu == null) { Log.Warn("autostart: no MainMenuManager"); return; }
            if (FBPP.GetString("gamesaved") == "saved")
            {
                Log.Info("autostart: loading the save");
                menu.LoadGame();
            }
            else
            {
                Log.Info("autostart: starting a new game");
                menu.SureStartNewGame();
                menu.StartGame();
            }
        }

        void OnMessage(int from, Msg id, NetDataReader r)
        {
            try
            {
                switch (id)
                {
                    case Msg.TestQuery:
                        if (TestMode) SendTestReport(from, r.GetString());
                        break;
                    case Msg.TestCommand:
                        if (TestMode) CoopTestAgent.Run(from, r.GetString(), r.GetString());
                        break;
                    default:
                        Received?.Invoke(from, id, r);
                        break;
                }
            }
            catch (Exception e)
            {
                Log.Error($"coop: handling {id} from player {from} failed: {e}");
            }
        }

        /// Test builds: a key=value report of the state the bot asserts on.
        static void SendTestReport(int to, string tag)
        {
            var sb = new StringBuilder();
            int bar = tag.IndexOf('|');
            CoopTestAgent.Report(sb, bar >= 0 ? tag.Substring(bar + 1) : "");
            var w = Net.Begin(Msg.TestReport);
            w.Put(tag);
            w.Put(sb.ToString());
            Net.SendTo(to);
        }

        static string CommandLineAfter(string name, int offset)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + offset < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + offset];
            return null;
        }
    }
}
