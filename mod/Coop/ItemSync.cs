using System;
using System.Collections.Generic;
using System.Text;
using DG.Tweening;
using ICS2Coop;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ICS2iOS
{
    /// Network id of a synced world object (an Item or a delivery box).
    public class CoopItemId : MonoBehaviour
    {
        public int Id;
        public int HeldBy = -1;     // player carrying it, -1 = in the world
        public int Owner = -1;      // player whose physics moves it right now (thrown), -1 = nobody
        public bool IsBox;
        public float StillSince = -1f; // owner side: when it last started to be still
    }

    // Furniture, PC parts, food and every other pickable thing ("Item"), plus delivery boxes.
    //
    // Item names are not unique in the game itself, so every synced object gets a network id. When a guest
    // joins, the host sends the ids by save slot (items are loaded from the same slots on both sides) and by
    // scene path (start items that were never moved). New objects get ids from the creating player's range.
    //
    // What is sent is what changes the world: picked up (shown in the carrier's hands), put down / built,
    // thrown (the thrower streams it until it rests), created, destroyed. A new object travels as the game's
    // own save data for one item (written through a scratch ItemSaver), so its full state (backpack contents,
    // air conditioner settings, miner cards ...) arrives and the receiver rebuilds it with the game's own
    // loading code. Received changes also update the world bookkeeping the game does around pick-up / build
    // (save slots, table links, lamps, air conditioners), so the host's save includes what the guest built.
    public class ItemSync : MonoBehaviour
    {
        const float ScanInterval = 0.5f, MoveInterval = 0.1f;
        const int ScratchSlot = 987654;
        static readonly string[] SaverKeys =
        {
            "itemsaver", "posX", "posY", "posZ", "rotX", "rotY", "rotZ", "buildedValue", "rbKinematic",
            "backpackitemsave1", "backpackitemsave2", "backpackitemsave3", "backpackitemsave4", "backpackitemsave5",
            "backpackitemsave6", "backpackitemsave7", "backpackitemsave8", "backpackitemsave9", "backpackitemsave10",
            "itemquaternionYSaver", "baseballBatDurability", "climateDegree", "climatecurrentchangedvalue",
            "climateisworking", "climateautomod", "fanisworking", "itemnamesave", "extinguisherfuel", "sprayCount",
            "minerriggraphiccardcount", "minerrigisworking", "btcminerisworking",
        };

        static readonly Dictionary<int, CoopItemId> s_Objects = new Dictionary<int, CoopItemId>();
        static int s_NextId;
        static bool s_Ready;
        static byte[] s_PendingTable;
        static float s_SceneLoadedAt;
        static readonly List<KeyValuePair<Msg, byte[]>> s_Queue = new List<KeyValuePair<Msg, byte[]>>();
        static Item s_LastHeld;
        static int s_LastHeldId;
        float m_NextScan, m_NextMove;
        static bool s_Applying; // applying a remote change: the local watchers must not echo it

        public static int Count => s_Objects.Count;
        public static bool Ready => s_Ready;
        static int s_Missing, s_Extra;

        void OnEnable()
        {
            CoopSession.Received += OnMessage;
            CoopSession.Net.PlayerJoined += OnPlayerJoined;
            CoopSession.Net.PlayerLeft += OnPlayerLeft;
            SceneManager.sceneLoaded += (s, m) => { Reset(); s_SceneLoadedAt = Time.unscaledTime; };
        }

        /// New scene: the objects are new. Messages queued while the cafe loads are kept for the table.
        static void Reset()
        {
            s_Objects.Clear();
            s_Ready = false;
            s_LastHeld = null;
            s_LastHeldId = 0;
        }

        public static void EndSession() { Reset(); s_PendingTable = null; s_Queue.Clear(); s_NextId = 0; }

        int NewId()
        {
            if (CoopSession.IsHost) return ++s_NextId;
            return CoopSession.Net.LocalId * 1000000 + ++s_NextId;
        }

        // ---- registry ---------------------------------------------------------------------------------------
        static CoopItemId Tag(GameObject go, int id, bool box)
        {
            var t = go.GetComponent<CoopItemId>() ?? go.AddComponent<CoopItemId>();
            t.Id = id;
            t.IsBox = box;
            if (id > 0) s_Objects[id] = t;
            return t;
        }

        static string ScenePath(Transform t)
        {
            var sb = new StringBuilder();
            for (; t != null; t = t.parent) sb.Insert(0, "/" + t.name + "#" + t.GetSiblingIndex());
            return sb.ToString();
        }

        static bool IsSceneObject(GameObject go) => !go.name.Contains("(Clone)");

        /// Host: give every object an id (first guest) and send the table to a joining guest.
        void OnPlayerJoined(int player, string name)
        {
            if (!CoopSession.IsHost || SaveManager.Instance == null) return;
            if (!s_Ready)
            {
                s_NextId = 0;
                foreach (var item in FindObjectsOfType<Item>()) Tag(item.gameObject, ++s_NextId, false);
                foreach (var box in FindObjectsOfType<CargoBox>()) Tag(box.gameObject, ++s_NextId, true);
                s_Ready = true;
                Log.Info($"coop: {s_Objects.Count} world objects got network ids");
            }
            // the snapshot (sent just before) holds the save slots; this maps slots / scene paths to ids
            var slots = new List<KeyValuePair<int, int>>();
            var paths = new List<KeyValuePair<string, int>>();
            var boxSlots = new List<KeyValuePair<int, int>>();
            var loose = new List<CoopItemId>();
            foreach (var t in s_Objects.Values)
            {
                if (t == null) continue;
                if (t.IsBox)
                {
                    var bs = BoxSaverOf(t.GetComponent<CargoBox>());
                    if (bs != null) boxSlots.Add(new KeyValuePair<int, int>(bs.number, t.Id));
                    else loose.Add(t);
                    continue;
                }
                var item = t.GetComponent<Item>();
                if (item.mySaver != null && t.HeldBy < 0) slots.Add(new KeyValuePair<int, int>(item.mySaver.number, t.Id));
                else if (IsSceneObject(t.gameObject) && t.HeldBy < 0) paths.Add(new KeyValuePair<string, int>(ScenePath(t.transform), t.Id));
                else loose.Add(t);
            }
            var w = CoopSession.Net.Begin(Msg.ItemIds);
            w.Put(slots.Count); foreach (var kv in slots) { w.Put(kv.Key); w.Put(kv.Value); }
            w.Put(paths.Count); foreach (var kv in paths) { w.Put(kv.Key); w.Put(kv.Value); }
            w.Put(boxSlots.Count); foreach (var kv in boxSlots) { w.Put(kv.Key); w.Put(kv.Value); }
            CoopSession.Net.SendTo(player);
            foreach (var t in loose) SendSpawn(t, player);
            Log.Info($"coop: item ids to player {player}: {slots.Count} by slot, {paths.Count} by scene path, " +
                     $"{boxSlots.Count} boxes, {loose.Count} sent whole");
        }

        void OnPlayerLeft(int player, string name)
        {
            // whatever the player carried drops where its puppet stood
            foreach (var t in s_Objects.Values)
                if (t != null && t.HeldBy == player) PlaceRemote(t, t.transform.position, Quaternion.Euler(0, t.transform.eulerAngles.y, 0), false, false, 0f);
        }

        /// Guest: the cafe has loaded from the host's save; match the table to the loaded objects.
        static void ApplyTable(NetDataReader r)
        {
            var sm = SaveManager.Instance;
            var bySlot = new Dictionary<int, ItemSaver>();
            foreach (var s in sm.allSavers) bySlot[s.number] = s;
            var byPath = new Dictionary<string, Item>();
            foreach (var item in FindObjectsOfType<Item>())
                if (IsSceneObject(item.gameObject)) byPath[ScenePath(item.transform)] = item;
            int matched = 0, missing = 0, maxId = 0;
            int n = r.GetInt();
            for (int i = 0; i < n; i++)
            {
                int slot = r.GetInt(), id = r.GetInt();
                if (bySlot.TryGetValue(slot, out var s) && s.item != null) { Tag(s.item.gameObject, id, false); matched++; }
                else missing++;
                maxId = Math.Max(maxId, id);
            }
            n = r.GetInt();
            for (int i = 0; i < n; i++)
            {
                string path = r.GetString(); int id = r.GetInt();
                if (byPath.TryGetValue(path, out var item)) { Tag(item.gameObject, id, false); matched++; }
                else missing++;
            }
            n = r.GetInt();
            var boxBySlot = new Dictionary<int, ZamazorBoxSaver>();
            foreach (var b in sm.zamazorBoxSaver) boxBySlot[b.number] = b;
            for (int i = 0; i < n; i++)
            {
                int slot = r.GetInt(), id = r.GetInt();
                if (boxBySlot.TryGetValue(slot, out var b) && b._cargoBox != null) { Tag(b._cargoBox.gameObject, id, true); matched++; }
                else missing++;
            }
            // objects the host does not have (should not happen: both loaded the same save) stay out of sync
            int extra = 0;
            foreach (var item in FindObjectsOfType<Item>()) if (item.GetComponent<CoopItemId>() == null) { Tag(item.gameObject, -1, false); extra++; }
            foreach (var box in FindObjectsOfType<CargoBox>()) if (box.GetComponent<CoopItemId>() == null) { Tag(box.gameObject, -1, true); extra++; }
            s_Ready = true;
            s_Missing = missing;
            s_Extra = extra;
            Log.Info($"coop: item ids matched {matched}, missing {missing}, unmatched local {extra}");
            if (missing > 0 || extra > 0) Log.Warn("coop: the world differs from the host's in some items");
        }

        // ---- local changes -> network ----------------------------------------------------------------------
        void Update()
        {
            if (!CoopSession.Active || SaveManager.Instance == null || GameManager.Instance == null) return;
            if (!s_Ready)
            {
                // guest: the table arrived with the snapshot; the save loads 0.5 s after the scene starts
                if (CoopSession.IsClient && s_PendingTable != null && Time.unscaledTime - s_SceneLoadedAt > 2f &&
                    SceneManager.GetActiveScene().name == "MainScene")
                {
                    ApplyTable(new NetDataReader(s_PendingTable));
                    s_PendingTable = null;
                    foreach (var kv in s_Queue) Handle(kv.Key, new NetDataReader(kv.Value));
                    s_Queue.Clear();
                }
                return;
            }
            try
            {
                WatchHands();
                if (Time.unscaledTime >= m_NextScan) { m_NextScan = Time.unscaledTime + ScanInterval; Scan(); }
                if (Time.unscaledTime >= m_NextMove) { m_NextMove = Time.unscaledTime + MoveInterval; StreamMoving(); }
            }
            catch (Exception e)
            {
                Log.Error("coop: item sync: " + e);
            }
        }

        /// The local player's hand: picked up / built / thrown / put away.
        void WatchHands()
        {
            var ray = GameManager.Instance.playerRay;
            var held = ray != null ? ray.takedItem : null;
            if (held == s_LastHeld) return;
            int me = CoopSession.Net.LocalId;
            if (s_LastHeld != null)
            {
                var t = s_LastHeld.GetComponent<CoopItemId>();
                if (t != null && t.Id > 0)
                {
                    t.HeldBy = -1;
                    var rb = s_LastHeld.GetComponent<Rigidbody>();
                    if (s_LastHeld.builded) SendPlace(t, s_LastHeld);
                    else if (rb != null && !rb.isKinematic) { t.Owner = me; SendMove(t, false, DeliveryMethod.ReliableOrdered); }
                    else SendPlace(t, s_LastHeld);
                }
            }
            else if (s_LastHeldId > 0 && s_Objects.TryGetValue(s_LastHeldId, out var gone) && gone == null)
            {
                // put into the inventory (the game destroys the object)
                SendDestroy(s_LastHeldId);
                s_Objects.Remove(s_LastHeldId);
            }
            s_LastHeld = held;
            s_LastHeldId = 0;
            if (held != null)
            {
                var t = held.GetComponent<CoopItemId>();
                if (t == null || t.Id == 0)
                {
                    // taken out of the inventory: a new object
                    t = Tag(held.gameObject, NewId(), false);
                    t.HeldBy = me;
                    SendSpawn(t, -1);
                }
                else if (t.Id > 0)
                {
                    t.HeldBy = me;
                    t.Owner = -1;
                    var w = CoopSession.Net.Begin(Msg.ItemTake);
                    w.Put(t.Id);
                    w.Put(me);
                    CoopSession.Net.SendAll();
                }
                s_LastHeldId = t.Id;
            }
        }

        /// New and destroyed objects, whatever code made or removed them.
        void Scan()
        {
            foreach (var item in FindObjectsOfType<Item>())
                if (item.GetComponent<CoopItemId>() == null && item != GameManager.Instance.playerRay.takedItem)
                {
                    var t = Tag(item.gameObject, NewId(), false);
                    SendSpawn(t, -1);
                    // a new item still falling (out of a box): send its motion until it rests
                    var rb = item.GetComponent<Rigidbody>();
                    if (rb != null && !rb.isKinematic && !rb.IsSleeping()) t.Owner = CoopSession.Net.LocalId;
                }
            foreach (var box in FindObjectsOfType<CargoBox>())
                if (box.GetComponent<CoopItemId>() == null)
                    SendBoxSpawn(Tag(box.gameObject, NewId(), true));
            List<int> gone = null;
            foreach (var kv in s_Objects)
                if (kv.Value == null) (gone = gone ?? new List<int>()).Add(kv.Key);
            if (gone == null) return;
            foreach (int id in gone)
            {
                s_Objects.Remove(id);
                if (id != s_LastHeldId) SendDestroy(id);
            }
        }

        /// Thrown items: the thrower sends the motion until the item rests. An item that starts moving here by
        /// itself (blown away by the bomb, knocked over, falling) is sent the same way by this device: on the
        /// others it is kinematic and follows, so only one device's physics moves it.
        void StreamMoving()
        {
            int me = CoopSession.Net.LocalId;
            bool claim = CoopSession.WorldReady;
            foreach (var t in s_Objects.Values)
            {
                if (t == null) continue;
                if (t.Owner < 0 && t.HeldBy < 0 && claim)
                {
                    var body = t.GetComponent<Rigidbody>();
                    if (body != null && !body.isKinematic && !body.IsSleeping() && body.velocity.sqrMagnitude > 0.04f)
                    {
                        t.Owner = me;
                        t.StillSince = -1f;
                    }
                }
                if (t.Owner != me) continue;
                var rb = t.GetComponent<Rigidbody>();
                bool still = rb == null || rb.isKinematic || rb.IsSleeping() ||
                             (rb.velocity.sqrMagnitude < 0.0004f && rb.angularVelocity.sqrMagnitude < 0.0004f);
                if (!still) t.StillSince = -1f;
                else if (t.StillSince < 0f) t.StillSince = Time.unscaledTime;
                // at rest once still for 0.3 s (a new or thrown item starts with no speed for a moment)
                if (still && Time.unscaledTime - t.StillSince >= 0.3f)
                {
                    t.Owner = -1;
                    t.StillSince = -1f;
                    SendMove(t, true, DeliveryMethod.ReliableOrdered);
                }
                else SendMove(t, false, DeliveryMethod.Sequenced);
            }
        }

        static void SendPlace(CoopItemId t, Item item)
        {
            var rb = item.GetComponent<Rigidbody>();
            var w = CoopSession.Net.Begin(Msg.ItemPlace);
            w.Put(t.Id);
            PutPose(w, t.transform);
            w.Put(item.builded);
            w.Put(rb != null && rb.isKinematic);
            w.Put(item.quaternionYSaver);
            CoopSession.Net.SendAll();
        }

        static void SendMove(CoopItemId t, bool final, DeliveryMethod how)
        {
            var rb = t.GetComponent<Rigidbody>();
            var w = CoopSession.Net.Begin(Msg.ItemMove);
            w.Put(t.Id);
            PutPose(w, t.transform);
            w.Put(final);
            w.Put(rb != null && rb.isKinematic);
            CoopSession.Net.SendAll(how);
        }

        static void SendDestroy(int id)
        {
            CoopSession.Net.Begin(Msg.ItemDestroy).Put(id);
            CoopSession.Net.SendAll();
        }

        /// The object as the game saves it, plus who holds it. to = -1: everyone.
        static void SendSpawn(CoopItemId t, int to)
        {
            var item = t.GetComponent<Item>();
            var w = CoopSession.Net.Begin(Msg.ItemSpawn);
            w.Put(t.Id);
            w.Put(t.HeldBy);
            WriteItemState(item, w);
            if (to >= 0) CoopSession.Net.SendTo(to); else CoopSession.Net.SendAll();
        }

        static void SendBoxSpawn(CoopItemId t)
        {
            var box = t.GetComponent<CargoBox>();
            var w = CoopSession.Net.Begin(Msg.BoxSpawn);
            w.Put(t.Id);
            w.Put(BoxPrefabIndex(box));
            w.Put(box.item != null ? box.item.itemTag : "");
            PutPose(w, t.transform);
            w.Put(t.name);
            CoopSession.Net.SendAll();
        }

        static void PutPose(NetDataWriter w, Transform tr)
        {
            var p = tr.position; var q = tr.rotation;
            w.Put(p.x); w.Put(p.y); w.Put(p.z);
            w.Put(q.x); w.Put(q.y); w.Put(q.z); w.Put(q.w);
        }

        static void GetPose(NetDataReader r, out Vector3 p, out Quaternion q)
        {
            p = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
            q = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
        }

        /// One item's save keys, written through a scratch ItemSaver (the game's own per-item save code).
        static void WriteItemState(Item item, NetDataWriter w)
        {
            var holder = new GameObject("CoopScratchSaver");
            try
            {
                var saver = holder.AddComponent<ItemSaver>();
                saver.number = ScratchSlot;
                var realSaver = item.mySaver;
                saver.AddItem(item);
                saver.Save();
                item.mySaver = realSaver; // AddItem pointed the item at the scratch saver
                var keys = new List<string>();
                foreach (var k in SaverKeys) if (FBPP.HasKey(k + ScratchSlot)) keys.Add(k);
                w.Put(keys.Count);
                foreach (var k in keys)
                {
                    string key = k + ScratchSlot;
                    w.Put(k);
                    if (FBPP.HasKeyForString(key)) { w.Put((byte)0); w.Put(FBPP.GetString(key)); }
                    else if (FBPP.HasKeyForFloat(key)) { w.Put((byte)1); w.Put(FBPP.GetFloat(key)); }
                    else { w.Put((byte)2); w.Put(FBPP.GetInt(key)); }
                    FBPP.DeleteKey(key);
                }
            }
            finally
            {
                Object.Destroy(holder);
            }
        }

        /// Rebuilds an item from save keys with the game's ItemSaver.Load.
        static Item ReadItemState(NetDataReader r)
        {
            int n = r.GetInt();
            var keys = new List<string>();
            for (int i = 0; i < n; i++)
            {
                string key = r.GetString() + ScratchSlot;
                keys.Add(key);
                switch (r.GetByte())
                {
                    case 0: FBPP.SetString(key, r.GetString()); break;
                    case 1: FBPP.SetFloat(key, r.GetFloat()); break;
                    default: FBPP.SetInt(key, r.GetInt()); break;
                }
            }
            var holder = new GameObject("CoopScratchSaver");
            try
            {
                var saver = holder.AddComponent<ItemSaver>();
                saver.number = ScratchSlot;
                saver.Load();
                var item = saver.item;
                saver.DeleteItem();
                return item;
            }
            finally
            {
                foreach (var k in keys) FBPP.DeleteKey(k);
                Object.Destroy(holder);
            }
        }

        static ZamazorBoxSaver BoxSaverOf(CargoBox box)
        {
            foreach (var b in SaveManager.Instance.zamazorBoxSaver) if (b._cargoBox == box) return b;
            return null;
        }

        static int BoxPrefabIndex(CargoBox box)
        {
            var prefabs = GameManager.Instance.cargoboxes;
            for (int i = 0; i < prefabs.Length; i++)
                if (box.name.StartsWith(prefabs[i].name, StringComparison.Ordinal)) return i;
            return 0;
        }

        // ---- network -> world ------------------------------------------------------------------------------
        void OnMessage(int from, Msg id, NetDataReader r)
        {
            if (id < Msg.ItemIds || id > Msg.ShopOrder) return;
            // the host forwards every guest change to the other guests
            if (CoopSession.IsHost && id != Msg.ShopOrder)
                CoopSession.Net.Relay(from, id, r, id == Msg.ItemMove ? DeliveryMethod.Sequenced : DeliveryMethod.ReliableOrdered);
            if (id == Msg.ItemIds)
            {
                if (CoopSession.IsClient) { s_PendingTable = r.GetRemainingBytes(); s_Ready = false; }
                return;
            }
            if (!s_Ready) { s_Queue.Add(new KeyValuePair<Msg, byte[]>(id, r.GetRemainingBytes())); return; }
            Handle(id, r, from);
        }

        static void Handle(Msg id, NetDataReader r, int from = 0)
        {
            s_Applying = true;
            try
            {
                switch (id)
                {
                    case Msg.ItemSpawn:
                    {
                        int oid = r.GetInt(), heldBy = r.GetInt();
                        if (s_Objects.TryGetValue(oid, out var existing) && existing != null) return;
                        var item = ReadItemState(r);
                        if (item == null) { Log.Warn($"coop: could not create item {oid}"); return; }
                        var t = Tag(item.gameObject, oid, false);
                        if (heldBy >= 0) TakeRemote(t, heldBy);
                        else RegisterInSaver(item);
                        break;
                    }
                    case Msg.ItemTake:
                    {
                        int oid = r.GetInt(), player = r.GetInt();
                        if (Find(oid, out var t)) TakeRemote(t, player);
                        break;
                    }
                    case Msg.ItemPlace:
                    {
                        int oid = r.GetInt();
                        GetPose(r, out var p, out var q);
                        bool built = r.GetBool(), kinematic = r.GetBool();
                        float qy = r.GetFloat();
                        if (Find(oid, out var t)) PlaceRemote(t, p, q, built, kinematic, qy);
                        break;
                    }
                    case Msg.ItemMove:
                    {
                        int oid = r.GetInt();
                        GetPose(r, out var p, out var q);
                        bool final = r.GetBool(), kinematic = r.GetBool();
                        if (Find(oid, out var t)) MoveRemote(t, p, q, final, kinematic);
                        break;
                    }
                    case Msg.ItemDestroy:
                    {
                        int oid = r.GetInt();
                        if (Find(oid, out var t)) DestroyRemote(t);
                        break;
                    }
                    case Msg.BoxSpawn:
                        BoxSpawnRemote(r);
                        break;
                    case Msg.ShopOrder:
                        if (CoopSession.IsHost) ShopOrderRemote(r, from);
                        break;
                }
            }
            finally
            {
                s_Applying = false;
            }
        }

        static bool Find(int id, out CoopItemId t)
        {
            if (s_Objects.TryGetValue(id, out t) && t != null) return true;
            Log.Warn($"coop: no object with network id {id}");
            return false;
        }

        /// Another player picked it up: the world bookkeeping of PlayerRay.TakeItem, then into their hands.
        static void TakeRemote(CoopItemId t, int player)
        {
            var item = t.GetComponent<Item>();
            t.HeldBy = player;
            t.Owner = -1;
            t.transform.DOKill();
            if (item != null)
            {
                item.builded = false;
                if (item.myTableController != null) item.myTableController.DeleteMe(item);
                var table = item.GetComponent<TableController>();
                if (table != null) table.DeleteAll();
                if (item.mySaver != null) item.mySaver.DeleteItem();
                var lamp = item.GetComponent<LightObj>();
                if (lamp != null) lamp.DeleteLamp();
                var start = item.GetComponent<StartGameItem>();
                if (start != null) start.Taked();
                var arcade = item.GetComponent<ArcadeMachine>();
                if (arcade != null) arcade.MachineTaked();
                var ac = item.GetComponent<AirConditioner>();
                if (ac != null) { ac.ClimateSetToRoomTempature(reset: true); ac.ClimateStopped(); }
                var fan = item.GetComponent<Fan>();
                if (fan != null) fan.StopFan();
                var pad = item.GetComponent<MousePad>();
                if (pad != null) pad.DeleteMyTable();
                var jug = item.GetComponent<CoffeeJug>();
                if (jug != null && GameManager.Instance.coffeemachine != null && GameManager.Instance.coffeemachine.myJug == jug)
                    GameManager.Instance.coffeemachine.JugTaked();
                item.triggerCount = 0;
            }
            foreach (var c in t.GetComponents<Collider>()) { c.isTrigger = false; c.enabled = false; }
            var rb = t.GetComponent<Rigidbody>() ?? t.gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            var body = PlayerSync.PuppetBody(player);
            if (body != null)
            {
                t.transform.SetParent(body.transform, true);
                t.transform.localPosition = new Vector3(0f, 1.1f, 0.5f);
                t.transform.localRotation = Quaternion.identity;
            }
            else t.gameObject.SetActive(false);
        }

        /// Put down / built by another player: the world bookkeeping of PlayerRay.BuildRoutine / Throw.
        static void PlaceRemote(CoopItemId t, Vector3 p, Quaternion q, bool built, bool kinematic, float qy)
        {
            var item = t.GetComponent<Item>();
            t.HeldBy = -1;
            t.Owner = -1;
            t.transform.DOKill();
            t.transform.SetParent(null, true);
            t.gameObject.SetActive(true);
            t.transform.SetPositionAndRotation(p, q);
            t.gameObject.layer = LayerMask.NameToLayer("Default");
            foreach (var c in t.GetComponents<Collider>()) { c.isTrigger = false; c.enabled = true; }
            var rb = t.GetComponent<Rigidbody>();
            if (rb != null) rb.isKinematic = kinematic;
            if (item == null) return;
            item.builded = built;
            item.quaternionYSaver = qy;
            RegisterInSaver(item);
            if (built)
            {
                var lamp = item.GetComponent<LightObj>();
                if (lamp != null) lamp.LampBuilded();
                var ac = item.GetComponent<AirConditioner>();
                if (ac != null) ac.ClimateBuilded();
            }
        }

        static void MoveRemote(CoopItemId t, Vector3 p, Quaternion q, bool final, bool kinematic)
        {
            if (t.HeldBy >= 0) PlaceRemote(t, p, q, false, true, t.GetComponent<Item>()?.quaternionYSaver ?? 0f);
            t.Owner = -1;
            var rb = t.GetComponent<Rigidbody>();
            if (rb != null) rb.isKinematic = true; // follow the owner's physics, not our own
            t.transform.SetPositionAndRotation(p, q);
            if (final && rb != null) rb.isKinematic = kinematic;
            if (final) { var item = t.GetComponent<Item>(); if (item != null) RegisterInSaver(item); }
        }

        static void DestroyRemote(CoopItemId t)
        {
            s_Objects.Remove(t.Id);
            var item = t.GetComponent<Item>();
            if (item != null)
            {
                if (item.myTableController != null) item.myTableController.DeleteMe(item);
                if (item.mySaver != null) item.mySaver.DeleteItem();
                var lamp = item.GetComponent<LightObj>();
                if (lamp != null) lamp.DeleteLamp();
            }
            if (s_LastHeld == item) { s_LastHeld = null; s_LastHeldId = 0; }
            Object.Destroy(t.gameObject);
        }

        static void BoxSpawnRemote(NetDataReader r)
        {
            int oid = r.GetInt(), prefab = r.GetInt();
            string tag = r.GetString();
            GetPose(r, out var p, out var q);
            string name = r.GetString();
            if (s_Objects.TryGetValue(oid, out var existing) && existing != null) return;
            var prefabs = GameManager.Instance.cargoboxes;
            var go = Object.Instantiate(prefabs[Mathf.Clamp(prefab, 0, prefabs.Length - 1)].gameObject, p, q);
            go.name = name;
            var box = go.GetComponent<CargoBox>();
            box.item = GameManager.Instance.GiveMeItemObject(tag);
            Tag(go, oid, true);
            foreach (var b in SaveManager.Instance.zamazorBoxSaver)
                if (b._cargoBox == null) { b._cargoBox = box; break; }
        }

        static void RegisterInSaver(Item item)
        {
            if (item.mySaver != null) return;
            foreach (var s in SaveManager.Instance.allSavers)
                if (s.item == null) { s.AddItem(item); return; }
            Log.Warn("coop: no free save slot for " + item.name);
        }

        // ---- shop ---------------------------------------------------------------------------------------
        /// Guest: an order placed on the guest's in-game shop is delivered by the host's courier.
        public static void ForwardOrders()
        {
            var zm = GameManager.Instance != null ? GameManager.Instance.zamazorManager : null;
            if (zm == null || zm.myOrders.Count == 0) return;
            foreach (var order in zm.myOrders)
            {
                var w = CoopSession.Net.Begin(Msg.ShopOrder);
                w.Put(order.orderedItems.Count);
                foreach (var it in order.orderedItems) w.Put(it.itemTag);
                w.Put(order.priceTxt != null ? order.priceTxt.text : "");
                CoopSession.Net.SendTo(0);
                Log.Info($"coop: shop order with {order.orderedItems.Count} item(s) sent to the host for delivery");
                if (order != null) Object.Destroy(order.gameObject);
            }
            zm.myOrders.Clear();
        }

        static void ShopOrderRemote(NetDataReader r, int from)
        {
            var zm = GameManager.Instance.zamazorManager;
            int n = r.GetInt();
            var go = Object.Instantiate(zm.orderPrefab, zm.orderParent);
            var order = go.GetComponent<MyOrder>();
            for (int i = 0; i < n; i++)
            {
                var obj = GameManager.Instance.GiveMeItemObject(r.GetString());
                if (obj != null) order.orderedItems.Add(obj);
            }
            order.priceTxt.text = r.GetString();
            order.piecesTxt.text = order.orderedItems.Count.ToString();
            order.notYetDeliveredObj.SetActive(true);
            order.deliveredObj.SetActive(false);
            order.ontheWayObj.SetActive(false);
            order.delivered = false;
            zm.myOrders.Add(order);
            Log.Info($"coop: {CoopSession.Net.NameOf(from)} ordered {order.orderedItems.Count} item(s); the courier brings them");
        }

        void LateUpdate()
        {
            if (CoopSession.IsClient && s_Ready) ForwardOrders();
        }

        // ---- tests ----------------------------------------------------------------------------------------
        public static void Report(StringBuilder sb, string detail)
        {
            sb.Append("items.ready=").Append(s_Ready).Append('\n');
            sb.Append("items.count=").Append(s_Objects.Count).Append('\n');
            sb.Append("items.missing=").Append(s_Missing).Append('\n');
            sb.Append("items.extra=").Append(s_Extra).Append('\n');
            var zm = GameManager.Instance != null ? GameManager.Instance.zamazorManager : null;
            sb.Append("shop.orders=").Append(zm != null ? zm.myOrders.Count + zm.readytoDeliveryOrders.Count : -1).Append('\n');
            if (string.IsNullOrEmpty(detail)) return;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var part in detail.Split(','))
            {
                if (!int.TryParse(part, out int id)) continue;
                string k = "item." + id;
                if (!s_Objects.TryGetValue(id, out var t) || t == null) { sb.Append(k).Append(".exists=False\n"); continue; }
                var item = t.GetComponent<Item>();
                var p = t.transform.position;
                sb.Append(k).Append(".exists=True\n");
                sb.Append(k).Append(".pos=").Append(p.x.ToString("0.00", inv)).Append(',').Append(p.y.ToString("0.00", inv)).Append(',').Append(p.z.ToString("0.00", inv)).Append('\n');
                sb.Append(k).Append(".active=").Append(t.gameObject.activeInHierarchy).Append('\n');
                sb.Append(k).Append(".held=").Append(t.HeldBy).Append('\n');
                if (item != null)
                {
                    sb.Append(k).Append(".tag=").Append(item.myItem != null ? item.myItem.itemTag : "?").Append('\n');
                    sb.Append(k).Append(".built=").Append(item.builded).Append('\n');
                    sb.Append(k).Append(".saved=").Append(item.mySaver != null).Append('\n');
                }
            }
        }

        /// Test helper: the id of an item lying in the world, preferring one in a save slot (or 0).
        public static int AnyWorldItemId()
        {
            int any = 0;
            foreach (var t in s_Objects.Values)
            {
                if (t == null || t.IsBox || t.HeldBy >= 0 || t.Id <= 0 || !t.gameObject.activeInHierarchy) continue;
                if (t.GetComponent<Item>()?.mySaver != null) return t.Id;
                if (any == 0) any = t.Id;
            }
            return any;
        }

        public static int IdOf(GameObject go)
        {
            var t = go != null ? go.GetComponent<CoopItemId>() : null;
            return t != null ? t.Id : 0;
        }

        public static GameObject Get(int id) => s_Objects.TryGetValue(id, out var t) && t != null ? t.gameObject : null;
    }
}
