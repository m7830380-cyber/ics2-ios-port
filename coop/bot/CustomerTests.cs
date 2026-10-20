using System;
using System.Collections.Generic;
using ICS2Coop;
using LiteNetLib;
using LiteNetLib.Utils;

/// Stage 6 scenarios: customers and the CafeSoft PC requests.
static partial class Program
{
    sealed class CivState { public bool Active; public float[] Pos; public byte Enum; public float Price; public int Bools; }

    static readonly Dictionary<int, CivState> s_Civs = new Dictionary<int, CivState>();
    static readonly List<(Msg kind, int a, int b)> s_CustomerEvents = new List<(Msg, int, int)>();

    static void ListenCustomers()
    {
        s_Net.Message += (from, id, r) =>
        {
            switch (id)
            {
                case Msg.CustomerStates:
                {
                    int n = r.GetShort();
                    for (int e = 0; e < n; e++)
                    {
                        int i = r.GetShort();
                        var c = new CivState { Active = r.GetBool() };
                        if (c.Active)
                        {
                            c.Pos = new[] { r.GetFloat(), r.GetFloat(), r.GetFloat() };
                            r.GetFloat();
                            c.Enum = r.GetByte(); c.Price = r.GetFloat(); r.GetFloat(); r.GetBool(); r.GetByte();
                            c.Bools = r.GetInt();
                            int ni = r.GetByte();
                            for (int k = 0; k < ni; k++) r.GetShort();
                        }
                        s_Civs[i] = c;
                    }
                    break;
                }
                case Msg.RequestOpen: s_CustomerEvents.Add((id, r.GetInt(), 0)); break;
                case Msg.RequestClose: s_CustomerEvents.Add((id, r.GetInt(), 0)); break;
                case Msg.RequestAnswer: s_CustomerEvents.Add((id, r.GetInt(), r.GetInt())); break;
                case Msg.CustomerAction: s_CustomerEvents.Add((id, r.GetInt(), r.GetInt())); break;
                case Msg.MoneyDelta: { r.GetInt(); s_CustomerEvents.Add((id, (int)Math.Round(r.GetFloat()), 0)); break; }
            }
        };
    }

    static (Msg kind, int a, int b) WaitCustomerEvent(Msg kind, Func<(Msg kind, int a, int b), bool> ok, int timeout, string what)
    {
        (Msg kind, int a, int b) found = default;
        bool got = false;
        WaitFor(() =>
        {
            int i = s_CustomerEvents.FindIndex(e => e.kind == kind && ok(e));
            if (i < 0) return false;
            found = s_CustomerEvents[i];
            s_CustomerEvents.RemoveAt(i);
            got = true;
            return true;
        }, timeout, what);
        return got ? found : default;
    }

    static Dictionary<string, string> CivReport(int target, int civ, Func<Dictionary<string, string>, bool> ok, int timeout, string what)
    {
        s_ReportTarget = target;
        s_ReportDetail = "civs=" + civ;
        try { return WaitReport(ok, timeout, what); }
        finally { s_ReportTarget = 0; s_ReportDetail = null; }
    }

    /// Stage 6 (host side, bot is the guest): the host streams the town's people matching where its game has
    /// them; the guest's "cash taken" makes the host's customer leave the pay state; a PC request on the host
    /// reaches the guest, the guest's answer closes it on the host.
    /// --mutate: expects a streamed person 2 m off, so the run must FAIL.
    static void CustomersHost(string address, int port, int timeout, bool mutate)
    {
        ListenCustomers();
        ListenItems();
        ConnectWithRetry(address, port, timeout);
        var r0 = WaitReport(r => r["game.ready"] == "True" && r["items.ready"] == "True", 60, "host ready");
        WaitFor(() => s_Civs.Count > 0, 5, "people states");
        int civ = -1;
        foreach (var kv in s_Civs) if (kv.Value.Active) { civ = kv.Key; break; }
        Expect(civ >= 0, "no active town person");
        Say($"{s_Civs.Count} people streamed; checking person {civ}");

        // positions follow the host's game (person walks: compare the stream with the report taken right after)
        int checks = 0;
        float worst = 0f;
        for (int i = 0; i < 5; i++)
        {
            System.Threading.Thread.Sleep(300);
            Pump();
            var rep = CivReport(0, civ, r => r.ContainsKey("civ." + civ + ".pos"), 5, "person report");
            var streamed = s_Civs[civ].Pos;
            if (mutate) streamed = new[] { streamed[0] + 2f, streamed[1], streamed[2] };
            float err = Dist(streamed, ParseVec(rep["civ." + civ + ".pos"]));
            worst = Math.Max(worst, err);
            checks++;
        }
        Say($"stream vs host: worst {worst:0.00} m over {checks} checks");
        Expect(worst < 0.5f, $"streamed person {worst:0.00} m from the host's");

        // a customer waits to pay; the guest takes the cash: the host's customer moves on
        Command("forcepay", civ + " 42");
        WaitFor(() => s_Civs.TryGetValue(civ, out var c) && c.Enum == 8 /* PayTime */ && Math.Abs(c.Price - 42f) < 0.01f, 5, "pay state streamed");
        { var w = s_Net.Begin(Msg.CustomerAction); w.Put(civ); w.Put(1); w.Put(0); s_Net.SendTo(0); }
        var after = CivReport(0, civ, r => r["civ." + civ + ".enum"] != "PayTime", 5, "host customer paid");
        Say($"after the guest took the cash: {after["civ." + civ + ".enum"]}, price {after["civ." + civ + ".price"]}");

        // a PC request: needs a table; the bot creates one through item sync
        string tableTag = r0["items.tableTag"];
        Expect(tableTag != "", "no table prefab");
        int tableId = s_Net.LocalId * 1000000 + 7;
        var pos = ParseVec(r0["player.pos"]);
        SendItemSpawn(tableId, tableTag, new[] { pos[0] + 2f, pos[1], pos[2] }, "BotTable");
        ItemReport(0, tableId, r => r["item." + tableId + ".exists"] == "True", 5, "host has the bot's table");
        Command("notify", tableId.ToString());
        WaitCustomerEvent(Msg.RequestOpen, e => e.a == tableId, 5, "PC request reached the guest");
        { var w = s_Net.Begin(Msg.RequestAnswer); w.Put(tableId); w.Put(0); s_Net.SendTo(0); }
        WaitCustomerEvent(Msg.RequestClose, e => e.a == tableId, 5, "request closed on the host after the guest's answer");
        WaitReport(r => r["requests.open"] == "0", 5, "no open request on the host");
    }

