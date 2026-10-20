using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ICS2Coop;
using LiteNetLib;
using LiteNetLib.Utils;

// Headless co-op player for automated tests. It speaks the real protocol, so the game cannot tell it from a
// second phone, and it can ask a test copy of the game (-ics2test) for state reports to assert on.
//
//   CoopBot <scenario> [--address 127.0.0.1] [--port 27815] [--timeout 240]
//
// Prints step lines and ends with exactly one "RESULT: PASS" or "RESULT: FAIL <why>" (exit code 0 / 1).
static class Program
{
    static CoopNet s_Net;
    static readonly Dictionary<string, Dictionary<string, string>> s_Reports = new Dictionary<string, Dictionary<string, string>>();
    static int s_Query;
    static readonly Stopwatch s_Clock = Stopwatch.StartNew();

    static int Main(string[] args)
    {
        string scenario = args.Length > 0 ? args[0] : "handshake";
        string address = Arg(args, "--address", "127.0.0.1");
        int port = int.Parse(Arg(args, "--port", CoopNet.DefaultPort.ToString()));
        int timeout = int.Parse(Arg(args, "--timeout", "240"));

        s_Net = new CoopNet { LocalName = "TestBot", Log = s => Say("net: " + s) };
        s_Net.Message += OnMessage;
        try
        {
            switch (scenario)
            {
                case "selftest": return SelfTest();
                case "handshake": Handshake(address, port, timeout); break;
                case "puppets": Puppets(address, port, timeout, Flag(args, "--mutate")); break;
                default: throw new Fail("unknown scenario " + scenario);
            }
            Console.WriteLine("RESULT: PASS");
            return 0;
        }
        catch (Fail f)
        {
            Console.WriteLine("RESULT: FAIL " + f.Message);
            return 1;
        }
        catch (Exception e)
        {
            Console.WriteLine("RESULT: FAIL exception " + e);
            return 1;
        }
        finally
        {
            s_Net.Stop();
        }
    }

    // ---- scenarios ------------------------------------------------------------------------------------------

    /// Stage 1: join a hosting game, check both sides agree on who is in the session and that the game is in
    /// the cafe with co-op on as host.
    static void Handshake(string address, int port, int timeout)
    {
        ConnectWithRetry(address, port, timeout);
        Expect(s_Net.LocalId > 0, $"local id should be > 0, is {s_Net.LocalId}");
        Expect(s_Net.PlayerCount == 2, $"bot sees {s_Net.PlayerCount} players, expected 2");
        var r = WaitReport(r2 => r2["scene"] == "MainScene" && r2["game.ready"] == "True", 120, "game in the cafe");
        Expect(r["coop.role"] == "host", "game role " + r["coop.role"]);
        Expect(r["coop.players"] == "2", "game sees " + r["coop.players"] + " players");
        Expect(r["coop.names"].Contains(s_Net.LocalId + ":TestBot"), "game does not list the bot: " + r["coop.names"]);
        Say($"game: money={Get(r, "game.money")} day={Get(r, "game.day")} hour={Get(r, "time.hours")} fps={Get(r, "fps")} rtt={s_Net.RoundTripMs()}ms");
    }

