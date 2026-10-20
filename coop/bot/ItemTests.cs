using System;
using System.Collections.Generic;
using System.Globalization;
using ICS2Coop;
using LiteNetLib;
using LiteNetLib.Utils;

/// Stage 5 scenarios: furniture and other items.
static partial class Program
{
    sealed class ItemEvent
    {
        public Msg Kind;
        public int Id, Player;
        public float[] Pos;
        public bool Built;
        public Dictionary<string, string> State;
        public string Tag;
        public List<string> Tags;
    }

    static readonly List<ItemEvent> s_ItemEvents = new List<ItemEvent>();
    static int s_TableCount = -1;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static void ListenItems()
    {
        s_Net.Message += (from, id, r) =>
        {
            switch (id)
            {
                case Msg.ItemIds:
                {
                    int n = r.GetInt(); for (int i = 0; i < n; i++) { r.GetInt(); r.GetInt(); }
                    int m = r.GetInt(); for (int i = 0; i < m; i++) { r.GetString(); r.GetInt(); }
                    int b = r.GetInt(); for (int i = 0; i < b; i++) { r.GetInt(); r.GetInt(); }
                    s_TableCount = n + m + b;
                    break;
                }
                case Msg.ItemTake: s_ItemEvents.Add(new ItemEvent { Kind = id, Id = r.GetInt(), Player = r.GetInt() }); break;
                case Msg.ItemPlace:
                {
                    var e = new ItemEvent { Kind = id, Id = r.GetInt(), Pos = new[] { r.GetFloat(), r.GetFloat(), r.GetFloat() } };
                    r.GetFloat(); r.GetFloat(); r.GetFloat(); r.GetFloat();
                    e.Built = r.GetBool();
                    s_ItemEvents.Add(e);
                    break;
                }
                case Msg.ItemDestroy: s_ItemEvents.Add(new ItemEvent { Kind = id, Id = r.GetInt() }); break;
                case Msg.ItemMove:
                {
                    var e = new ItemEvent { Kind = id, Id = r.GetInt(), Pos = new[] { r.GetFloat(), r.GetFloat(), r.GetFloat() } };
                    r.GetFloat(); r.GetFloat(); r.GetFloat(); r.GetFloat();
                    e.Built = r.GetBool(); // "final" for moves
                    s_ItemEvents.Add(e);
                    break;
                }
                case Msg.BoxSpawn:
                {
                    var e = new ItemEvent { Kind = id, Id = r.GetInt() };
                    r.GetInt();
                    e.Tag = r.GetString();
                    e.Pos = new[] { r.GetFloat(), r.GetFloat(), r.GetFloat() };
                    s_ItemEvents.Add(e);
                    break;
                }
                case Msg.ShopOrder:
                {
                    var e = new ItemEvent { Kind = id, Tags = new List<string>() };
                    int n = r.GetInt();
                    for (int i = 0; i < n; i++) e.Tags.Add(r.GetString());
                    s_ItemEvents.Add(e);
                    break;
                }
                case Msg.ItemSpawn:
                {
                    var e = new ItemEvent { Kind = id, Id = r.GetInt(), Player = r.GetInt(), State = new Dictionary<string, string>() };
                    int n = r.GetInt();
                    for (int i = 0; i < n; i++)
                    {
                        string k = r.GetString();
                        switch (r.GetByte())
                        {
                            case 0: e.State[k] = r.GetString(); break;
                            case 1: e.State[k] = r.GetFloat().ToString(Inv); break;
                            default: e.State[k] = r.GetInt().ToString(Inv); break;
                        }
                    }
                    s_ItemEvents.Add(e);
                    break;
                }
            }
        };
    }

    static ItemEvent WaitItemEvent(Msg kind, Func<ItemEvent, bool> ok, int timeout, string what)
    {
        ItemEvent found = null;
        WaitFor(() => (found = s_ItemEvents.Find(e => e.Kind == kind && ok(e))) != null, timeout, what);
        s_ItemEvents.Remove(found);
        return found;
    }

