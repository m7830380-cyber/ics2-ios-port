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
static partial class Program
{
    static CoopNet s_Net;
    static readonly Dictionary<string, Dictionary<string, string>> s_Reports = new Dictionary<string, Dictionary<string, string>>();
    static int s_Query;
    static int s_ReportTarget; // player the state queries go to (0 = host; the bot as host asks its guest)
    static string s_ReportDetail; // extra report detail, e.g. "items=12"

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
                case "snapshot": Snapshot(address, port, timeout, Arg(args, "--fixture", "fixtures")); break;
                case "join": JoinGuest(port, timeout, Arg(args, "--fixture", "fixtures"), Flag(args, "--mutate")); break;
                case "economy-guest":
                {
                    int guest = JoinGuest(port, timeout, Arg(args, "--fixture", "fixtures"), false);
                    EconomyGuest(guest, Flag(args, "--mutate"));
                    break;
                }
                case "economy-host": EconomyHost(address, port, timeout); break;
                case "panel":
                {
                    // pictures of the co-op panel (the game's own capture) for a look by eye
                    ConnectWithRetry(address, port, timeout);
                    WaitReport(r => r["game.ready"] == "True", 60, "game ready");
                    Command("panel", "open");
                    Thread.Sleep(1500);
                    Command("screenshot", "panel-host.png");
                    Thread.Sleep(1500);
                    WaitReport(r => true, 5, "picture taken");
                    break;
                }
                case "items-host": ItemsHost(address, port, timeout, Flag(args, "--mutate")); break;
                case "customers-host": CustomersHost(address, port, timeout, Flag(args, "--mutate")); break;
                case "customers-guest":
                {
                    ListenItems();
                    int guest = JoinGuest(port, timeout, Arg(args, "--fixture", "fixtures"), false);
                    CustomersGuest(guest);
                    break;
                }
                case "items-guest":
                {
                    ListenItems();
                    int guest = JoinGuest(port, timeout, Arg(args, "--fixture", "fixtures"), false);
                    ItemsGuest(guest);
                    break;
                }
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