    /// Stage 6 (guest side, bot hosts): the guest's town people follow the host's stream with their AI off;
    /// taking a paying customer's cash on the guest sends the money and the action; a PC request from the
    /// host appears in the guest's CafeSoft app, approving it there answers the host.
    static void CustomersGuest(int guest)
    {
        ListenCustomers();
        MoneyState(guest, 12345f, 0); // a real host sends its total at once; the guest's money sync starts with it
        var r0 = WaitReportFrom(guest, r => r["items.ready"] == "True" && r["civils.count"] != "-1" && r["money.synced"] == "True", 20, "guest ready");
        int civ = 0;
        var pr = CivReport(guest, civ, r => r.ContainsKey("civ." + civ + ".params"), 5, "animation parameters");
        int walkIndex = Array.IndexOf(pr["civ." + civ + ".params"].Split(','), "walk");
        Expect(walkIndex >= 0 && walkIndex < 32, "no walk parameter: " + pr["civ." + civ + ".params"]);
        s_WalkBit = 1 << walkIndex;
        var pos = ParseVec(r0["player.pos"]);
        var target = new[] { pos[0] + 2f, pos[1], pos[2] + 1f };
        SendCiv(guest, civ, target, 8 /* PayTime */, 25f, true);
        var c = CivReport(guest, civ, r => r["civ." + civ + ".ai"] == "False" && r["civ." + civ + ".enum"] == "PayTime", 5, "guest person follows the host");
        WaitFor(() => { SendCiv(guest, civ, target, 8, 25f, true); System.Threading.Thread.Sleep(50); return true; }, 1, "");
        c = CivReport(guest, civ, r => Dist(ParseVec(r["civ." + civ + ".pos"]), target) < 0.1f, 5, "guest person at the streamed position");
        Expect(c["civ." + civ + ".price"] == "25", "price " + c["civ." + civ + ".price"]);
        Expect(c["civ." + civ + ".walk"] == "True", "walk animation not applied");

        Command("takecash", civ.ToString(), guest);
        WaitCustomerEvent(Msg.CustomerAction, e => e.a == civ && e.b == 1, 5, "guest's cash taking sent to the host");
        WaitCustomerEvent(Msg.MoneyDelta, e => e.a == 25, 5, "guest's +25 money change");
        var paid = CivReport(guest, civ, r => true, 5, "guest after taking");
        Expect(paid["civ." + civ + ".price"] == "0", "guest could take the cash twice (price " + paid["civ." + civ + ".price"] + ")");

        string tableTag = r0["items.tableTag"];
        int tableId = 900;
        SendItemSpawn(tableId, tableTag, new[] { pos[0] + 2f, pos[1], pos[2] }, "HostTable", guest);
        ItemReport(guest, tableId, r => r["item." + tableId + ".exists"] == "True", 5, "guest has the host's table");
        { var w = s_Net.Begin(Msg.RequestOpen); w.Put(tableId); w.Put("PC 7"); w.Put(1f); w.Put(0); w.Put("casual"); w.Put(civ); s_Net.SendTo(guest); }
        WaitReportFrom(guest, r => r["requests.open"] == "1", 5, "request shown in the guest's CafeSoft app");
        System.Threading.Thread.Sleep(500);
        WaitReportFrom(guest, r => r["requests.open"] == "1", 5, "request still there (the table knows its customer)");
        Command("approve", "", guest);
        var ans = WaitCustomerEvent(Msg.RequestAnswer, e => e.a == tableId, 5, "guest's approval sent to the host");
        Expect(ans.b == 0, "answer was " + ans.b);
        WaitReportFrom(guest, r => r["requests.open"] == "0", 5, "request gone from the guest's app");
        { var w = s_Net.Begin(Msg.RequestOpen); w.Put(tableId); w.Put("PC 7"); w.Put(1f); w.Put(0); w.Put("casual"); w.Put(civ); s_Net.SendTo(guest); }
        WaitReportFrom(guest, r => r["requests.open"] == "1", 5, "second request shown");
        s_Net.Begin(Msg.RequestClose).Put(tableId); s_Net.SendTo(guest);
        WaitReportFrom(guest, r => r["requests.open"] == "0", 5, "request closed by the host disappears");
        Say("guest customers: follow, take cash, PC requests OK");
    }

    static void SendCiv(int to, int index, float[] p, byte state, float price, bool walk)
    {
        var w = s_Net.Begin(Msg.CustomerStates);
        w.Put((short)1);
        w.Put((short)index);
        w.Put(true);
        w.Put(p[0]); w.Put(p[1]); w.Put(p[2]);
        w.Put(0f);
        w.Put(state); w.Put(price); w.Put(10f); w.Put(false); w.Put((byte)2);
        w.Put(walk ? s_WalkBit : 0);
        w.Put((byte)0);
        s_Net.SendTo(to, DeliveryMethod.Unreliable);
    }

    static int s_WalkBit = 1; // set from the game's parameter order in CustomersGuest if known
}
