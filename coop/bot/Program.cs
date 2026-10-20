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