    static Dictionary<string, string> ItemReport(int target, int id, Func<Dictionary<string, string>, bool> ok, int timeout, string what)
    {
        s_ReportTarget = target;
        s_ReportDetail = "items=" + id;
        try { return WaitReport(ok, timeout, what); }
        finally { s_ReportTarget = 0; s_ReportDetail = null; }
    }

    static void SendItemPlace(int id, float[] p, bool built, int to = 0)
    {
        var w = s_Net.Begin(Msg.ItemPlace);
        w.Put(id);
        w.Put(p[0]); w.Put(p[1]); w.Put(p[2]);
        w.Put(0f); w.Put(0f); w.Put(0f); w.Put(1f);
        w.Put(built);
        w.Put(true);
        w.Put(0f);
        if (to >= 0) s_Net.SendTo(to); else s_Net.SendAll();
    }

    static void SendItemSpawn(int id, string tag, float[] p, string name, int to = 0)
    {
        var w = s_Net.Begin(Msg.ItemSpawn);
        w.Put(id);
        w.Put(-1);
        var keys = new List<(string, byte, object)>
        {
            ("itemsaver", 0, tag), ("posX", 1, p[0]), ("posY", 1, p[1]), ("posZ", 1, p[2]),
            ("rotX", 1, 0f), ("rotY", 1, 0f), ("rotZ", 1, 0f), ("buildedValue", 0, "True"), ("rbKinematic", 0, "True"),
            ("itemquaternionYSaver", 1, 0f), ("itemnamesave", 0, name),
        };
        w.Put(keys.Count);
        foreach (var (k, t, v) in keys)
        {
            w.Put(k); w.Put(t);
            if (t == 0) w.Put((string)v); else w.Put((float)v);
        }
        if (to >= 0) s_Net.SendTo(to); else s_Net.SendAll();
    }

    static float[] Vec(Dictionary<string, string> r, string key) => ParseVec(r[key]);

    static string V(float[] p) => string.Format(Inv, "{0:0.###},{1:0.###},{2:0.###}", p[0], p[1], p[2]);

