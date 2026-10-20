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
        public static void SetResolution(int width, int height, bool fullscreen) => Ignored($"SetResolution({width}x{height})");
        public static void SetResolution(int width, int height, bool fullscreen, int refreshRate) => Ignored($"SetResolution({width}x{height}@{refreshRate})");
        public static void SetResolution(int width, int height, FullScreenMode mode) => Ignored($"SetResolution({width}x{height}, {mode})");
        public static void SetFullScreen(bool value) { }
        public static void SetFullScreenMode(FullScreenMode mode) { }

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
