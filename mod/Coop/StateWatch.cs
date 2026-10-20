using System;
using System.Collections.Generic;
using System.Reflection;
using ICS2Coop;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ICS2iOS
{
    // Things that change without a call worth mirroring, watched a few times a second on every device; a
    // change made here is sent, a change from elsewhere is applied (last one wins):
    //  * the scene's own animators (doors, the home door, CD trays, toasters, the coffee holder, ovens ...):
    //    their parameters. People, players, UI and the local player's hands are not in it.
    //  * values the game keeps in fields: tip box, cash registers, fridge, coffee jugs, fires, horse bets,
    //    lights, power, shop open, air conditioners, fans, extinguishers, rooms bought, the cafe's smell.
    //  * whether shop shelves and other interactive scene objects are shown.
    // Host only: elevator cars where the host has them (each device runs the elevator from the same button
    // presses; this removes any drift once the car stops), and the dirt already on the floor when a guest joins.
    public class StateWatch : MonoBehaviour
    {
        const float ScanInterval = 0.25f, RebindInterval = 4f;

        static readonly string[] WatchedFields =
        {
            "TipBox.tipboxmoney", "CashRegister.totalMoney", "CashRegisterSecondCafe.totalMoney", "HomeFridge.foodCount",
            "CoffeeJug.coffeML", "Fire.extinguishTimer", "RaceHorse.horseBet", "LightSwitch.lightsOn", "ElectricalSwitch.electric",
            "CivilManager.shopisOpened", "Door.doorLock", "AirConditioner.climateDegree", "AirConditioner.climateteisWorking",
            "AirConditioner.autoMod", "Fan.fanisWorking", "Extinguisher.fuel", "UpgradeItem.buyed", "Oven.cooking",
            "GameManager.globalCafeSmell", "Employee.isRecruit", "Employee.fired",
        };

        sealed class AnimEntry { public Animator Anim; public AnimSync.Layout Layout; public AnimSync.State Known; public int Changes; public float Since; public bool Noisy; }
        sealed class FieldEntry { public Component Owner; public FieldInfo Field; public object Known; public int Changes; public float Since; public bool Noisy; }
        sealed class ActiveEntry { public GameObject Go; public bool Known; }

        static readonly List<AnimEntry> s_Anims = new List<AnimEntry>();
        static readonly List<FieldEntry> s_Fields = new List<FieldEntry>();
        static readonly List<ActiveEntry> s_Actives = new List<ActiveEntry>();
        static readonly Dictionary<Animator, AnimEntry> s_AnimByRef = new Dictionary<Animator, AnimEntry>();
        static readonly Dictionary<GameObject, ActiveEntry> s_ActiveByRef = new Dictionary<GameObject, ActiveEntry>();
        static readonly Dictionary<string, FieldEntry> s_FieldByKey = new Dictionary<string, FieldEntry>();
        static int s_Sent, s_Applied, s_Noisy;

        /// Something that changes all the time by itself (decorations, a jug filling up) is the same on every
        /// device anyway, or not worth the traffic: more than 12 changes in 10 seconds and it is left alone.
        static bool TooNoisy(ref int changes, ref float since, string what)
        {
            float now = Time.unscaledTime;
            if (now - since > 10f) { since = now; changes = 0; }
            if (++changes <= 12) return false;
            s_Noisy++;
            Log.Info("coop: not watching " + what + " (changes all the time)");
            return true;
        }
        float m_NextScan, m_NextBind, m_NextElevator;
        static object[] s_Elevators;

        void OnEnable()
        {
            CoopSession.Received += OnMessage;
            CoopSession.Net.PlayerJoined += OnPlayerJoined;
            SceneManager.sceneLoaded += (s, m) => Clear();
        }

        static void Clear()
        {
            s_Anims.Clear(); s_Fields.Clear(); s_Actives.Clear();
            s_AnimByRef.Clear(); s_ActiveByRef.Clear(); s_FieldByKey.Clear();
            s_Elevators = null;
        }

        static bool Ready => CoopSession.WorldReady && CoopSession.Net.PlayerCount >= 2;

        void Update()
        {
            if (!Ready) return;
            float now = Time.unscaledTime;
            if (now >= m_NextBind) { m_NextBind = now + RebindInterval; Bind(); }
            if (now >= m_NextScan) { m_NextScan = now + ScanInterval; Scan(); }
            if (CoopSession.IsHost && now >= m_NextElevator) { m_NextElevator = now + 0.5f; SendElevators(); }
        }

        // ---- what is watched -------------------------------------------------------------------------------
        static bool Excluded(Transform t)
        {
            var gm = GameManager.Instance;
            if (t.GetComponentInParent<Canvas>() != null) return true;
            if (t.GetComponentInParent<Civil>() != null) return true;
            if (gm.player != null && t.IsChildOf(gm.player)) return true;
            if (gm.playerCamera != null && t.IsChildOf(gm.playerCamera.transform)) return true;
            if (t.root.name.StartsWith("CoopPlayer", StringComparison.Ordinal)) return true;
            return ActorSync.IsActorPart(t);
        }

        static void Bind()
        {
            foreach (var a in FindObjectsOfType<Animator>(true))
            {
                if (s_AnimByRef.ContainsKey(a) || !a.gameObject.scene.IsValid() || a.runtimeAnimatorController == null) continue;
                AnimEntry e = null;
                if (!Excluded(a.transform))
                {
                    e = new AnimEntry { Anim = a, Layout = AnimSync.LayoutOf(a) };
                    if (e.Layout.Params.Length == 0) e = null;
                    else { e.Known = AnimSync.Capture(a, e.Layout); s_Anims.Add(e); }
                }
                s_AnimByRef[a] = e;
            }
            foreach (var spec in WatchedFields)
            {
                int dot = spec.IndexOf('.');
                var type = NetRef.TypeByName(spec.Substring(0, dot));
                var field = type?.GetField(spec.Substring(dot + 1), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field == null) continue;
                foreach (var o in FindObjectsOfType(type, true))
                {
                    var c = o as Component;
                    if (c == null || !c.gameObject.scene.IsValid()) continue;
                    string key = c.GetInstanceID() + "." + field.Name;
                    if (s_FieldByKey.ContainsKey(key)) continue;
                    var e = new FieldEntry { Owner = c, Field = field, Known = field.GetValue(c) };
                    s_FieldByKey[key] = e;
                    s_Fields.Add(e);
                }
            }
            // shop shelves and other things you buy or use up in the scene (not items: ItemSync has them)
            foreach (var info in FindObjectsOfType<ItemInfo>(true))
            {
                var go = info.gameObject;
                if (s_ActiveByRef.ContainsKey(go) || !go.scene.IsValid()) continue;
                ActiveEntry e = null;
                if (go.GetComponentInParent<Item>() == null && go.GetComponentInParent<CargoBox>() == null && !Excluded(go.transform))
                {
                    e = new ActiveEntry { Go = go, Known = go.activeSelf };
                    s_Actives.Add(e);
                }
                s_ActiveByRef[go] = e;
            }
            s_Anims.RemoveAll(e => e.Anim == null);
            s_Fields.RemoveAll(e => e.Owner == null);
            s_Actives.RemoveAll(e => e.Go == null);
        }

        // ---- local changes ---------------------------------------------------------------------------------
        static void Scan()
        {
            foreach (var e in s_Anims)
            {
                if (e.Noisy || e.Anim == null || !e.Anim.isActiveAndEnabled) continue;
                var s = AnimSync.Capture(e.Anim, e.Layout);
                if (!AnimSync.ParamsChanged(e.Known, s)) continue;
                e.Known = s;
                if (TooNoisy(ref e.Changes, ref e.Since, "animator " + e.Anim.name)) { e.Noisy = true; continue; }
                var w = CoopSession.Net.Begin(Msg.WatchAnim);
                NetRef.Put(w, e.Anim);
                AnimSync.Write(w, s);
                Send();
            }
            foreach (var e in s_Fields)
            {
                if (e.Noisy || e.Owner == null) continue;
                var v = e.Field.GetValue(e.Owner);
                if (Same(e.Known, v)) continue;
                e.Known = v;
                if (TooNoisy(ref e.Changes, ref e.Since, e.Owner.GetType().Name + "." + e.Field.Name)) { e.Noisy = true; continue; }
                var w = CoopSession.Net.Begin(Msg.WatchField);
                NetRef.Put(w, e.Owner);
                w.Put(e.Field.Name);
                NetRef.PutValue(w, v);
                Send();
            }
            foreach (var e in s_Actives)
            {
                if (e.Go == null || e.Go.activeSelf == e.Known) continue;
                e.Known = e.Go.activeSelf;
                var w = CoopSession.Net.Begin(Msg.WatchActive);
                NetRef.Put(w, e.Go);
                w.Put(e.Known);
                Send();
            }
        }

        static bool Same(object a, object b)
        {
            if (a is float fa && b is float fb) return Mathf.Abs(fa - fb) <= Mathf.Max(0.05f, Mathf.Abs(fa) * 0.002f);
            return Equals(a, b);
        }

        static void Send()
        {
            CoopSession.Net.SendAll();
            s_Sent++;
        }

        // ---- remote changes ---------------------------------------------------------------------------------
        void OnMessage(int from, Msg id, NetDataReader r)
        {
            if (id == Msg.Dirt) { s_PendingDirt = r.GetRemainingBytes(); return; }   // applied once the cafe is loaded
            if (id != Msg.WatchAnim && id != Msg.WatchField && id != Msg.WatchActive && id != Msg.Elevators) return;
            if (SceneManager.GetActiveScene().name != "MainScene" || GameManager.Instance == null) return;
            byte[] raw = r.GetRemainingBytes();
            if (CoopSession.IsHost && CoopSession.Net.PlayerCount > 2 && id != Msg.Elevators)
            {
                var w = CoopSession.Net.Begin(id);
                w.Put(raw);
                CoopSession.Net.SendAll(DeliveryMethod.ReliableOrdered, from);
            }
            var rr = new NetDataReader(raw);
            try
            {
                switch (id)
                {
                    case Msg.WatchAnim: ApplyAnim(rr); break;
                    case Msg.WatchField: ApplyField(rr); break;
                    case Msg.WatchActive: ApplyActive(rr); break;
                    case Msg.Elevators: ApplyElevators(rr); break;
                }
                s_Applied++;
            }
            catch (Exception e) { Log.Error($"coop: applying {id} failed: {e}"); }
        }

        static void ApplyAnim(NetDataReader r)
        {
            var anim = NetRef.Get(r) as Animator;
            var s = AnimSync.Read(r);
            if (anim == null) return;
            if (!s_AnimByRef.TryGetValue(anim, out var e) || e == null)
            {
                e = new AnimEntry { Anim = anim, Layout = AnimSync.LayoutOf(anim) };
                s_AnimByRef[anim] = e;
                s_Anims.Add(e);
            }
            if (!anim.gameObject.activeInHierarchy) { e.Known = s; return; }
            AnimSync.ApplyParams(anim, e.Layout, s);
            e.Known = AnimSync.Capture(anim, e.Layout);
        }

        static void ApplyField(NetDataReader r)
        {
            var owner = NetRef.Get(r) as Component;
            string name = r.GetString();
            var v = NetRef.GetValue(r);
            if (owner == null) return;
            var field = owner.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null) return;
            var value = NetRef.As(v, field.FieldType);
            field.SetValue(owner, value);
            string key = owner.GetInstanceID() + "." + name;
            if (s_FieldByKey.TryGetValue(key, out var e)) e.Known = value;
            else { e = new FieldEntry { Owner = owner, Field = field, Known = value }; s_FieldByKey[key] = e; s_Fields.Add(e); }
        }

        static void ApplyActive(NetDataReader r)
        {
            var go = NetRef.Get(r) as GameObject;
            bool on = r.GetBool();
            if (go == null) return;
            if (go.activeSelf != on) go.SetActive(on);
            if (s_ActiveByRef.TryGetValue(go, out var e) && e != null) e.Known = on;
        }

        // ---- elevators (host -> guests) -------------------------------------------------------------------------
        static object[] Elevators()
        {
            if (s_Elevators != null) return s_Elevators;
            var type = NetRef.TypeByName("OldElevator.ElevatorBrain");
            if (type == null) return s_Elevators = new object[0];
            var list = new List<object>(FindObjectsOfType(type, true));
            list.Sort((a, b) => string.CompareOrdinal(NetRef.PathOf(((Component)a).transform), NetRef.PathOf(((Component)b).transform)));
            return s_Elevators = list.ToArray();
        }

        static Transform Car(object brain) => brain.GetType().GetField("elevator")?.GetValue(brain) as Transform;
        static bool Moving(object brain) => brain.GetType().GetField("moving")?.GetValue(brain) is bool b && b;

        static void SendElevators()
        {
            var all = Elevators();
            if (all.Length == 0) return;
            var w = CoopSession.Net.Begin(Msg.Elevators);
            w.Put((byte)all.Length);
            foreach (var b in all)
            {
                var car = Car(b);
                var p = car != null ? car.position : Vector3.zero;
                w.Put(p.x); w.Put(p.y); w.Put(p.z);
                w.Put(Moving(b));
            }
            CoopSession.Net.SendAll(DeliveryMethod.Unreliable);
        }

        static void ApplyElevators(NetDataReader r)
        {
            var all = Elevators();
            int n = r.GetByte();
            for (int i = 0; i < n; i++)
            {
                var p = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                bool moving = r.GetBool();
                if (i >= all.Length) continue;
                var car = Car(all[i]);
                if (car == null || moving || Moving(all[i])) continue;
                var d = p - car.position;
                if (d.sqrMagnitude < 0.0004f) continue;
                // a stopped car somewhere else than on the host: put it there (and whoever stands in it)
                var gm = GameManager.Instance;
                if (gm.player != null && (gm.player.position - car.position).sqrMagnitude < 4f && Mathf.Abs(d.y) < 6f)
                {
                    var cc = gm.player.GetComponent<CharacterController>();
                    if (cc != null) cc.enabled = false;
                    gm.player.position += d;
                    if (cc != null) cc.enabled = true;
                }
                car.position = p;
            }
        }

        // ---- dirt already on the floor when a guest joins (host -> guest) ----------------------------------------
        static FieldInfo SpawnedCount => typeof(FootstepsManager).GetField("spawnedCount", BindingFlags.NonPublic | BindingFlags.Instance);

        void OnPlayerJoined(int player, string name)
        {
            if (!CoopSession.IsHost || GameManager.Instance == null) return;
            var fm = FindObjectOfType<FootstepsManager>();
            if (fm == null) return;
            var w = CoopSession.Net.Begin(Msg.Dirt);
            w.Put((int)SpawnedCount.GetValue(fm));
            var areas = fm.footprintsSpawnAreas;
            int countPos = w.Length;
            w.Put(0);
            int n = 0;
            for (int a = 0; a < areas.Length; a++)
            {
                foreach (var go in areas[a].SpawnedDirtyObjects)
                {
                    if (go == null) continue;
                    int kind = -1, index = -1;
                    Prefab(fm, go.name, ref kind, ref index);
                    if (kind < 0) continue;
                    w.Put((byte)a); w.Put((byte)kind); w.Put((byte)index); w.Put(go.name);
                    var t = go.transform;
                    w.Put(t.position.x); w.Put(t.position.y); w.Put(t.position.z);
                    w.Put(t.rotation.x); w.Put(t.rotation.y); w.Put(t.rotation.z); w.Put(t.rotation.w);
                    var sr = go.GetComponent<SpriteRenderer>();
                    var c = sr != null ? sr.color : Color.white;
                    w.Put(c.r); w.Put(c.g); w.Put(c.b); w.Put(c.a);
                    n++;
                }
            }
            var d = w.Data;
            d[countPos] = (byte)n; d[countPos + 1] = (byte)(n >> 8); d[countPos + 2] = (byte)(n >> 16); d[countPos + 3] = (byte)(n >> 24);
            CoopSession.Net.SendTo(player);
            Log.Info($"coop: {n} bits of dirt sent to player {player}");
        }

        /// kind 0 footprints, 1 trash, 2 poo; by the prefab's name at the start of the object's name.
        static void Prefab(FootstepsManager fm, string name, ref int kind, ref int index)
        {
            int best = 0;
            var lists = new[] { fm.footstepsprefabs, fm.trash, new[] { fm.poo } };
            for (int k = 0; k < lists.Length; k++)
                for (int i = 0; i < lists[k].Length; i++)
                {
                    var prefab = lists[k][i];
                    if (prefab != null && name.StartsWith(prefab.name + "(Clone)", StringComparison.Ordinal) && prefab.name.Length > best)
                    { best = prefab.name.Length; kind = k; index = i; }
                }
        }

        static byte[] s_PendingDirt;

        static void ApplyDirt(NetDataReader r)
        {
            var fm = FindObjectOfType<FootstepsManager>();
            if (fm == null) return;
            int count = r.GetInt();
            int n = r.GetInt();
            var areas = fm.footprintsSpawnAreas;
            // the guest's own dirt from loading goes; the host's is the cafe's
            foreach (var area in areas)
            {
                foreach (var go in area.SpawnedDirtyObjects) if (go != null) Destroy(go);
                area.SpawnedDirtyObjects.Clear();
            }
            for (int i = 0; i < n; i++)
            {
                int a = r.GetByte(), kind = r.GetByte(), index = r.GetByte();
                string name = r.GetString();
                var p = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                var q = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
                var c = new Color(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
                var prefab = kind == 0 ? fm.footstepsprefabs[index] : kind == 1 ? fm.trash[index] : fm.poo;
                if (prefab == null || a >= areas.Length) continue;
                var go = Instantiate(prefab, p, q);
                go.name = name;
                var sr = go.GetComponent<SpriteRenderer>();
                if (sr != null) sr.color = c;
                var trash = go.GetComponent<Trash>();
                if (trash != null) trash.myArea = areas[a];
                areas[a].SpawnedDirtyObjects.Add(go);
            }
            SpawnedCount.SetValue(fm, count);
            Log.Info($"coop: {n} bits of dirt from the host");
        }

        void LateUpdate()
        {
            if (s_PendingDirt != null && ItemSync.Ready && GameManager.Instance != null)
            {
                var raw = s_PendingDirt;
                s_PendingDirt = null;
                try { ApplyDirt(new NetDataReader(raw)); } catch (Exception e) { Log.Error("coop: dirt: " + e); }
            }
        }

        // ---- tests ------------------------------------------------------------------------------------------------
        public static void Report(System.Text.StringBuilder sb)
        {
            sb.Append("watch.anims=").Append(s_Anims.Count).Append('\n');
            sb.Append("watch.fields=").Append(s_Fields.Count).Append('\n');
            sb.Append("watch.actives=").Append(s_Actives.Count).Append('\n');
            sb.Append("watch.sent=").Append(s_Sent).Append('\n');
            sb.Append("watch.noisy=").Append(s_Noisy).Append('\n');
            sb.Append("watch.applied=").Append(s_Applied).Append('\n');
            var fm = GameManager.Instance != null ? FindObjectOfType<FootstepsManager>() : null;
            int dirt = 0;
            if (fm != null) foreach (var a in fm.footprintsSpawnAreas) foreach (var go in a.SpawnedDirtyObjects) if (go != null) dirt++;
            sb.Append("world.dirt=").Append(dirt).Append('\n');
            var ls = GameManager.Instance != null ? GameManager.Instance.lightSwitch : null;
            if (ls != null) sb.Append("world.lights=").Append(ls.lightsOn).Append('\n');
            if (GameManager.Instance != null && GameManager.Instance.electricalSwitch != null)
                sb.Append("world.power=").Append(GameManager.Instance.electricalSwitch.electric).Append('\n');
            int i = 0;
            foreach (var b in Elevators())
            {
                var car = Car(b);
                if (car != null) sb.Append("elevator.").Append(i).Append(".y=").Append(car.position.y.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                i++;
            }
        }
    }
}
