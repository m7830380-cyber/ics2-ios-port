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
    //   * gives the game's sky material Unity's Skybox/Procedural compiled natively (the copy converted from the
    //     Windows build drew a wrong, screen-fixed sky);
    //   * clears each Enviro camera's texture before it draws (never old frames where the sky does not draw);
    //   * "Sky: Plain" in the iOS settings: no Enviro sky cameras; Unity's procedural sky after the lighting;
    //   * gives the game's height fog (BOXOPHOBIC "Height Fog Global", a fog sphere around the camera) its shader
    //     compiled natively with a tolerant sky test: the converted copy fogged the whole sky grey outdoors;
    //   * logs every camera and the sky shaders once per scene;
    //   * "FX test" button next to the FPS counter: switches the player camera's screen effects off, all at once
    //     or one at a time, to find the one that still greys the sky on iPhone.
    public class SkyGuard : MonoBehaviour
    {
        const string ClearName = "ICS2 sky clear";
        const string SkyName = "ICS2 sky";
        static bool s_Logged;
        static bool s_BundleTried;
        static Material s_Behind;
        static readonly System.Collections.Generic.Dictionary<string, Shader> s_ModShaders = new System.Collections.Generic.Dictionary<string, Shader>();
        static bool s_SimpleApplied;
        static bool s_GuardNow;
        float m_Next;

        // effects the FX test switches off ("Fog" is Unity's scene fog, "Sky" the Enviro sky drawing, "HeightFog" the
        // game's fog sphere around the camera)
        static readonly string[] s_FxNames =
        {
            "GlobalFog", "SunShafts", "MobileFrost", "ScreenDamage", "VolumetricLightRenderer", "EnviroFog",
            "EnviroLightShafts", "PostProcessLayer", "Outliner", "Fog", "Sky", "HeightFog",
        };
        const string HeightFogShader = "Hidden/BOXOPHOBIC/Atmospherics/Height Fog Global";
        static MeshRenderer s_HeightFogOff;
        static int s_Fx;   // 0 all on, 1 all off, 2.. only s_FxNames[s_Fx - 2] off
        static readonly System.Collections.Generic.List<Behaviour> s_FxDisabled = new System.Collections.Generic.List<Behaviour>();
        static bool s_FogDisabled;

        public static bool FxAvailable => Enabled && EnviroSky.instance != null && EnviroSky.instance.PlayerCamera != null;
        public static string FxLabel => s_Fx == 0 ? "FX test" : s_Fx == 1 ? "FX: all off" : "FX off: " + s_FxNames[s_Fx - 2];

        public static void NextFxTest()
        {
            s_Fx = (s_Fx + 1) % (s_FxNames.Length + 2);
            RestoreFx();
            s_GuardNow = true;
            Log.Info("fx test: " + FxLabel);
            DebugOverlay.Toast(FxLabel);
        }

        static bool FxOff(string name) => s_Fx == 1 || s_Fx >= 2 && s_FxNames[s_Fx - 2] == name;

        /// Every frame while the FX test is on: the game and Enviro switch effects back on.
        static void ApplyFx(Camera main)
        {
            if (s_Fx == 0) return;
            foreach (var b in main.GetComponents<Behaviour>())
            {
                if (b == null || !b.enabled) continue;
                string n = b.GetType().Name;
                if (Array.IndexOf(s_FxNames, n) < 0 || !FxOff(n)) continue;
                b.enabled = false;
                s_FxDisabled.Add(b);
            }
            if (FxOff("Fog") && RenderSettings.fog) { RenderSettings.fog = false; s_FogDisabled = true; }
            if (FxOff("Sky")) RemoveSky(main);
            if (FxOff("HeightFog") && HeightFog() is MeshRenderer r && r.enabled) { r.enabled = false; s_HeightFogOff = r; }
        }

        static void RestoreFx()
        {
            foreach (var b in s_FxDisabled) if (b != null) b.enabled = true;
            s_FxDisabled.Clear();
            if (s_FogDisabled) { RenderSettings.fog = true; s_FogDisabled = false; }
            if (s_HeightFogOff != null) s_HeightFogOff.enabled = true;
            s_HeightFogOff = null;
            IosGraphics.Apply();   // post-processing back to what the quality setting says
        }

        public static bool Enabled => Bootstrap.OnDevice || Array.IndexOf(Environment.GetCommandLineArgs(), "-skyguard") >= 0;

        void Start()
        {
            SceneManager.sceneLoaded += (s, m) => { s_Logged = false; s_SimpleApplied = false; };
            IosSettings.Changed += () => m_Next = 0f;
        }

        void Update()
        {
            if (!Enabled) return;
            try { if (s_Fx != 0 && FxAvailable) ApplyFx(EnviroSky.instance.PlayerCamera); }
            catch (Exception e) { Log.Error("fx test: " + e); s_Fx = 0; }
            if (Time.unscaledTime < m_Next && !s_GuardNow) return;
            s_GuardNow = false;
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
            NativeSkybox();
            NativeHeightFog();

            if (IosSettings.SimpleSky) { RemoveSky(main); Simple(sky, main); return; }
            if (s_SimpleApplied) Full(sky, main);

            if (main.clearFlags == CameraClearFlags.Depth || main.clearFlags == CameraClearFlags.Nothing)
            {
                Log.Info($"sky guard: player camera cleared only {main.clearFlags}; now clears to black");
                main.clearFlags = CameraClearFlags.SolidColor;
                main.backgroundColor = Color.black;
            }
            if (!FxOff("Sky")) DrawSkyAfterLighting(main);
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

        /// A shader from StreamingAssets/ics2shaders (the mod's own, compiled for Metal by the shell), or null.
        static Shader ModShader(string name)
        {
            if (!s_BundleTried)
            {
                s_BundleTried = true;
                try
                {
                    string path = System.IO.Path.Combine(Application.streamingAssetsPath, "ics2shaders");
                    var bundle = AssetBundle.LoadFromFile(path);
                    if (bundle == null) Log.Warn("sky guard: no shader bundle at " + path);
                    else foreach (var sh in bundle.LoadAllAssets<Shader>())
                    {
                        s_ModShaders[sh.name] = sh;
                        Log.Info($"sky guard: mod shader {sh.name} {(sh.isSupported ? "ok" : "UNSUPPORTED")}");
                    }
                }
                catch (Exception e) { Log.Error("sky guard: loading the shader bundle: " + e); }
            }
            return s_ModShaders.TryGetValue(name, out var s) && s.isSupported ? s : null;
        }

        static Material Behind()
        {
            if (s_Behind == null && ModShader("Hidden/ICS2/SkyBehind") is Shader sh) s_Behind = new Material(sh) { hideFlags = HideFlags.DontSave };
            return s_Behind;
        }

        /// The game's sky material uses Unity's Skybox/Procedural; the copy converted from the Windows build draws
        /// the sky wrongly on iPhone, so the material gets the same shader compiled natively (its settings stay).
        static void NativeSkybox()
        {
            var box = RenderSettings.skybox;
            if (box == null || box.shader == null || box.shader.name != "Skybox/Procedural") return;
            var native = ModShader("Hidden/ICS2/SkyboxProcedural");
            if (native == null) return;
            box.shader = native;
            Log.Info($"sky guard: sky material '{box.name}' now uses the native procedural sky shader");
        }

        /// The fog sphere of the game's Atmospheric Height Fog (the object renames itself "Height Fog Global").
        static MeshRenderer HeightFog()
        {
            var go = GameObject.Find("Height Fog Global");
            return go != null ? go.GetComponent<MeshRenderer>() : null;
        }

        /// The height fog's own material gets the same fog compiled natively (Hidden/ICS2/HeightFogGlobal). The
        /// converted shader counts a pixel as sky only at exactly the far-plane depth; on iPhone the whole sky
        /// got the full distance fog (grey) instead of the game's light horizon fog.
        static void NativeHeightFog()
        {
            var r = HeightFog();
            var mat = r != null ? r.sharedMaterial : null;
            if (mat == null || mat.shader == null || mat.shader.name != HeightFogShader) return;
            var native = ModShader("Hidden/ICS2/HeightFogGlobal");
            if (native == null) return;
            int queue = mat.renderQueue;
            mat.shader = native;
            mat.renderQueue = queue;   // the fog script sets it again every frame anyway
            Log.Info("sky guard: height fog now uses the native shader " +
                     $"(intensity {Shader.GetGlobalFloat("AHF_FogIntensity"):0.##}, distance {Shader.GetGlobalFloat("AHF_FogDistanceStart"):0.#}-{Shader.GetGlobalFloat("AHF_FogDistanceEnd"):0.#} m, " +
                     $"height {Shader.GetGlobalFloat("AHF_FogHeightStart"):0.#}-{Shader.GetGlobalFloat("AHF_FogHeightEnd"):0.#} m, " +
                     $"sky fog {Shader.GetGlobalFloat("AHF_SkyboxFogIntensity"):0.##} height {Shader.GetGlobalFloat("AHF_SkyboxFogHeight"):0.##} fill {Shader.GetGlobalFloat("AHF_SkyboxFogFill"):0.##}, " +
                     $"colour {Shader.GetGlobalColor("AHF_FogColorStart")})");
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
            var fx = new StringBuilder($"sky: '{main.name}' components:");
            foreach (var b in main.GetComponents<Behaviour>())
                if (b != null) fx.Append(' ').Append(b.GetType().Name).Append(b.enabled ? "" : "(off)");
            Log.Info(fx.ToString());
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