    /// Stage 2: the bot walks a circle next to the game's player. The game must show a puppet that follows
    /// it (within interpolation tolerance) and animates; the bot must receive the game player's own body,
    /// matching the position the game reports. Leaving must remove the puppet.
    /// --mutate: the bot expects the puppet 3 m off, so the run must FAIL (proves the check can fail).
    static void Puppets(string address, int port, int timeout, bool mutate)
    {
        var hostStates = new List<PlayerState>();
        s_Net.Message += (from, id, r) => { if (id == Msg.PlayerState) hostStates.Add(PlayerState.Read(r)); };
        ConnectWithRetry(address, port, timeout);
        var r0 = WaitReport(r => r["game.ready"] == "True" && r.ContainsKey("player.pos"), 120, "game in the cafe");
        var center = ParseVec(r0["player.pos"]);
        int me = s_Net.LocalId;

        // the game's own body arrives ~20 times a second and matches its reported position
        WaitFor(() => hostStates.Count >= 20, 10, "20 host player states");
        var hs = hostStates[hostStates.Count - 1];
        var r1 = WaitReport(r => true, 10, "host position report");
        float hostErr = Dist(new[] { hs.X, hs.Y, hs.Z }, ParseVec(r1["player.pos"]));
        Say($"host body: {hostStates.Count} states, last differs from report by {hostErr:0.000} m");
        Expect(hs.Player == 0, "host state player id " + hs.Player);
        Expect(hostErr < 0.25f, $"host body {hostErr:0.00} m away from its reported position");

        // walk a 2 m circle around the host player at ~1.2 m/s for 8 seconds, checking the puppet as we go
        float start = (float)s_Clock.Elapsed.TotalSeconds, lastSend = -1f, worst = 0f;
        float[] sent = center;
        int checks = 0, walking = 0;
        double nextCheck = start + 1.5;
        while (s_Clock.Elapsed.TotalSeconds - start < 8)
        {
            float t = (float)s_Clock.Elapsed.TotalSeconds;
            if (t - lastSend >= 0.05f)
            {
                lastSend = t;
                float ang = (t - start) * 0.6f;
                sent = new[] { center[0] + 2f * (float)Math.Cos(ang), center[1], center[2] + 2f * (float)Math.Sin(ang) };
                SendState(me, t, sent, (float)(-ang * 180 / Math.PI));
            }
            Pump();
            if (s_Clock.Elapsed.TotalSeconds >= nextCheck)
            {
                nextCheck = s_Clock.Elapsed.TotalSeconds + 1.0;
                var expected = mutate ? new[] { sent[0] + 3f, sent[1], sent[2] } : sent;
                var r = Report();
                Expect(r.ContainsKey($"puppet.{me}.pos"), "no puppet for the bot (puppets=" + Get(r, "puppets") + ")");
                float err = Dist(ParseVec(r[$"puppet.{me}.pos"]), expected);
                worst = Math.Max(worst, err);
                checks++;
                if (r[$"puppet.{me}.walk"] == "True") walking++;
                Expect(r[$"puppet.{me}.visible"] == "True", "puppet not visible");
                // 1.2 m/s * (0.1 s interpolation delay + report latency) -> well under 0.6 m
                Expect(err < 0.6f, $"puppet {err:0.00} m from where the bot is");
            }
        }
        Say($"puppet followed: {checks} checks, worst error {worst:0.00} m, walking in {walking}/{checks}");
        Expect(checks >= 5, "too few checks");
        Expect(walking >= checks - 1, $"puppet walk animation on in only {walking}/{checks} checks");

        // stand still: the walk animation stops
        for (int i = 0; i < 30; i++) { SendState(me, (float)s_Clock.Elapsed.TotalSeconds, sent, 0f); Pump(); Thread.Sleep(35); }
        WaitReport(r => r[$"puppet.{me}.walk"] == "False", 5, "puppet idle after stopping");
        Command("shotpuppet", "puppet.png");
        var rs = WaitReport(r => true, 5, "picture taken");
        Say($"puppet size {Get(rs, $"puppet.{me}.size")} m, {Get(rs, $"puppet.{me}.renderers")} renderers");
        var size = ParseVec(rs[$"puppet.{me}.size"]);
        Expect(size[1] > 1.2f && size[1] < 2.4f, $"puppet is {size[1]:0.00} m tall, not person-sized");

        // leaving removes the puppet: reconnect and check only the new id has one
        s_Net.Stop();
        Thread.Sleep(500);
        ConnectWithRetry(address, port, 30);
        SendState(s_Net.LocalId, (float)s_Clock.Elapsed.TotalSeconds, center, 0f);
        WaitReport(r => r["puppets"] == "1" && !r.ContainsKey($"puppet.{me}.pos"), 15, "old puppet removed after leaving");
    }

    static void Command(string command, string arg)
    {
        var w = s_Net.Begin(Msg.TestCommand);
        w.Put(command);
        w.Put(arg ?? "");
        s_Net.SendTo(0);
    }

    static void SendState(int player, float time, float[] p, float yaw)
    {
        var s = new PlayerState { Player = player, Time = time, X = p[0], Y = p[1], Z = p[2], Yaw = yaw };
        s.Write(s_Net.Begin(Msg.PlayerState));
        s_Net.SendTo(0, DeliveryMethod.Sequenced);
    }