    /// Stage 5 (host side, bot is the guest): ids arrive with the world; the host applies the bot's
    /// take / place / create / destroy with the game's bookkeeping (save slots); the host's own real pick-up
    /// and build (PlayerRay.TakeItem / Build), a new item and a removed item reach the bot.
    /// --mutate: expects the placed item 1 m off, so the run must FAIL.
    static void ItemsHost(string address, int port, int timeout, bool mutate)
    {
        ListenItems();
        ConnectWithRetry(address, port, timeout);
        var r0 = WaitReport(r => r["game.ready"] == "True" && r["items.ready"] == "True", 60, "host items ready");
        WaitFor(() => s_TableCount >= 0, 10, "item id table");
        Say($"item ids: {s_TableCount} in the table, host has {r0["items.count"]}");
        Expect(s_TableCount > 0, "empty item table");
        int id = int.Parse(r0["items.any"], Inv);
        Expect(id > 0, "no item in the world to test with");
        int me = s_Net.LocalId;
        var start = ParseVec(r0["player.pos"]);
        for (int i = 0; i < 10; i++) { SendState(me, (float)s_Clock.Elapsed.TotalSeconds, start, 0f); Pump(); }

        var a = ItemReport(0, id, r => r["item." + id + ".exists"] == "True", 5, "item " + id);
        string tag = a["item." + id + ".tag"];
        var p0 = Vec(a, "item." + id + ".pos");
        Say($"testing with item {id} '{tag}' at {V(p0)}");

        // the bot picks it up: the host shows it in the bot's hands and frees its save slot
        { var w = s_Net.Begin(Msg.ItemTake); w.Put(id); w.Put(me); s_Net.SendTo(0); }
        var t = ItemReport(0, id, r => r["item." + id + ".held"] == me.ToString(), 5, "host shows the item in the bot's hands");
        Expect(t["item." + id + ".saved"] == "False", "item still in a save slot while carried");
        Expect(Dist(Vec(t, "item." + id + ".pos"), start) < 2f, "carried item is not at the bot");

        // the bot builds it 0.5 m further: placed, built, saved again
        var p1 = new[] { p0[0] + 0.5f, p0[1], p0[2] };
        SendItemPlace(id, p1, true);
        var expect = mutate ? new[] { p1[0] + 1f, p1[1], p1[2] } : p1;
        var pl = ItemReport(0, id, r => r["item." + id + ".held"] == "-1", 5, "item put down");
        Expect(Dist(Vec(pl, "item." + id + ".pos"), expect) < 0.05f, $"placed at {pl["item." + id + ".pos"]}, expected {V(expect)}");
        Expect(pl["item." + id + ".built"] == "True" && pl["item." + id + ".saved"] == "True", "placed item not built / not saved");

        // the bot creates a new one (e.g. opened a delivery box) and later removes it
        int newId = me * 1000000 + 1;
        var p2 = new[] { p0[0], p0[1] + 0.3f, p0[2] + 0.5f };
        SendItemSpawn(newId, tag, p2, "BotMade");
        var sp = ItemReport(0, newId, r => r["item." + newId + ".exists"] == "True", 5, "host created the bot's item");
        Expect(sp["item." + newId + ".tag"] == tag && sp["item." + newId + ".saved"] == "True", "created item wrong tag / not saved");
        Expect(Dist(Vec(sp, "item." + newId + ".pos"), p2) < 0.05f, "created item at " + sp["item." + newId + ".pos"]);
        s_Net.Begin(Msg.ItemDestroy).Put(newId); s_Net.SendTo(0);
        ItemReport(0, newId, r => r["item." + newId + ".exists"] == "False", 5, "host removed the bot's item");

        // the host's player really picks the item up and builds it elsewhere (PlayerRay code paths)
        s_ItemEvents.Clear();
        Command("take", id.ToString(Inv));
        WaitItemEvent(Msg.ItemTake, e => e.Id == id && e.Player == 0, 5, "host pick-up announced");
        System.Threading.Thread.Sleep(700);
        var p3 = new[] { p0[0] - 0.4f, p0[1], p0[2] };
        Command("build", V(p3));
        var placed = WaitItemEvent(Msg.ItemPlace, e => e.Id == id, 5, "host build announced");
        Expect(placed.Built && Dist(placed.Pos, p3) < 0.05f, $"host build sent {V(placed.Pos)} built={placed.Built}");

        // a new item on the host (what opening a delivery box does), then removed
        Command("spawnitem", tag + "@" + V(p2));
        var ns = WaitItemEvent(Msg.ItemSpawn, e => e.State.ContainsKey("itemsaver"), 5, "host's new item announced");
        Expect(ns.State["itemsaver"] == tag, "new item tag " + ns.State["itemsaver"]);
        Expect(Math.Abs(float.Parse(ns.State["posX"], Inv) - p2[0]) < 1f, "new item position");
        // it falls: the host streams it until it rests, and the last position is where the host has it
        var rest = WaitItemEvent(Msg.ItemMove, e => e.Id == ns.Id && e.Built, 10, "new item came to rest");
        var hr = ItemReport(0, ns.Id, r => r["item." + ns.Id + ".exists"] == "True", 5, "host's new item");
        Expect(Dist(rest.Pos, Vec(hr, "item." + ns.Id + ".pos")) < 0.05f, $"rest position {V(rest.Pos)} != host {hr["item." + ns.Id + ".pos"]}");
        Command("destroyitem", ns.Id.ToString(Inv));
        WaitItemEvent(Msg.ItemDestroy, e => e.Id == ns.Id, 5, "host's removed item announced");
        Say("items: take, place, create, remove both ways OK");

        // the bot orders on its in-game shop: the host's courier delivers a box, which reaches the bot
        var o = s_Net.Begin(Msg.ShopOrder);
        o.Put(1); o.Put(tag); o.Put("$1");
        s_Net.SendTo(0);
        WaitReport(r => r["shop.orders"] == "1", 5, "host queued the bot's order");
        var box = WaitItemEvent(Msg.BoxSpawn, e => e.Tag == tag, 100, "courier delivered the bot's order (box announced)");
        Say($"delivery box {box.Id} with '{box.Tag}' at {V(box.Pos)}");
    }

