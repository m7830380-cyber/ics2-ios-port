using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace ICS2iOS
{
    // Settings of the iOS layer (touch controls, graphics), kept as key=value lines in
    // Documents/ICS2iOS-settings.txt next to the game's own settings and saves.
    public static class IosSettings
    {
        public static float LookSensitivity = 1f;   // multiplier on top of the game's own mouse sensitivity
        public static float HudOpacity = 0.45f;
        public static float HudScale = 1f;
        public static bool HudHidden;
        public static bool InvertLook;
        public static int FrameRate = 60;           // 30 or 60
        public static float RenderScale = 1f;       // 0.5 .. 1 of the native resolution
        public static int Quality = 1;              // 0 low, 1 medium, 2 high (shadows, post-processing)
        // Unity's own sky instead of the Enviro sky cameras (SkyGuard); the default on iPhone, where the Enviro
        // sky left old frames in the sky
        public static bool SimpleSky = Application.platform == RuntimePlatform.IPhonePlayer;
        // 0 off, 1 FXAA, 2 TAA (the game's own). TAA blends in earlier frames; on iPhone it never lets go of
        // them in the open sky (grey smear outdoors), so FXAA is the default
        public static int AntiAliasing = 1;
        public static bool RealWeather;             // off by default: live weather would send the IP address to a weather service
        public static string CoopName = "";         // name shown to the other players (empty: device name)
        public static string CoopLastAddress = "";  // last address typed into "Join by address"

        static string Path => System.IO.Path.Combine(ICS2Shim.Platform.PersistentDataPath, "ICS2iOS-settings.txt");

        public static event Action Changed;

        public static void Load()
        {
            try
            {
                if (!File.Exists(Path)) return;
                var d = new Dictionary<string, string>();
                foreach (var line in File.ReadAllLines(Path))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
                LookSensitivity = F(d, "look", LookSensitivity, 0.1f, 5f);
                HudOpacity = F(d, "hud.opacity", HudOpacity, 0.1f, 1f);
                HudScale = F(d, "hud.scale", HudScale, 0.6f, 1.6f);
                HudHidden = B(d, "hud.hidden", HudHidden);
                InvertLook = B(d, "look.invert", InvertLook);
                FrameRate = (int)F(d, "fps", FrameRate, 30, 60) >= 45 ? 60 : 30;
                RenderScale = F(d, "render.scale", RenderScale, 0.5f, 1f);
                Quality = (int)F(d, "quality", Quality, 0, 2);
                AntiAliasing = (int)F(d, "aa", AntiAliasing, 0, 2);
                RealWeather = B(d, "weather.real", RealWeather);
                SimpleSky = B(d, "sky.simple", SimpleSky);
                if (d.TryGetValue("coop.name", out var cn)) CoopName = cn;
                if (d.TryGetValue("coop.address", out var ca)) CoopLastAddress = ca;
            }
            catch (Exception e)
            {
                Log.Warn("settings: cannot read: " + e.Message);
            }
        }

        public static void Save()
        {
            try
            {
                var ci = CultureInfo.InvariantCulture;
                File.WriteAllLines(Path, new[]
                {
                    "look=" + LookSensitivity.ToString(ci),
                    "look.invert=" + InvertLook,
                    "hud.opacity=" + HudOpacity.ToString(ci),
                    "hud.scale=" + HudScale.ToString(ci),
                    "hud.hidden=" + HudHidden,
                    "fps=" + FrameRate,
                    "render.scale=" + RenderScale.ToString(ci),
                    "quality=" + Quality,
                    "aa=" + AntiAliasing,
                    "weather.real=" + RealWeather,
                    "sky.simple=" + SimpleSky,
                    "coop.name=" + CoopName.Replace("\n", " "),
                    "coop.address=" + CoopLastAddress,
                });
            }
            catch (Exception e)
            {
                Log.Warn("settings: cannot write: " + e.Message);
            }
            try { Changed?.Invoke(); } catch (Exception e) { Log.Error("settings changed handler: " + e); }
        }

        static float F(Dictionary<string, string> d, string k, float def, float min, float max) =>
            d.TryGetValue(k, out var s) && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                ? Mathf.Clamp(v, min, max) : def;

        static bool B(Dictionary<string, string> d, string k, bool def) =>
            d.TryGetValue(k, out var s) && bool.TryParse(s, out bool v) ? v : def;
    }
}
