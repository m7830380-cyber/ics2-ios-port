using System;
using System.IO;
using ICS2Shim;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using UnityEngine.SceneManagement;

namespace ICS2iOS
{
    // Entry point of the iOS additions.
    public static class Bootstrap
    {
        public static string Documents { get; private set; }
        public static bool OnDevice => Application.platform == RuntimePlatform.IPhonePlayer;

        // Runs before anything else in the player (before the first scene's Awake calls). The game keeps its
        // saves and settings (FileBasedPrefs, save slots) under Application.persistentDataPath, which is the
        // app's Documents folder on iOS; relative paths land there too.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void EarlyInit()
        {
            Documents = Application.persistentDataPath;
            Log.Init(Documents);
            try
            {
                if (OnDevice)
                    Directory.SetCurrentDirectory(Documents);
            }
            catch (Exception e)
            {
                Log.Error("cannot prepare Documents: " + e);
            }
            Log.Info($"Internet Cafe Simulator 2 iOS starting. cwd={Environment.CurrentDirectory}");
            Log.Info($"device={SystemInfo.deviceModel} os={SystemInfo.operatingSystem} gpu={SystemInfo.graphicsDeviceName} " +
                     $"api={SystemInfo.graphicsDeviceType} mem={SystemInfo.systemMemorySize}MB cpu={SystemInfo.processorCount}x " +
                     $"screen={Screen.width}x{Screen.height}@{Screen.currentResolution.refreshRate}Hz dpi={Screen.dpi} " +
                     $"safe={Screen.safeArea} unity={Application.unityVersion}");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Init()
        {
            try
            {
                Input.simulateMouseWithTouches = false;
                Input.multiTouchEnabled = true;
                TouchSim.Init();
                VInput.Virtual = OnDevice || Input.touchSupported || TouchSim.Enabled;
                Screen.sleepTimeout = SleepTimeout.NeverSleep;
                Platform.ApplyFrameRate();
                Log.Info($"virtual input={VInput.Virtual} targetFrameRate={Application.targetFrameRate}");

                InstallPlayerLoopHook();
                IosWebPlugin.Register();

                var go = new GameObject("ICS2iOS");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<DebugOverlay>();

                SteamOffline.AchievementUnlocked += name => DebugOverlay.Toast("Achievement unlocked");

                SceneManager.sceneLoaded += (s, mode) =>
                {
                    Log.Info($"scene loaded: '{s.name}' ({mode}) roots={s.rootCount} mem={MemoryMB()}MB");
                    SafeArea.OnSceneLoaded();
                    OfflineWeather.OnSceneLoaded();
                    WebViewNote.OnSceneLoaded();
                    Platform.ApplyFrameRate();
                };
                Application.lowMemory += () => Log.Warn($"iOS low memory warning (mono heap {MemoryMB()}MB)");
            }
            catch (Exception e)
            {
                Log.Error("ICS2iOS init failed: " + e);
            }
        }

        static long MemoryMB() => GC.GetTotalMemory(false) >> 20;

        // TouchInput.Tick runs at the start of every frame, after Unity has read the touches and before
        // any MonoBehaviour.Update, so the game always sees a consistent virtual mouse/keyboard.
        static void InstallPlayerLoopHook()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                if (loop.subSystemList[i].type != typeof(PreUpdate)) continue;
                var subs = loop.subSystemList[i].subSystemList;
                var list = new PlayerLoopSystem[subs.Length + 1];
                Array.Copy(subs, list, subs.Length);
                list[subs.Length] = new PlayerLoopSystem { type = typeof(TouchInput), updateDelegate = SafeTick };
                loop.subSystemList[i].subSystemList = list;
                PlayerLoop.SetPlayerLoop(loop);
                Log.Info("touch input hooked into PreUpdate");
                return;
            }
            Log.Error("PreUpdate not found in player loop; touch input will not work");
        }

        static int s_TickErrors;
        static void SafeTick()
        {
            try
            {
                TouchInput.Tick();
            }
            catch (Exception e)
            {
                if (s_TickErrors++ < 20) Log.Error("touch input: " + e);
            }
            finally
            {
                VInput.BeginFrame();
            }
        }
    }
}
