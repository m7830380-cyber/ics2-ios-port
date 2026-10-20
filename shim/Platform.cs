using System;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ICS2Shim
{
    /// <summary>Stand-ins for desktop-only calls the patcher redirects here.</summary>
    public static class Platform
    {
        // ---- System.Diagnostics.Process: there are no other processes on iOS ------------------
        public static Process ProcessStart(ProcessStartInfo info)
        {
            Debug.LogWarning("[ICS2-iOS] Process.Start blocked: " + (info?.FileName ?? "<null>") + " " + (info?.Arguments ?? ""));
            return null;
        }
        public static Process ProcessStart(string file)
        {
            // URLs are the one useful case (help links etc.)
            if (file != null && (file.StartsWith("http://") || file.StartsWith("https://")))
            {
                Application.OpenURL(file);
                return null;
            }
            Debug.LogWarning("[ICS2-iOS] Process.Start blocked: " + file);
            return null;
        }
        public static Process ProcessStart(string file, string args)
        {
            Debug.LogWarning("[ICS2-iOS] Process.Start blocked: " + file + " " + args);
            return null;
        }
        public static bool ProcessStartInstance(Process p)
        {
            Debug.LogWarning("[ICS2-iOS] Process.Start blocked: " + (p?.StartInfo?.FileName ?? "<null>"));
            return false;
        }
        public static Process[] GetProcessesByName(string name) => new Process[0];

        /// Replaces Application.dataPath in the game (screenshots folder): the app bundle is read-only on iOS.
        public static string WritableDataPath => PersistentDataPath;

        /// Replaces Application.persistentDataPath in the game. `-ics2profile <dir>` on the command line (Windows
        /// test installs) moves saves, settings and the log into that folder, so test copies never touch the
        /// owner's real saves.
        public static string PersistentDataPath
        {
            get
            {
                if (!s_ProfileChecked)
                {
                    s_ProfileChecked = true;
                    s_Profile = CommandLineValue("-ics2profile");
                    if (s_Profile != null) System.IO.Directory.CreateDirectory(s_Profile);
                }
                return s_Profile ?? Application.persistentDataPath;
            }
        }
        static string s_Profile;
        static bool s_ProfileChecked;

        /// The game's save file name (it hardcodes "save-data.save" when it starts FBPP). A co-op guest plays
        /// in a separate session file so the host's world never overwrites the guest's own save.
        /// True while this device is a guest in someone else's cafe: the patched game skips the systems that
        /// only the host runs (customers arriving, crack orders, cabin income, day rollover).
        public static bool IsCoopGuest { get; set; }

        /// Guest: actions on the host's world (approve a customer's PC, take a customer's cash ...) are sent to
        /// the host instead of run locally. Returns true when the action was forwarded (the game method then
        /// returns at once). Set by the co-op mod.
        public static Func<object, string, int, bool> ForwardHandler;
        public static bool Forward(object self, string action, int arg) => ForwardHandler != null && ForwardHandler(self, action, arg);

        /// True while a co-op session runs (set by the co-op mod).
        public static bool CoopActive { get; set; }

        /// Co-op: a patched game method was called (doors, elevators, room purchases, NPC hits ...). The mod
        /// mirrors it on the other devices, or sends it to the host instead of running it here. Returns true
        /// when the method must not run locally. The patched game checks CoopActive first (no allocation
        /// outside a session).
        public static Func<object, string, object[], bool> CallHandler;
        public static bool Call(object self, string action, object[] args) => CallHandler != null && CallHandler(self, action, args);

        /// Co-op: who a fighting customer goes for. The patched Civil.Update reads the player through this, so the
        /// mod can hand it the nearest player's body instead of always the host's own.
        public static Func<UnityEngine.Transform, object, UnityEngine.Transform> FightTargetHandler;
        public static UnityEngine.Transform FightTarget(UnityEngine.Transform player, object civil) =>
            CoopActive && FightTargetHandler != null ? FightTargetHandler(player, civil) : player;

        /// Co-op: the health Civil.Update checks to keep fighting: the current victim's, not always the host's.
        public static Func<float, object, float> FightHealthHandler;
        public static float FightHealth(float health, object civil) =>
            CoopActive && FightHealthHandler != null ? FightHealthHandler(health, civil) : health;

        /// How many hooked methods are running on the stack (the patched methods call CallEnter / CallExit
        /// around their body). A hooked method called from inside another one that is already mirrored on the
        /// other devices (CoveredDepth) is not sent again: the outer call's replay does it there.
        public static int CallDepth;
        public static int CoveredDepth = -1;
        public static bool Covered => CoveredDepth >= 0 && CallDepth > CoveredDepth;
        public static void CallEnter() => CallDepth++;
        public static void CallExit()
        {
            if (CallDepth > 0) CallDepth--;
            if (CallDepth <= CoveredDepth) CoveredDepth = -1;
        }

        /// Co-op: FBPP (the game's key/value store) was written: value is string / int / float / bool, or null
        /// when the key was deleted. Called from the patched FileBasedPrefs.
        public static Action<string, object> PrefSetHandler;
        public static void OnPrefSet(string key, object value)
        {
            if (CoopActive && PrefSetHandler != null) PrefSetHandler(key, value);
        }

        /// Replaces Time.timeScale writes in GameManager (pause menu, idle pause): in co-op the world keeps
        /// running for the other players, so pausing only opens the menu.
        public static void SetTimeScale(float value)
        {
            if (CoopActive && value < 0.01f) return;
            Time.timeScale = value;
        }

        /// Host: CafeSoftManager.GenerateNotification was called (a customer asks to use a PC).
        public static Action<object, string, float, int, string> NotificationGenerated;
        public static void OnGenerateNotification(object table, string pcName, float timer, int extraTime, string message)
            => NotificationGenerated?.Invoke(table, pcName, timer, extraTime, message);

        public const string DefaultSaveFileName = "save-data.save";
        public static string SaveFileName { get; set; } = DefaultSaveFileName;

        /// Value after `name` on the command line, or null.
        public static string CommandLineValue(string name)
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                    if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            catch (Exception) { }
            return null;
        }

        public static bool CommandLineFlag(string name)
        {
            try
            {
                foreach (var a in Environment.GetCommandLineArgs())
                    if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception) { }
            return false;
        }

        // ---- frame pacing ----------------------------------------------------------------------
        // iOS ignores QualitySettings.vSyncCount and runs at 30 fps unless targetFrameRate is set,
        // so the game's "VSync" option is mapped to "match the display" (60, or 120 on ProMotion).
        static int vSyncCount = 1;
        static int requestedFps = -1;

        /// Highest refresh rate the display supports (120 on ProMotion iPhones).
        public static int DisplayMaxFps
        {
            get
            {
                int r = Screen.currentResolution.refreshRate;
                return r >= 30 ? r : 60;
            }
        }

        /// Optional cap set by the iOS mod (0 = no cap) e.g. to save battery.
        public static int UserFpsCap;

        public static int EffectiveTargetFps
        {
            get
            {
                int max = DisplayMaxFps;
                if (UserFpsCap > 0) max = Math.Min(max, UserFpsCap);
                if (vSyncCount > 0 || requestedFps <= 0) return max;
                return Math.Min(requestedFps, max);
            }
        }

        public static void SetVSyncCount(int v)
        {
            vSyncCount = v;
            QualitySettings.vSyncCount = v;
            ApplyFrameRate();
        }

        public static void SetTargetFrameRate(int fps)
        {
            requestedFps = fps;
            ApplyFrameRate();
        }

        public static void ApplyFrameRate()
        {
            Application.targetFrameRate = EffectiveTargetFps;
        }

        // ---- display mode: the iOS mod owns the render resolution (Settings > Render scale) ----
        // The game's resolution / fullscreen options are desktop window settings; on iOS they would shrink
        // or stretch the screen behind the mod's back, so they are ignored.
        // On Windows (the co-op PC build) they are the game's own options and work as usual.
        static bool Desktop => Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor;
        public static void SetResolution(int width, int height, bool fullscreen)
        { if (Desktop) Screen.SetResolution(width, height, fullscreen); else Ignored($"SetResolution({width}x{height})"); }
        public static void SetResolution(int width, int height, bool fullscreen, int refreshRate)
        { if (Desktop) Screen.SetResolution(width, height, fullscreen, refreshRate); else Ignored($"SetResolution({width}x{height}@{refreshRate})"); }
        public static void SetResolution(int width, int height, FullScreenMode mode)
        { if (Desktop) Screen.SetResolution(width, height, mode); else Ignored($"SetResolution({width}x{height}, {mode})"); }
        public static void SetFullScreen(bool value) { if (Desktop) Screen.fullScreen = value; }
        /// SettingMenu.SetResolution: the game's resolution option (Windows only, and only a valid list index).
        public static bool ResolutionAllowed(int index, Resolution[] list) => Desktop && list != null && index >= 0 && index < list.Length;
        public static void SetFullScreenMode(FullScreenMode mode) { if (Desktop) Screen.fullScreenMode = mode; }

        static void Ignored(string what) => Debug.Log("[ICS2-iOS] game requested Screen." + what + " - ignored on iOS");

        // ---- Enviro sky ----------------------------------------------------------------------------
        /// Replaces the sky command buffer's Blit(CameraTarget, CameraTarget, sky): reading the target being
        /// written is undefined on Metal and leaves old frames in the sky. The sky shader ignores its source.
        public static void SkyBlit(UnityEngine.Rendering.CommandBuffer cb, UnityEngine.Rendering.RenderTargetIdentifier source,
                                   UnityEngine.Rendering.RenderTargetIdentifier dest, Material mat)
        {
            cb.Blit(Texture2D.blackTexture, dest, mat);
            Debug.Log("[ICS2-iOS] sky: Enviro sky blit uses a black source (no feedback)");
        }
    }
}