    static Dictionary<string, string> Report() => WaitReport(r => true, 5, "report");

    static float[] ParseVec(string s)
    {
        var p = s.Split(',');
        return new[] { float.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture),
                       float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture),
                       float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture) };
    }

    static float Dist(float[] a, float[] b)
    {
        float dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
        return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    static bool Flag(string[] a, string name) => Array.IndexOf(a, name) >= 0;

    /// The scorer must be able to fail: a report that never arrives and a wrong value both have to throw.
    static int SelfTest()
    {
        int failures = 0;
        try { Expect(false, "expected"); } catch (Fail) { failures++; }
        try { WaitFor(() => false, 1, "never true"); } catch (Fail) { failures++; }
        Console.WriteLine(failures == 2 ? "RESULT: PASS" : "RESULT: FAIL scorer did not fail " + (2 - failures) + " time(s)");
        return failures == 2 ? 0 : 1;
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    static void ConnectWithRetry(string address, int port, int timeout)
    {
        var deadline = s_Clock.Elapsed.TotalSeconds + timeout;
        while (s_Clock.Elapsed.TotalSeconds < deadline)
        {
            s_Net.Join(address, port);
            var until = s_Clock.Elapsed.TotalSeconds + 6;
            while (s_Clock.Elapsed.TotalSeconds < until)
            {
                Pump();
                if (s_Net.LocalId > 0) { Say($"joined as player {s_Net.LocalId}"); return; }
            }
        }
        throw new Fail($"could not join {address}:{port} within {timeout}s");
    }

    /// Asks the game for a state report until `ok` holds or the timeout passes. Polls instead of checking once.
    static Dictionary<string, string> WaitReport(Func<Dictionary<string, string>, bool> ok, int timeout, string what)
    {
        Dictionary<string, string> last = null;
        var deadline = s_Clock.Elapsed.TotalSeconds + timeout;
        while (s_Clock.Elapsed.TotalSeconds < deadline)
        {
            string tag = "q" + (++s_Query);
            s_Net.Begin(Msg.TestQuery).Put(tag);
            s_Net.SendTo(0);
            var until = s_Clock.Elapsed.TotalSeconds + 2;
            while (s_Clock.Elapsed.TotalSeconds < until && !s_Reports.ContainsKey(tag)) Pump();
            if (s_Reports.TryGetValue(tag, out last))
            {
                bool good;
                try { good = ok(last); } catch (KeyNotFoundException) { good = false; }
                if (good) { Say("ok: " + what); return last; }
            }
            if (s_Net.LocalId <= 0) throw new Fail("lost the connection while waiting for: " + what);
        }
        throw new Fail($"timed out waiting for: {what} (last report: {(last == null ? "none" : string.Join(" ", last))})");
    }

    static void WaitFor(Func<bool> cond, int timeout, string what)
    {
        var deadline = s_Clock.Elapsed.TotalSeconds + timeout;
        while (s_Clock.Elapsed.TotalSeconds < deadline) { Pump(); if (cond()) return; }
        throw new Fail("timed out waiting for: " + what);
    }

    static void Pump()
    {
        s_Net.Poll();
        Thread.Sleep(15);
    }

    static void OnMessage(int from, Msg id, NetDataReader r)
    {
        if (id != Msg.TestReport) return;
        string tag = r.GetString();
        var map = new Dictionary<string, string>();
        foreach (var line in r.GetString().Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) map[line.Substring(0, eq)] = line.Substring(eq + 1);
        }
        s_Reports[tag] = map;
    }

    static string Get(Dictionary<string, string> r, string k) => r.TryGetValue(k, out var v) ? v : "?";

    static void Expect(bool cond, string why) { if (!cond) throw new Fail(why); }

    static void Say(string s) => Console.WriteLine($"[{s_Clock.Elapsed.TotalSeconds,6:0.0}] {s}");

    static string Arg(string[] a, string name, string def)
    {
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
        return def;
    }

    sealed class Fail : Exception { public Fail(string m) : base(m) { } }
}
