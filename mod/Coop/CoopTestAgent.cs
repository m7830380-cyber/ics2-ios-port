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
            Line(sb, "fps", (1f / Mathf.Max(Time.smoothDeltaTime, 1e-4f)).ToString("0", Inv));
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
                default:
                    Log.Warn($"test: unknown command '{command}'");
                    break;
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
