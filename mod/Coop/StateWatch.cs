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
    //    lights, power, shop open, air conditioners, fans, extinguishers, rooms bought, the cafe's smell,
    //    crypto prices, the family's money / mood / jobs, unspent skill points.
    //  * whether shop shelves and other interactive scene objects are shown.
    // Host only: elevator cars (only the host's travel; a guest's cars follow them, see below), and the dirt
    // already on the floor when a guest joins.
    public class StateWatch : MonoBehaviour
    {
        // Bind searches the whole scene (a hitch): everything in it is found the first time, later rounds only pick
        // up what was built or bought since
        const float ScanInterval = 0.25f, RebindInterval = 15f;

        static readonly string[] WatchedFields =
        {
            "TipBox.tipboxmoney", "CashRegister.totalMoney", "CashRegisterSecondCafe.totalMoney", "HomeFridge.foodCount",
            "CoffeeJug.coffeML", "Fire.extinguishTimer", "RaceHorse.horseBet", "LightSwitch.lightsOn", "ElectricalSwitch.electric",
            "CivilManager.shopisOpened", "Door.doorLock", "AirConditioner.climateDegree", "AirConditioner.climateteisWorking",
            "AirConditioner.autoMod", "Fan.fanisWorking", "Extinguisher.fuel", "UpgradeItem.buyed", "Oven.cooking",
            "GameManager.globalCafeSmell", "Employee.isRecruit", "Employee.fired",
            // crypto prices (the host rolls them each day), the family's money / mood / jobs, skill points
            "BuyCoinn.price", "BuyCoinn.change", "Wife.totalMoney", "Wife.morale", "Wife.work", "Wife.pregnant",
            "Boy.totalMoney", "Boy.morale", "Boy.work", "GameManager.totalunusedabilitypoint",
        };

        sealed class AnimEntry { public Animator Anim; public AnimSync.Layout Layout; public AnimSync.State Known, Pending; public int Changes; public float Since, QuietUntil; public bool Noisy; }
        sealed class FieldEntry { public Component Owner; public FieldInfo Field; public object Known; public int Changes; public float Since, QuietUntil; public bool Noisy; }
        sealed class ActiveEntry { public GameObject Go; public bool Known; }

        static readonly List<AnimEntry> s_Anims = new List<AnimEntry>();
        static readonly List<FieldEntry> s_Fields = new List<FieldEntry>();
        static readonly List<ActiveEntry> s_Actives = new List<ActiveEntry>();
        static readonly Dictionary<Animator, AnimEntry> s_AnimByRef = new Dictionary<Animator, AnimEntry>();
        static readonly Dictionary<GameObject, ActiveEntry> s_ActiveByRef = new Dictionary<GameObject, ActiveEntry>();
        static readonly Dictionary<string, FieldEntry> s_FieldByKey = new Dictionary<string, FieldEntry>();
        static int s_Sent, s_Applied, s_Noisy;

        /// Something that changes all the time by itself (decorations, a jug filling up) is the same on every
        /// device anyway, or not worth the traffic: more than 12 changes in 10 seconds and it is left alone for
        /// 10 seconds (then its value then is sent, so a fire extinguisher's fuel still ends up the same).
        static bool TooNoisy(ref int changes, ref float since, ref float quietUntil, ref bool noisy, string what)
        {
            float now = Time.unscaledTime;
            if (now - since > 10f) { since = now; changes = 0; }
            if (++changes <= 12) return false;
            if (!noisy) { s_Noisy++; Log.Info("coop: watching " + what + " less often (changes all the time)"); }
            noisy = true;
            quietUntil = now + 10f;
            since = quietUntil; changes = 0;
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
            s_Follow.Clear();
        }

        static bool Ready => CoopSession.WorldReady && CoopSession.Net.PlayerCount >= 2;

        void Update()
        {
            if (!Ready) return;
            float now = Time.unscaledTime;
            if (now >= m_NextBind) { m_NextBind = now + RebindInterval; Bind(); }
            if (now >= m_NextScan) { m_NextScan = now + ScanInterval; Scan(); }
            UpdateElevators(now);
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
                if (e.Anim == null || !e.Anim.isActiveAndEnabled) continue;
                if (e.Pending != null)
                {
                    // the other device's state, which came while this one was switched off
                    AnimSync.ApplyParams(e.Anim, e.Layout, e.Pending);
                    e.Pending = null;
                    e.Known = AnimSync.Capture(e.Anim, e.Layout);
                    continue;
                }
                if (Time.unscaledTime < e.QuietUntil) continue;
                var s = AnimSync.Capture(e.Anim, e.Layout);
                if (!AnimSync.ParamsChanged(e.Known, s)) continue;
                e.Known = s;
                // an animator that keeps changing is a decoration doing its own thing on every device: left alone
                if (TooNoisy(ref e.Changes, ref e.Since, ref e.QuietUntil, ref e.Noisy, "animator " + e.Anim.name)) { e.QuietUntil = float.MaxValue; continue; }
                var w = CoopSession.Net.Begin(Msg.WatchAnim);
                NetRef.Put(w, e.Anim);
                AnimSync.Write(w, s);
                Send();
            }
            foreach (var e in s_Fields)
            {
                if (e.Owner == null || Time.unscaledTime < e.QuietUntil) continue;
                var v = e.Field.GetValue(e.Owner);
                if (Same(e.Known, v)) continue;
                e.Known = v;
                if (TooNoisy(ref e.Changes, ref e.Since, ref e.QuietUntil, ref e.Noisy, e.Owner.GetType().Name + "." + e.Field.Name)) continue;
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
            if (!anim.gameObject.activeInHierarchy) { e.Known = s; e.Pending = s; return; }
            e.Pending = null;
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
        // Only the host's elevators travel (a guest's GoToPosition / EmergencyStopBegin are dropped, CallSync
        // HostAlways): the host sends each car's position 20 times a second while one moves, and a guest's car
        // follows it smoothly, carrying the guest's body when it stands in it. The guest's ElevatorBrain is kept
        // "moving" at zero speed meanwhile, so its doors, buttons and lights behave as during a trip.
        sealed class CarFollow
        {
            public Vector3 Pos, Vel;      // host's last position and speed
            public float At;              // when it arrived (local unscaled time)
            public bool Moving;
            public int Dir;
        }

        static readonly Dictionary<int, CarFollow> s_Follow = new Dictionary<int, CarFollow>();
        static float s_LastMoving = -10f;
        static FieldInfo s_Dir, s_Speed, s_DistToFloor;
        static MethodInfo s_DoorLocks, s_CallerLights;

        static float s_ElevatorsRetry;

        static object[] Elevators()
        {
            // found again when a car was destroyed, or a few seconds later when none was there yet
            if (s_Elevators != null && s_Elevators.Length > 0 && (Component)s_Elevators[0] == null) s_Elevators = null;
            if (s_Elevators != null && s_Elevators.Length == 0 && Time.unscaledTime >= s_ElevatorsRetry) s_Elevators = null;
            if (s_Elevators != null) return s_Elevators;
            s_ElevatorsRetry = Time.unscaledTime + 3f;
            var type = NetRef.TypeByName("OldElevator.ElevatorBrain");
            if (type == null) return s_Elevators = new object[0];
            var list = new List<object>(FindObjectsOfType(type, true));
            list.Sort((a, b) => string.CompareOrdinal(NetRef.PathOf(((Component)a).transform), NetRef.PathOf(((Component)b).transform)));
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            s_Dir = type.GetField("direction", all);
            s_Speed = type.GetField("currentSpeed", all);
            s_DistToFloor = type.GetField("distToFloor", all);
            s_DoorLocks = type.GetMethod("UpdateDoorLocks", all);
            s_CallerLights = type.GetMethod("UpdateCallersLights", all);
            return s_Elevators = list.ToArray();
        }

        static Transform Car(object brain) => brain.GetType().GetField("elevator")?.GetValue(brain) as Transform;
        static bool Moving(object brain) => brain.GetType().GetField("moving")?.GetValue(brain) is bool b && b;

        /// The elevator car whose floor is under this body (index into Elevators()), or -1.
        public static int CarUnder(Vector3 bodyPos, out Transform car)
        {
            car = null;
            var all = Elevators();
            if (all.Length == 0) return -1;
            foreach (var hit in Physics.RaycastAll(bodyPos, Vector3.down, 2.5f, ~0, QueryTriggerInteraction.Ignore))
                for (int i = 0; i < all.Length; i++)
                {
                    var c = Car(all[i]);
                    if (c != null && hit.transform.IsChildOf(c)) { car = c; return i; }
                }
            return -1;
        }

        public static Transform CarAt(int index)
        {
            var all = Elevators();
            return index >= 0 && index < all.Length ? Car(all[index]) : null;
        }

        void UpdateElevators(float now)
        {
            if (CoopSession.IsHost)
            {
                bool anyMoving = false;
                foreach (var b in Elevators()) if (Moving(b)) { anyMoving = true; break; }
                if (anyMoving) s_LastMoving = now;
                // fast while a car moves (and a moment after, so the stop arrives), slow otherwise
                float interval = now - s_LastMoving < 1f ? 0.05f : 0.5f;
                if (now >= m_NextElevator) { m_NextElevator = now + interval; SendElevators(); }
            }
            else FollowElevators(now);
        }

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
                w.Put((sbyte)(s_Dir != null ? (int)s_Dir.GetValue(b) : 0));
            }
            CoopSession.Net.SendAll(DeliveryMethod.Sequenced);
        }

        static void ApplyElevators(NetDataReader r)
        {
            var all = Elevators();
            int n = r.GetByte();
            float now = Time.unscaledTime;
            for (int i = 0; i < n; i++)
            {
                var p = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                bool moving = r.GetBool();
                int dir = r.GetSByte();
                if (i >= all.Length) continue;
                bool first = !s_Follow.TryGetValue(i, out var f);
                if (first)
                {
                    f = new CarFollow { Pos = p, At = now };
                    s_Follow[i] = f;
                }
                float dt = now - f.At;
                f.Vel = moving && f.Moving && dt > 0.01f && dt < 0.5f ? (p - f.Pos) / dt : Vector3.zero;
                f.Pos = p;
                f.At = now;
                // a stopped car is where the host's is before its doors unlock (they only open on its floor)
                if (!moving) PlaceCar(all[i], p);
                if (first && !moving)
                {
                    try { s_DoorLocks?.Invoke(all[i], new object[] { true }); s_CallerLights?.Invoke(all[i], null); }
                    catch (Exception e) { Log.Warn("coop: elevator locks: " + e.Message); }
                }
                if (moving != f.Moving) TripChanged(all[i], moving, dir, p);
                f.Moving = moving;
                f.Dir = dir;
            }
        }

        /// Guest: the host's car started or stopped: the same sounds, lights and door locks here.
        static void TripChanged(object brain, bool moving, int dir, Vector3 pos)
        {
            var type = brain.GetType();
            try
            {
                type.GetField("moving")?.SetValue(brain, moving);
                s_Dir?.SetValue(brain, dir);
                if (moving) (type.GetField("travelSound")?.GetValue(brain) as AudioSource)?.Play();
                else
                {
                    (type.GetField("travelSound")?.GetValue(brain) as AudioSource)?.Stop();
                    (type.GetField("breaksSound")?.GetValue(brain) as AudioSource)?.Stop();
                    (type.GetField("stopSound")?.GetValue(brain) as AudioSource)?.Play();
                    type.GetField("destination")?.SetValue(brain, pos);   // arrived: not "away from its destination"
                    s_DoorLocks?.Invoke(brain, new object[] { false });
                }
                s_CallerLights?.Invoke(brain, null);
            }
            catch (Exception e) { Log.Warn("coop: elevator " + ((Component)brain).name + ": " + e.Message); }
        }

        /// Guest, every frame: each car goes where the host has it (predicted a little ahead while moving), and
        /// takes the player standing in it along.
        static void FollowElevators(float now)
        {
            var all = Elevators();
            foreach (var kv in s_Follow)
            {
                if (kv.Key >= all.Length) continue;
                var brain = all[kv.Key];
                var car = Car(brain);
                if (car == null) continue;
                var f = kv.Value;
                if (f.Moving)
                {
                    // the brain stays "moving" but does not move the car itself
                    var type = brain.GetType();
                    type.GetField("moving")?.SetValue(brain, true);
                    s_Speed?.SetValue(brain, 0f);
                    s_Dir?.SetValue(brain, f.Dir);
                    type.GetField("destination")?.SetValue(brain, new Vector3(car.position.x, f.Dir >= 0 ? 1e6f : -1e6f, car.position.z));
                }
                PlaceCar(brain, f.Pos + f.Vel * Mathf.Min(now - f.At, 0.15f));
            }
        }

        /// Guest: the car to this position, with the local player if they stand in it.
        static void PlaceCar(object brain, Vector3 target)
        {
            var car = Car(brain);
            if (car == null) return;
            var d = target - car.position;
            if (d.sqrMagnitude < 1e-6f) return;
            var gm = GameManager.Instance;
            // far away (joining): just put it there
            if (d.sqrMagnitude <= 36f && gm != null && gm.player != null && Standing(brain, gm.player, car))
            {
                gm.player.position += d;
                car.position = target;
                Physics.SyncTransforms();     // the character controller must see where it is now
                return;
            }
            car.position = target;
        }

        static bool Standing(object brain, Transform player, Transform car)
        {
            float reach = s_DistToFloor != null ? (float)s_DistToFloor.GetValue(brain) + 0.6f : 1.6f;
            foreach (var hit in Physics.RaycastAll(player.position, Vector3.down, reach, ~0, QueryTriggerInteraction.Ignore))
                if (hit.transform.IsChildOf(car)) return true;
            return false;
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