    /// Stage 5 (guest side, bot hosts): the guest matches the host's item ids to its loaded world; the host's
    /// take / place / create / destroy show on the guest; the guest's own real pick-up and build, new and
    /// removed items, and shop orders reach the host.
    static void ItemsGuest(int guest)
    {
        var r0 = WaitReportFrom(guest, r => r["items.ready"] == "True", 20, "guest matched the item ids");
        Expect(r0["items.missing"] == "0" && r0["items.extra"] == "0", $"guest world differs: missing {r0["items.missing"]}, extra {r0["items.extra"]}");
        int id = int.Parse(r0["items.any"], Inv);
        Expect(id > 0, "no item in the guest's world");
        var start = ParseVec(r0["player.pos"]);
        for (int i = 0; i < 10; i++) { SendState(0, (float)s_Clock.Elapsed.TotalSeconds, new[] { start[0] + 1f, start[1], start[2] }, 0f, guest); Pump(); }

        var a = ItemReport(guest, id, r => r["item." + id + ".exists"] == "True", 5, "item " + id);
        string tag = a["item." + id + ".tag"];
        var p0 = Vec(a, "item." + id + ".pos");
        Say($"testing with item {id} '{tag}' at {V(p0)}");

        { var w = s_Net.Begin(Msg.ItemTake); w.Put(id); w.Put(0); s_Net.SendTo(guest); }
        ItemReport(guest, id, r => r["item." + id + ".held"] == "0", 5, "guest shows the item in the host's hands");
        var p1 = new[] { p0[0] + 0.5f, p0[1], p0[2] };
        SendItemPlace(id, p1, true, guest);
        var pl = ItemReport(guest, id, r => r["item." + id + ".held"] == "-1", 5, "item put down on the guest");
        Expect(Dist(Vec(pl, "item." + id + ".pos"), p1) < 0.05f, "placed at " + pl["item." + id + ".pos"]);
        SendItemSpawn(500, tag, new[] { p0[0], p0[1] + 0.3f, p0[2] + 0.5f }, "HostMade", guest);
        ItemReport(guest, 500, r => r["item.500.exists"] == "True", 5, "guest created the host's item");
        s_Net.Begin(Msg.ItemDestroy).Put(500); s_Net.SendTo(guest);
        ItemReport(guest, 500, r => r["item.500.exists"] == "False", 5, "guest removed the host's item");

        s_ItemEvents.Clear();
        Command("take", id.ToString(Inv), guest);
        WaitItemEvent(Msg.ItemTake, e => e.Id == id && e.Player == guest, 5, "guest pick-up announced");
        System.Threading.Thread.Sleep(700);
        var p3 = new[] { p0[0] - 0.4f, p0[1], p0[2] };
        Command("build", V(p3), guest);
        var placed = WaitItemEvent(Msg.ItemPlace, e => e.Id == id, 5, "guest build announced");
        Expect(placed.Built && Dist(placed.Pos, p3) < 0.05f, $"guest build sent {V(placed.Pos)}");

        Command("spawnitem", tag + "@" + V(p1), guest);
        var ns = WaitItemEvent(Msg.ItemSpawn, e => e.State.ContainsKey("itemsaver"), 5, "guest's new item announced");
        Expect(ns.Id >= guest * 1000000, "guest's new item id " + ns.Id + " outside its range");
        Command("destroyitem", ns.Id.ToString(Inv), guest);
        WaitItemEvent(Msg.ItemDestroy, e => e.Id == ns.Id, 5, "guest's removed item announced");

        Command("order", tag, guest);
        var order = WaitItemEvent(Msg.ShopOrder, e => true, 5, "guest's shop order forwarded");
        Expect(order.Tags.Count == 1 && order.Tags[0] == tag, "order " + string.Join(",", order.Tags));
        WaitReportFrom(guest, r => r["shop.orders"] == "0", 5, "guest keeps no local order (no second courier)");
        Say("guest items: matched world, take, place, create, remove, order OK");
    }
}
