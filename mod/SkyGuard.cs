using System;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ICS2iOS
{
    // The Enviro sky on iPhone. Enviro renders the sky into render textures with extra cameras ("Skybox"
    // clear) and paints them behind the cafe. On Metal a render pass that is not cleared starts with whatever
    // the GPU's tile memory held: anywhere the sky is not drawn, old frames stay on screen ("out of the map"
    // smear). This:
    //   * logs every camera, the sky shaders and whether the GPU supports them (the next log tells us more);
    //   * clears each Enviro camera's texture before it draws (same picture where the sky draws, never old
    //     frames where it does not), and makes sure the player camera clears;
    //   * "Sky: Simple" in the iOS settings (default on iPhone): no Enviro sky cameras at all; the camera draws
    //     the scene's own sky (Unity's procedural sky, following the sun through the day) after the lighting,
    //     only where nothing else was drawn. Saves two camera renders per frame too.
    public class SkyGuard : MonoBehaviour
    {
        const string ClearName = "ICS2 sky clear";
        static bool s_Logged;
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
            try { Guard(); }
            catch (Exception e) { Log.Error("sky guard: " + e); m_Next = Time.unscaledTime + 30f; }
        }

        static void Guard()
        {
            var sky = EnviroSky.instance;
            if (sky == null || !sky.started || sky.PlayerCamera == null) return;
            var main = sky.PlayerCamera;

            if (!s_Logged) { s_Logged = true; LogCameras(main); }

            if (IosSettings.SimpleSky) { Simple(sky, main); return; }
            if (s_SimpleApplied) Full(sky, main);

            if (main.clearFlags == CameraClearFlags.Depth || main.clearFlags == CameraClearFlags.Nothing)
            {
                Log.Info($"sky guard: player camera cleared only {main.clearFlags}; now clears to black");
                main.clearFlags = CameraClearFlags.SolidColor;
                main.backgroundColor = Color.black;
            }
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