    /// Stage 4a: a hosting game sends its world when the bot joins. The snapshot must be a save whose money
    /// and day match what the game reports. Saves it as a fixture for the join test (local, never committed:
    /// it is game data).
    static void Snapshot(string address, int port, int timeout, string fixtureDir)
    {
        string json = null;
        byte[] ids = null;
        s_Net.Message += (from, id, r) =>
        {
            if (id == Msg.WorldSnapshot) json = r.GetLargeString();
            else if (id == Msg.ItemIds) ids = r.GetRemainingBytes();
        };
        ConnectWithRetry(address, port, timeout);
        WaitFor(() => json != null && ids != null, 20, "world snapshot and item ids");
        System.IO.Directory.CreateDirectory(fixtureDir);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(fixtureDir, "itemids.bin"), ids);
        Say($"snapshot: {json.Length / 1024} KB");
        var save = SaveJson.Parse(json);
        var rep = WaitReport(r => r["game.ready"] == "True", 30, "game ready");
        Expect(save.Str("gamesaved") == "saved", "snapshot is not marked as a saved game");
        float money = save.Float("money");
        Expect(Math.Abs(money - float.Parse(rep["game.money"], System.Globalization.CultureInfo.InvariantCulture)) < 0.01f,
               $"snapshot money {money} != game {rep["game.money"]}");
        Expect(save.Int("globaldaycount").ToString() == rep["game.day"], $"snapshot day {save.Int("globaldaycount")} != game {rep["game.day"]}");
        Expect(save.Count > 50, $"snapshot has only {save.Count} keys");
        System.IO.Directory.CreateDirectory(fixtureDir);
        System.IO.File.WriteAllText(System.IO.Path.Combine(fixtureDir, "snapshot.json"), json);
        System.IO.File.WriteAllText(System.IO.Path.Combine(fixtureDir, "own-save.save"), SaveJson.Scramble(json));
        Say($"fixture written: {save.Count} keys, money {money}, day {save.Int("globaldaycount")}");
    }

    /// Stage 4b: the bot hosts with a known world (the fixture with money 12345 and day 7); a real game joins
    /// from its main menu, must load the cafe from that world into its session save, and keep its own
    /// language setting. --mutate: the bot sends day 7 but expects 8, so the run must FAIL.
    static int JoinGuest(int port, int timeout, string fixtureDir, bool mutate)
    {
        var save = SaveJson.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(fixtureDir, "snapshot.json")));
        save.SetFloat("money", 12345f);
        save.SetInt("globaldaycount", 7);
        save.SetStr("language", "BOTLANG");
        string world = save.ToJson();

        int guest = -1;
        s_Net.PlayerJoined += (id, name) => guest = id;
        s_Net.LocalName = "TestHost";
        Expect(s_Net.StartHost(port), "bot cannot host");
        WaitFor(() => guest > 0, timeout, "the game to join");
        Say($"game joined as player {guest}; sending the world");
        s_Net.Begin(Msg.WorldSnapshot).PutLargeString(world);
        s_Net.SendTo(guest);

        var idsFile = System.IO.Path.Combine(fixtureDir, "itemids.bin");
        if (System.IO.File.Exists(idsFile))
        {
            var w = s_Net.Begin(Msg.ItemIds);
            var b = System.IO.File.ReadAllBytes(idsFile);
            w.Put(b, 0, b.Length);
            s_Net.SendTo(guest);
        }
        var r = WaitReportFrom(guest, x => x["scene"] == "MainScene" && x["game.ready"] == "True", 180, "guest in the cafe");
        r = WaitReportFrom(guest, x => x["game.day"] != "0", 20, "guest loaded the save");
        Expect(r["save.file"] == "coop-session.save", "guest plays in " + r["save.file"]);
        Expect(r["game.money"] == "12345", "guest money " + r["game.money"]);
        Expect(r["game.day"] == (mutate ? "8" : "7"), "guest day " + r["game.day"]);
        Expect(r["fbpp.language"] != "BOTLANG", "guest took the host's language setting");
        Say($"guest: money {r["game.money"]}, day {r["game.day"]}, language kept ({r["fbpp.language"]})");
        return guest;
    }

    /// Stage 3 (guest side, bot hosts): money totals and the clock from the host apply; a change on the guest
    /// reaches the host once; a host change racing a guest change is merged, not lost; the guest does not
    /// run the day rollover. --mutate: expects the race to lose the guest's change, so the run must FAIL.
    static void EconomyGuest(int guest, bool mutate)
    {
        var deltas = new List<(int seq, float d)>();
        s_Net.Message += (from, id, r) => { if (id == Msg.MoneyDelta) deltas.Add((r.GetInt(), r.GetFloat())); };
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        MoneyState(guest, 20000f, 0);
        WaitReportFrom(guest, r => r["money.synced"] == "True" && r["game.money"] == "20000", 10, "guest shows the host's money");

        Clock(7, 15.5f);
        var c = WaitReportFrom(guest, r => Math.Abs(float.Parse(r["time.hourf"], inv) - 15.5f) < 0.1f, 10, "guest clock jumps to 15:30");
        Expect(c["game.day"] == "7", "guest day " + c["game.day"]);
        Clock(7, 15.6f);
        WaitReportFrom(guest, r => Math.Abs(float.Parse(r["time.hourf"], inv) - 15.6f) < 0.05f, 10, "guest clock follows small changes");

        // the guest spends 150 (the game edits money directly, as the test command does)
        Command("money", "-150", guest);
        WaitFor(() => deltas.Count >= 1, 5, "the guest's money change");
        Expect(deltas.Count == 1 && deltas[0].seq == 1 && Math.Abs(deltas[0].d + 150f) < 0.01f, $"guest sent {string.Join(",", deltas)}");
        WaitReportFrom(guest, r => r["game.money"] == "19850", 5, "guest shows its own change before the host confirms");

        // meanwhile the host earned 100 and has not applied the guest's change yet
        MoneyState(guest, 20100f, 0);
        string raced = mutate ? "20100" : "19950";
        WaitReportFrom(guest, r => r["game.money"] == raced, 5, $"guest merges the host's +100 with its pending -150 ({raced})");

        // the host applies the guest's change
        MoneyState(guest, 19950f, 1);
        WaitReportFrom(guest, r => r["game.money"] == "19950" && r["money.pending"] == "0", 5, "guest change confirmed");
        Thread.Sleep(500);
        Pump();
        Expect(deltas.Count == 1, $"guest re-sent its change: {string.Join(",", deltas)}");

        // the day rollover is the host's: on the guest DailyChanges returns at once
        Command("dailychanges", "", guest);
        Thread.Sleep(1500);
        var d = WaitReportFrom(guest, r => true, 5, "after DailyChanges on the guest");
        Expect(d["coop.guest"] == "True", "guest mode is off");
        Expect(d["game.day"] == "7", "guest ran the day rollover itself: day " + d["game.day"]);
        Expect(d["game.money"] == "19950", "money changed by the day rollover: " + d["game.money"]);

        // going to bed on the guest: no day change, no save (the host saves the world)
        Command("sleep", "", guest);
        Thread.Sleep(1500);
        var s2 = WaitReportFrom(guest, r => true, 5, "after sleeping on the guest");
        Expect(s2["game.day"] == "7", "guest's sleep changed the day: " + s2["game.day"]);
        Expect(s2["save.mtime"] == d["save.mtime"], "guest wrote its session save when sleeping");
    }

    /// Stage 3 (host side, bot is the guest): the host applies a guest's money change once (repeats ignored),
    /// confirms it, and broadcasts its clock.
    static void EconomyHost(string address, int port, int timeout)
    {
        var states = new List<(float total, int ack)>();
        var clocks = new List<(int day, float hour)>();
        s_Net.Message += (from, id, r) =>
        {
            if (id == Msg.MoneyState) states.Add((r.GetFloat(), r.GetInt()));
            else if (id == Msg.Clock) { int day = r.GetInt(); r.GetInt(); r.GetInt(); clocks.Add((day, r.GetFloat())); }
        };
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        ConnectWithRetry(address, port, timeout);
        WaitReport(r => r["game.ready"] == "True", 60, "game ready");
        WaitFor(() => states.Count > 0, 5, "money state from the host");
        float before = float.Parse(WaitReport(r => true, 5, "money")["game.money"], inv);
        Expect(Math.Abs(states[states.Count - 1].total - before) < 0.01f, $"host sent {states[states.Count - 1].total}, has {before}");

        SendMoneyDelta(1, -300f);
        WaitFor(() => states.Count > 0 && states[states.Count - 1].ack == 1, 5, "host confirms change #1");
        var after = WaitReport(r => true, 5, "money after");
        Expect(Math.Abs(float.Parse(after["game.money"], inv) - (before - 300f)) < 0.01f, $"host money {after["game.money"]}, expected {before - 300f}");
        Expect(Math.Abs(states[states.Count - 1].total - (before - 300f)) < 0.01f, "confirmed total " + states[states.Count - 1].total);

        SendMoneyDelta(1, -300f); // repeated packet
        Thread.Sleep(600);
        var again = WaitReport(r => true, 5, "money after repeat");
        Expect(Math.Abs(float.Parse(again["game.money"], inv) - (before - 300f)) < 0.01f, "repeated change applied twice: " + again["game.money"]);

        int n = clocks.Count;
        WaitFor(() => clocks.Count >= n + 2, 4, "two clock updates");
        var rc = WaitReport(r => true, 5, "clock report");
        float hourErr = Math.Abs(clocks[clocks.Count - 1].hour - float.Parse(rc["time.hourf"], inv));
        Expect(hourErr < 0.05f, $"clock {clocks[clocks.Count - 1].hour} vs game {rc["time.hourf"]}");
        Expect(clocks[clocks.Count - 1].day.ToString() == rc["game.day"], "clock day");
        Say($"host: money {before} -> {before - 300f}, clock {clocks[clocks.Count - 1].hour:0.00}h day {clocks[clocks.Count - 1].day}");
    }

    static void MoneyState(int to, float total, int ack)
    {
        var w = s_Net.Begin(Msg.MoneyState);
        w.Put(total);
        w.Put(ack);
        s_Net.SendTo(to);
    }

    static void SendMoneyDelta(int seq, float d)
    {
        var w = s_Net.Begin(Msg.MoneyDelta);
        w.Put(seq);
        w.Put(d);
        s_Net.SendTo(0);
    }

    static void Clock(int day, float hour)
    {
        var w = s_Net.Begin(Msg.Clock);
        w.Put(day);
        w.Put(7);
        w.Put(1);
        w.Put(hour);
        s_Net.SendAll();
    }


    static Dictionary<string, string> WaitReportFrom(int player, Func<Dictionary<string, string>, bool> ok, int timeout, string what)
    {
        s_ReportTarget = player;
        try { return WaitReport(ok, timeout, what); }
        finally { s_ReportTarget = 0; }
    }

    static void Command(string command, string arg, int to = 0)
    {
        var w = s_Net.Begin(Msg.TestCommand);
        w.Put(command);
        w.Put(arg ?? "");
        s_Net.SendTo(to);
    }

    static void SendState(int player, float time, float[] p, float yaw, int to = 0)
    {
        var s = new PlayerState { Player = player, Time = time, X = p[0], Y = p[1], Z = p[2], Yaw = yaw, Lift = PlayerState.NoLift };
        s.Write(s_Net.Begin(Msg.PlayerState));
        s_Net.SendTo(to, DeliveryMethod.Sequenced);
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
            s_Net.Begin(Msg.TestQuery).Put(s_ReportDetail == null ? tag : tag + "|" + s_ReportDetail);
            s_Net.SendTo(s_ReportTarget);
            var until = s_Clock.Elapsed.TotalSeconds + 2;
            while (s_Clock.Elapsed.TotalSeconds < until && !s_Reports.ContainsKey(tag)) Pump();
            if (s_Reports.TryGetValue(tag, out last))
            {
                bool good;
                try { good = ok(last); } catch (KeyNotFoundException) { good = false; }
                if (good) { Say("ok: " + what); return last; }
            }
            if (s_Net.LocalId < 0 || (s_Net.IsHost ? s_Net.PlayerCount < 2 : s_Net.LocalId == 0))
                throw new Fail("lost the connection while waiting for: " + what);
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
        int bar = tag.IndexOf('|');
        if (bar >= 0) tag = tag.Substring(0, bar);
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
