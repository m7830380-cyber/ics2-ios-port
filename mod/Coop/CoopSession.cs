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
            Net.LocalName = Platform.CommandLineValue("-ics2name") ?? SystemInfo.deviceName ?? "Player";
            Net.Message += OnMessage;
            Net.PlayerJoined += (id, name) => DebugOverlay.Toast(name + " joined");
            Net.PlayerLeft += (id, name) => DebugOverlay.Toast(name + " left");
            Net.Disconnected += reason => { DebugOverlay.Toast("Disconnected from host"); EndGuestSession(); };
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
                if (s_InCafe && Active) Leave();
                s_InCafe = false;
                WorldSnapshot.ReloadAfterSession();
                if (m_AutoStart) m_AutoStartAt = Time.unscaledTime + 3f;
                // a guest joins from the main menu: the host's world arrives, then the cafe loads
                if (m_PendingRole == "join") { Join(m_PendingAddress); m_PendingRole = null; }
            }
            if (s.name == "MainScene")
            {
                s_InCafe = true;
                if (m_PendingRole == "host") { Host(); m_PendingRole = null; }
            }
        }

        static bool s_InCafe;

        public static void Host() { if (Net.StartHost()) DebugOverlay.Toast("Co-op: hosting"); }
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
            if (!WorldSnapshot.InSessionSave) return;
            WorldSnapshot.Restore();
            if (s_InCafe && Instance != null) Instance.StartCoroutine(ToMainMenu());
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
            try
            {
                Net.Poll();
            }
            catch (Exception e)
            {
                Log.Error("coop: network poll failed: " + e);
            }
            if (m_AutoStartAt > 0 && Time.unscaledTime >= m_AutoStartAt)
            {
                m_AutoStartAt = -1f;
                AutoStart();
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
            CoopTestAgent.Report(sb);
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
