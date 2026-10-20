using System;
using ICS2Shim;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace ICS2iOS
{
    // Applies the iOS graphics settings (frame rate, render resolution, quality) on start, on every scene
    // load and whenever they change. Effects are only reduced, never removed from the game's data:
    //   Low    - no real-time shadows, post-processing (bloom, AO, colour grading ...) off
    //   Medium - shadows up to 25 m, low shadow resolution, post-processing on
    //   High   - the game's own settings
    public static class IosGraphics
    {
        static int s_NativeW, s_NativeH;
        static float s_AppliedScale = -1f;

        public static void Init()
        {
            s_NativeW = Mathf.Max(Screen.width, Screen.height);
            s_NativeH = Mathf.Min(Screen.width, Screen.height);
            IosSettings.Changed += Apply;
            Apply();
        }

        /// The iOS graphics options. The Windows build keeps the game's own settings (test copies, -ics2test,
        /// pass -iosgraphics to use the phone defaults so two games fit in memory).
        static bool Applies => Bootstrap.OnDevice || Platform.CommandLineFlag("-iosgraphics");

        public static void Apply()
        {
            if (!Applies) return;
            try
            {
                Platform.UserFpsCap = IosSettings.FrameRate;
                Platform.ApplyFrameRate();

                if (Mathf.Abs(s_AppliedScale - IosSettings.RenderScale) > 0.01f && s_NativeW > 0)
                {
                    s_AppliedScale = IosSettings.RenderScale;
                    int w = Mathf.RoundToInt(s_NativeW * s_AppliedScale), h = Mathf.RoundToInt(s_NativeH * s_AppliedScale);
                    Screen.SetResolution(w, h, true);
                    Log.Info($"render resolution {w}x{h} ({s_AppliedScale:P0} of {s_NativeW}x{s_NativeH})");
                }

                int q = IosSettings.Quality;
                if (q == 0) QualitySettings.shadows = ShadowQuality.Disable;
                else if (q == 1)
                {
                    if (QualitySettings.shadows == ShadowQuality.Disable) QualitySettings.shadows = ShadowQuality.HardOnly;
                    QualitySettings.shadowDistance = Mathf.Min(QualitySettings.shadowDistance <= 0 ? 25f : QualitySettings.shadowDistance, 25f);
                    QualitySettings.shadowResolution = ShadowResolution.Low;
                    QualitySettings.shadowCascades = 1;
                }
                else if (QualitySettings.shadows == ShadowQuality.Disable) QualitySettings.shadows = ShadowQuality.All;

                foreach (var l in Resources.FindObjectsOfTypeAll<PostProcessLayer>())
                    if (l != null && l.gameObject.scene.IsValid()) l.enabled = q > 0;
                Log.Info($"graphics: quality={q} fps={Application.targetFrameRate} shadows={QualitySettings.shadows} " +
                         $"shadowDistance={QualitySettings.shadowDistance} lod={QualitySettings.lodBias}");
            }
            catch (Exception e)
            {
                Log.Error("graphics settings: " + e);
            }
        }
    }
}
