using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ICS2iOS
{
    // Test-only (-ics2test): what the headless bot (coop/bot) can read and do inside a real game, so co-op is
    // checked by scripts instead of playtests. Reports are key=value lines; values use the invariant culture.
    public static class CoopTestAgent
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static void Report(StringBuilder sb)
        {
            var net = CoopSession.Net;
            Line(sb, "scene", SceneManager.GetActiveScene().name);
            Line(sb, "coop.role", !CoopSession.Active ? "off" : CoopSession.IsHost ? "host" : "client");
            Line(sb, "coop.localId", net.LocalId.ToString(Inv));
            Line(sb, "coop.players", net.PlayerCount.ToString(Inv));
            Line(sb, "coop.names", string.Join(",", Names(net)));
            Line(sb, "save.file", ICS2Shim.Platform.SaveFileName);
            Line(sb, "fbpp.language", FBPP.GetString("language"));
            Line(sb, "snapshot.received", WorldSnapshot.ReceivedBytes.ToString(Inv));
            Line(sb, "fps",(1f / Mathf.Max(Time.smoothDeltaTime, 1e-4f)).ToString("0", Inv));
            var gm = GameManager.Instance;
            Line(sb, "game.ready", (gm != null).ToString());
            if (gm != null)
            {
                Line(sb, "game.money", gm.money.ToString("0.##", Inv));
                Line(sb, "game.day", gm.day.ToString(Inv));
                Line(sb, "game.paused", gm.gamePaused.ToString());
                if (gm.player != null) Line(sb, "player.pos", Vec(gm.player.position));
            }
            if (EnviroSky.instance != null)
                Line(sb, "time.hours", EnviroSky.instance.GameTime.Hours.ToString(Inv));
            PlayerSync.Report(sb);
        }

        public static void Run(int from, string command, string arg)
        {
            Log.Info($"test: command '{command}' '{arg}' from player {from}");
            switch (command)
            {
                case "quit":
                    Application.Quit();
                    break;
                case "log":
                    Log.Info("test: " + arg);
                    break;
                case "shotpuppet":
                    ShotPuppet(arg);
                    break;
                default:
                    Log.Warn($"test: unknown command '{command}'");
                    break;
            }
        }

        /// Renders the first puppet from 2.5 m in front with a temporary camera into <profile>/<file>.png.
        /// The game draws the picture itself: nothing outside the game window can end up in it.
        static void ShotPuppet(string file)
        {
            var body = PlayerSync.FirstPuppetBody();
            if (body == null) { Log.Warn("test: shotpuppet: no puppet"); return; }
            var go = new GameObject("CoopTestCamera");
            try
            {
                var cam = go.AddComponent<Camera>();
                var main = GameManager.Instance != null ? GameManager.Instance.playerCamera : Camera.main;
                if (main != null) { cam.CopyFrom(main); cam.RemoveAllCommandBuffers(); }
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.4f, 0.5f, 0.6f);
                cam.renderingPath = RenderingPath.Forward;
                var t = body.transform;
                go.transform.position = t.position + t.forward * 2.5f + Vector3.up * 1.4f;
                go.transform.LookAt(t.position + Vector3.up * 0.9f);
                // only the puppet (moved to a spare layer while rendering), so walls can't hide it
                const int Layer = 31;
                var layers = new System.Collections.Generic.Dictionary<GameObject, int>();
                foreach (var tr in body.GetComponentsInChildren<Transform>(true)) { layers[tr.gameObject] = tr.gameObject.layer; tr.gameObject.layer = Layer; }
                cam.cullingMask = 1 << Layer;
                cam.nearClipPlane = 0.05f;
                var rt = new RenderTexture(640, 360, 24);
                cam.targetTexture = rt;
                cam.Render();
                foreach (var kv in layers) kv.Key.layer = kv.Value;
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(640, 360, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 640, 360), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                cam.targetTexture = null;
                string path = System.IO.Path.Combine(ICS2Shim.Platform.PersistentDataPath, System.IO.Path.GetFileName(file));
                System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
                Object.Destroy(tex);
                rt.Release();
                Object.Destroy(rt);
                Log.Info("test: puppet picture saved to " + path);
            }
            finally
            {
                Object.Destroy(go);
            }
        }

        static System.Collections.Generic.IEnumerable<string> Names(ICS2Coop.CoopNet net)
        {
            foreach (int id in net.Players) yield return id + ":" + net.NameOf(id);
        }

        static string Vec(Vector3 v) => v.x.ToString("0.00", Inv) + "," + v.y.ToString("0.00", Inv) + "," + v.z.ToString("0.00", Inv);

        static void Line(StringBuilder sb, string key, string value) => sb.Append(key).Append('=').Append(value).Append('\n');
    }
}
