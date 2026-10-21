using System;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ICS2iOS
{
    // The Enviro sky on iPhone. Enviro renders the sky and the clouds with extra cameras into textures and
    // blits them over the player camera's target before the G-buffer pass (deferred). On Metal that early
    // write does not survive into the lighting pass: the sky showed old frames ("out of the map" smear).
    // This:
    //   * removes Enviro's early blit and draws the same Enviro picture after the lighting instead, only where
    //     the camera saw nothing (Hidden/ICS2/SkyBehind, from the shell's ics2shaders bundle): the game's own
    //     sky, clouds and time of day;
    //   * clears each Enviro camera's texture before it draws (never old frames where the sky does not draw);
    //   * "Sky: Plain" in the iOS settings: no Enviro sky cameras; Unity's procedural sky after the lighting;
    //   * logs every camera and the sky shaders once per scene.
    public class SkyGuard : MonoBehaviour
    {
        const string ClearName = "ICS2 sky clear";
        const string SkyName = "ICS2 sky";
        static bool s_Logged;
        static bool s_BundleTried;
        static Material s_Behind;
        static bool s_SimpleApplied;
        float m_Next;

        public static bool Enabled => Bootstrap.OnDevice || Array.IndexOf(Environment.GetCommandLineArgs(), "-skyguard") >= 0;

        void Start()
        {
            SceneManager.sceneLoaded += (s, m) => { s_Logged = false; s_SimpleApplied = false; };
            IosSettings.Changed += () => m_Next = 0f;
        }

        void Update()
        {
            if (!Enabled || Time.unscaledTime < m_Next) return;
            m_Next = Time.unscaledTime + 2f;
            try { IosGraphics.EnforceAntiAliasing(); Guard(); }
            catch (Exception e) { Log.Error("sky guard: " + e); m_Next = Time.unscaledTime + 30f; }
        }

        static void Guard()
        {
            var sky = EnviroSky.instance;
            if (sky == null || !sky.started || sky.PlayerCamera == null) return;
            var main = sky.PlayerCamera;

            if (!s_Logged) { s_Logged = true; LogCameras(main); }

            if (IosSettings.SimpleSky) { RemoveSky(main); Simple(sky, main); return; }
            if (s_SimpleApplied) Full(sky, main);

            if (main.clearFlags == CameraClearFlags.Depth || main.clearFlags == CameraClearFlags.Nothing)
            {
                Log.Info($"sky guard: player camera cleared only {main.clearFlags}; now clears to black");
                main.clearFlags = CameraClearFlags.SolidColor;
                main.backgroundColor = Color.black;
            }
            DrawSkyAfterLighting(main);
            foreach (var cam in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (cam == null || cam == main || !cam.name.StartsWith("Enviro", StringComparison.Ordinal)) continue;
                if (cam.targetTexture == null || HasClear(cam)) continue;
                var cb = new CommandBuffer { name = ClearName };
                cb.ClearRenderTarget(true, true, new Color(0f, 0f, 0f, 0f));
                cam.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, cb);
                Log.Info($"sky guard: '{cam.name}' ({cam.clearFlags}) clears its texture before drawing");
            }
        }

        /// Hidden/ICS2/SkyBehind from StreamingAssets/ics2shaders (built by the shell), or null.
        static Material Behind()
        {
            if (s_BundleTried) return s_Behind;
            s_BundleTried = true;
            try
            {
                string path = System.IO.Path.Combine(Application.streamingAssetsPath, "ics2shaders");
                var bundle = AssetBundle.LoadFromFile(path);
                if (bundle == null) { Log.Warn("sky guard: no shader bundle at " + path); return null; }
                Shader shader = null;
                foreach (var s in bundle.LoadAllAssets<Shader>()) if (s.name == "Hidden/ICS2/SkyBehind") shader = s;
                if (shader == null || !shader.isSupported) { Log.Warn($"sky guard: SkyBehind shader {(shader == null ? "missing" : "unsupported")}"); return null; }
                s_Behind = new Material(shader) { hideFlags = HideFlags.DontSave };
                Log.Info("sky guard: SkyBehind shader loaded");
            }
            catch (Exception e) { Log.Error("sky guard: loading the shader bundle: " + e); }
            return s_Behind;
        }

        /// Enviro's sky picture (its own material: sky, background and clouds textures) into a temporary
        /// texture, then onto the camera target where the depth is at the far plane, after the lighting.
        static void DrawSkyAfterLighting(Camera main)
        {
            var r = main.GetComponent<EnviroSkyRendering>();
            var behind = Behind();
            if (r == null || r.material == null || behind == null) return;   // Enviro's own blit stays (black source)

            RemoveEnviroBuffers(main);
            bool deferred = main.actualRenderingPath == RenderingPath.DeferredShading;
            var evt = deferred ? CameraEvent.AfterLighting : CameraEvent.AfterForwardOpaque;
            if (!deferred) main.depthTextureMode |= DepthTextureMode.Depth;
            foreach (var b in main.GetCommandBuffers(evt)) if (b.name == SkyName) return;
            RemoveSky(main);

            int tmp = Shader.PropertyToID("_ICS2Sky");
            var cb = new CommandBuffer { name = SkyName };
            cb.GetTemporaryRT(tmp, -1, -1, 0, FilterMode.Bilinear, main.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default);
            cb.Blit(BuiltinRenderTextureType.CameraTarget, tmp, r.material);   // the sky shader ignores its source
            cb.Blit(tmp, BuiltinRenderTextureType.CameraTarget, behind);
            cb.ReleaseTemporaryRT(tmp);
            main.AddCommandBuffer(evt, cb);
            Log.Info($"sky guard: Enviro sky drawn {evt} on '{main.name}' ({main.actualRenderingPath})");
        }

        static void RemoveSky(Camera cam)
        {
            foreach (var evt in new[] { CameraEvent.AfterLighting, CameraEvent.AfterForwardOpaque })
                foreach (var b in cam.GetCommandBuffers(evt))
                    if (b.name == SkyName) cam.RemoveCommandBuffer(evt, b);
        }

        static bool HasClear(Camera cam)
        {
            foreach (var b in cam.GetCommandBuffers(CameraEvent.BeforeForwardOpaque))
                if (b.name == ClearName) return true;
            return false;
        }

        static void Simple(EnviroSky sky, Camera main)
        {
            if (!s_SimpleApplied)
            {
                s_SimpleApplied = true;
                var r = main.GetComponent<EnviroSkyRendering>();
                if (r != null) r.enabled = false;
                RemoveEnviroBuffers(main);
                Log.Info($"sky guard: simple sky ({(RenderSettings.skybox != null ? RenderSettings.skybox.shader.name : "fog colour")}, no Enviro sky cameras)");
            }
            if (RenderSettings.skybox != null && RenderSettings.skybox.shader.isSupported) main.clearFlags = CameraClearFlags.Skybox;
            else
            {
                // no usable sky material: the fog colour, which follows the time of day
                var color = RenderSettings.fogColor;
                main.clearFlags = CameraClearFlags.SolidColor;
                main.backgroundColor = new Color(color.r, color.g, color.b, 1f);
            }
        }

        static void Full(EnviroSky sky, Camera main)
        {
            s_SimpleApplied = false;
            var r = main.GetComponent<EnviroSkyRendering>();
            if (r != null) { r.enabled = true; r.RefreshCameraCommand(); }
            main.clearFlags = CameraClearFlags.SolidColor;
            main.backgroundColor = Color.black;
            Log.Info("sky guard: full Enviro sky");
        }

        static void RemoveEnviroBuffers(Camera cam)
        {
            foreach (var evt in new[] { CameraEvent.BeforeGBuffer, CameraEvent.BeforeForwardOpaque })
                foreach (var b in cam.GetCommandBuffers(evt))
                    if (b.name == "Enviro Sky Rendering") cam.RemoveCommandBuffer(evt, b);
        }

        static string ShaderState(string name)
        {
            var s = Shader.Find(name);
            return s == null ? "MISSING" : s.isSupported ? "ok" : "UNSUPPORTED";
        }

        static void LogCameras(Camera main)
        {
            var sb = new StringBuilder("sky: ");
            var box = RenderSettings.skybox;
            sb.Append($"skybox={(box == null ? "none" : box.shader.name + (box.shader.isSupported ? "" : " UNSUPPORTED"))} ");
            foreach (var n in new[] { "Enviro/Skybox", "Enviro/SkyRendering", "Enviro/Clouds", "Enviro/Fog" })
                sb.Append(n).Append('=').Append(ShaderState(n)).Append(' ');
            sb.Append($"fog={RenderSettings.fog} fogColor={RenderSettings.fogColor}");
            Log.Info(sb.ToString());
            foreach (var cam in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (cam == null || !cam.gameObject.scene.IsValid() && cam.hideFlags == HideFlags.None) continue;
                var rt = cam.targetTexture;
                var buffers = new StringBuilder();
                foreach (CameraEvent evt in Enum.GetValues(typeof(CameraEvent)))
                    foreach (var b in cam.GetCommandBuffers(evt)) buffers.Append(evt).Append(':').Append(b.name).Append(' ');
                Log.Info($"sky: camera '{cam.name}'{(cam == main ? " (player)" : "")} enabled={cam.enabled} clear={cam.clearFlags} " +
                         $"bg={cam.backgroundColor} path={cam.actualRenderingPath} hdr={cam.allowHDR} msaa={cam.allowMSAA} depth={cam.depth} " +
                         $"far={cam.farClipPlane} target={(rt == null ? "screen" : $"{rt.width}x{rt.height} {rt.format} d{rt.depth}")} " +
                         $"buffers=[{buffers.ToString().Trim()}]");
            }
        }
    }
}
