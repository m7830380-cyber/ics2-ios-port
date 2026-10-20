using System.Collections.Generic;
using System.Text;
using ICS2Coop;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace ICS2iOS
{
    // Every other moving person and animal of the town (customers have CustomerSync): the robber, the police,
    // the beggar and the hobo, staff (bouncer, chef, maid, mascot, cleaning robot), strippers, the delivery
    // camel, cats and dogs, the bomber and the soldier, race horses, pirate-game customers and the family.
    // All are placed in the scene (none is spawned), so each is named by its scene path.
    //
    // The host runs them. A guest switches their scripts and navigation off and shows them as the host has
    // them: on / off, position, rotation, every Animator's parameters and state (AnimSync), the props they
    // show (cardboard box, food, ...), and ragdolls (knocked down: physics on).
    public class ActorSync : MonoBehaviour
    {
        const float SendInterval = 0.1f, FullInterval = 1f;
        const int ChunkBytes = 900;

        /// Script types whose objects are actors (their script is the AI the guest switches off).
        static readonly string[] ActorTypes =
        {
            "Robber", "Policeman", "Beggar", "Hobo", "Bouncer", "BunnyGirl", "Cat", "Dog", "Camel", "Chef", "Maid",
            "Mascot", "VacuumCleaner", "Bomberman", "Soldier", "PirateGamer", "RaceHorse",
        };

        sealed class Actor
        {
            public string Path;
            public GameObject Go;
            public Behaviour Script;
            public NavMeshAgent[] Agents;
            public Animator[] Anims;
            public AnimSync.Layout[] Layouts;
            public GameObject[] Props;
            public Rigidbody[] Bones;
            public Collider[] BoneColliders;
            public State Last, Target;
            public bool GuestRagdoll;
        }

        sealed class State
        {
            public bool Active, Ragdoll;
            public Vector3 Pos;
            public Quaternion Rot;
            public uint Props;
            public AnimSync.State[] Anims;
        }

        static readonly List<Actor> s_Actors = new List<Actor>();
        static bool s_Bound, s_GuestSetup, s_TableReceived;
        static string[] s_PendingTable;
        float m_NextSend, m_NextFull;

        void OnEnable()
        {
            CoopSession.Received += OnMessage;
            CoopSession.Net.PlayerJoined += OnPlayerJoined;
            SceneManager.sceneLoaded += (s, m) => { s_Actors.Clear(); s_Bound = false; s_GuestSetup = false; s_TableReceived = false; };
            CoopSession.Net.Disconnected += _ => RestoreGuest();
        }

        static System.Type[] s_Types;

        /// Part of a person or animal this module (or the family list) runs: other watchers leave it alone.
        public static bool IsActorPart(Transform t)
        {
            if (s_Types == null)
            {
                var list = new List<System.Type>();
                foreach (var n in ActorTypes) { var ty = NetRef.TypeByName(n); if (ty != null) list.Add(ty); }
                s_Types = list.ToArray();
            }
            var fm = GameManager.Instance != null ? GameManager.Instance.familyManager : null;
            for (var p = t; p != null; p = p.parent)
            {
                foreach (var ty in s_Types) if (p.GetComponent(ty) != null) return true;
                if (fm != null && (p.gameObject == fm.wife || p.gameObject == fm.danceWife || p.gameObject == fm.boy ||
                                   p.gameObject == fm.beggarBoy || p.gameObject == fm.babyBoy)) return true;
            }
            return false;
        }

        // ---- the list --------------------------------------------------------------------------------------
        /// Host: all actors of the scene, in a fixed order.
        static void BindHost()
        {
            if (s_Bound || GameManager.Instance == null) return;
            s_Actors.Clear();
            var seen = new HashSet<GameObject>();
            foreach (var typeName in ActorTypes)
            {
                var type = NetRef.TypeByName(typeName);
                if (type == null) { Log.Warn("coop: actor type missing: " + typeName); continue; }
                var found = new List<Component>();
                foreach (var o in FindObjectsOfType(type, true)) if (o is Component c && c.gameObject.scene.IsValid()) found.Add(c);
                found.Sort((a, b) => string.CompareOrdinal(NetRef.PathOf(a.transform), NetRef.PathOf(b.transform)));
                foreach (var c in found) if (seen.Add(c.gameObject)) s_Actors.Add(Make(c.gameObject, c as Behaviour));
            }
            var fm = GameManager.Instance.familyManager;
            if (fm != null)
                foreach (var go in new[] { fm.wife, fm.danceWife, fm.boy, fm.beggarBoy, fm.babyBoy })
                    if (go != null && seen.Add(go)) s_Actors.Add(Make(go, null));
            s_Bound = true;
            Log.Info($"coop: {s_Actors.Count} town people and animals are shared");
        }

        static Actor Make(GameObject go, Behaviour script)
        {
            var a = new Actor { Go = go, Script = script, Path = NetRef.PathOf(go.transform) };
            a.Agents = go.GetComponentsInChildren<NavMeshAgent>(true);
            a.Anims = go.GetComponentsInChildren<Animator>(true);
            a.Layouts = new AnimSync.Layout[a.Anims.Length];
            for (int i = 0; i < a.Anims.Length; i++) a.Layouts[i] = AnimSync.LayoutOf(a.Anims[i]);
            var props = new List<GameObject>();
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                if (r.gameObject != go && !(r is SkinnedMeshRenderer) && !props.Contains(r.gameObject) && props.Count < 32) props.Add(r.gameObject);
            a.Props = props.ToArray();
            var bones = new List<Rigidbody>();
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) if (rb.gameObject != go) bones.Add(rb);
            a.Bones = bones.ToArray();
            var cols = new List<Collider>();
            foreach (var rb in a.Bones) cols.AddRange(rb.GetComponents<Collider>());
            a.BoneColliders = cols.ToArray();
            return a;
        }

        /// Host -> joining guest: the actors' scene paths, in the order the states use.
        void OnPlayerJoined(int player, string name)
        {
            if (!CoopSession.IsHost || SceneManager.GetActiveScene().name != "MainScene") return;
            BindHost();
            var w = CoopSession.Net.Begin(Msg.ActorTable);
            w.Put(s_Actors.Count);
            foreach (var a in s_Actors) w.Put(a.Path);
            CoopSession.Net.SendTo(player);
        }

        static void BindGuest(string[] paths)
        {
            s_Actors.Clear();
            int missing = 0;
            foreach (var path in paths)
            {
                var t = NetRef.Find(path);
                if (t == null) { missing++; s_Actors.Add(null); continue; }
                Behaviour script = null;
                foreach (var typeName in ActorTypes)
                {
                    var type = NetRef.TypeByName(typeName);
                    if (type != null && t.GetComponent(type) is Behaviour b) { script = b; break; }
                }
                s_Actors.Add(Make(t.gameObject, script));
            }
            s_Bound = true;
            Log.Info($"coop: following {paths.Length - missing} town people and animals of the host" + (missing > 0 ? $" ({missing} not found here)" : ""));
        }

        static void SetupGuest()
        {
            if (s_GuestSetup) return;
            foreach (var a in s_Actors)
            {
                if (a == null) continue;
                if (a.Script != null)
                {
                    a.Script.enabled = false;
                    // coroutines and Invokes keep running on a switched-off script
                    if (a.Script is MonoBehaviour mb) { mb.StopAllCoroutines(); mb.CancelInvoke(); }
                }
                foreach (var ag in a.Agents) if (ag != null) ag.enabled = false;
                foreach (var an in a.Anims) if (an != null) an.applyRootMotion = false;
            }
            s_GuestSetup = true;
        }

        /// Leaving: the guest's game goes to the main menu anyway; nothing to restore beyond safety.
        static void RestoreGuest() { s_GuestSetup = false; }

        // ---- host -----------------------------------------------------------------------------------------
        void Update()
        {
            if (!CoopSession.Active || SceneManager.GetActiveScene().name != "MainScene") return;
            if (CoopSession.IsHost)
            {
                if (CoopSession.Net.PlayerCount < 2) return;
                BindHost();
                float now = Time.unscaledTime;
                if (now < m_NextSend) return;
                bool full = now >= m_NextFull;
                if (full) m_NextFull = now + FullInterval;
                m_NextSend = now + SendInterval;
                Send(full);
            }
            else
            {
                if (!s_Bound && s_PendingTable != null && GameManager.Instance != null && ItemSync.Ready) { BindGuest(s_PendingTable); s_PendingTable = null; }
                if (s_Bound) { SetupGuest(); Smooth(); }
            }
        }

        static State Capture(Actor a)
        {
            var s = new State { Active = a.Go.activeInHierarchy, Pos = a.Go.transform.position, Rot = a.Go.transform.rotation };
            var main = a.Anims.Length > 0 ? a.Anims[0] : null;
            s.Ragdoll = s.Active && main != null && !main.enabled && a.Bones.Length > 0 && !a.Bones[0].isKinematic;
            for (int i = 0; i < a.Props.Length; i++) if (a.Props[i] != null && a.Props[i].activeSelf) s.Props |= 1u << i;
            s.Anims = new AnimSync.State[a.Anims.Length];
            for (int i = 0; i < a.Anims.Length; i++) s.Anims[i] = AnimSync.Capture(a.Anims[i], a.Layouts[i]);
            return s;
        }

        static bool Changed(State a, State b)
        {
            if (a == null || a.Active != b.Active || a.Ragdoll != b.Ragdoll) return true;
            if (!b.Active) return false;
            if ((a.Pos - b.Pos).sqrMagnitude > 0.0004f || Quaternion.Angle(a.Rot, b.Rot) > 1f || a.Props != b.Props) return true;
            for (int i = 0; i < a.Anims.Length && i < b.Anims.Length; i++) if (AnimSync.Changed(a.Anims[i], b.Anims[i])) return true;
            return false;
        }

        static void Send(bool full)
        {
            NetDataWriter w = null;
            int count = 0, countPos = 0;
            for (int i = 0; i < s_Actors.Count; i++)
            {
                var a = s_Actors[i];
                if (a == null || a.Go == null) continue;
                var s = Capture(a);
                if (!full && !Changed(a.Last, s)) continue;
                a.Last = s;
                if (w == null) { w = CoopSession.Net.Begin(Msg.ActorStates); countPos = w.Length; w.Put((short)0); count = 0; }
                w.Put((short)i);
                w.Put((byte)((s.Active ? 1 : 0) | (s.Ragdoll ? 2 : 0)));
                if (s.Active)
                {
                    w.Put(s.Pos.x); w.Put(s.Pos.y); w.Put(s.Pos.z);
                    w.Put(s.Rot.x); w.Put(s.Rot.y); w.Put(s.Rot.z); w.Put(s.Rot.w);
                    w.Put(s.Props);
                    w.Put((byte)s.Anims.Length);
                    foreach (var an in s.Anims) AnimSync.Write(w, an);
                }
                count++;
                if (w.Length > ChunkBytes) { Flush(w, countPos, count); w = null; }
            }
            if (w != null) Flush(w, countPos, count);
        }

        static void Flush(NetDataWriter w, int countPos, int count)
        {
            w.Data[countPos] = (byte)(count & 0xff);
            w.Data[countPos + 1] = (byte)(count >> 8);
            CoopSession.Net.SendAll(DeliveryMethod.Unreliable);
        }

        // ---- guest ----------------------------------------------------------------------------------------
        static void Apply(NetDataReader r)
        {
            if (!s_Bound) return;
            SetupGuest();
            int n = r.GetShort();
            for (int e = 0; e < n; e++)
            {
                int i = r.GetShort();
                byte flags = r.GetByte();
                var a = i >= 0 && i < s_Actors.Count ? s_Actors[i] : null;
                var s = new State { Active = (flags & 1) != 0, Ragdoll = (flags & 2) != 0 };
                if (s.Active)
                {
                    s.Pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                    s.Rot = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
                    s.Props = r.GetUInt();
                    s.Anims = new AnimSync.State[r.GetByte()];
                    for (int k = 0; k < s.Anims.Length; k++) s.Anims[k] = AnimSync.Read(r);
                }
                if (a == null || a.Go == null) continue;
                if (!s.Active)
                {
                    if (a.Go.activeSelf) a.Go.SetActive(false);
                    a.Target = null;
                    continue;
                }
                bool wasActive = a.Go.activeSelf;
                if (!wasActive) a.Go.SetActive(true);
                // switching an object on runs its OnEnable (camel: warps to a random spot): we decide where it is
                if (a.Script != null && a.Script.enabled)
                {
                    a.Script.enabled = false;
                    if (a.Script is MonoBehaviour mb) { mb.StopAllCoroutines(); mb.CancelInvoke(); }
                }
                foreach (var ag in a.Agents) if (ag != null && ag.enabled) ag.enabled = false;
                if (!wasActive || a.Target == null || (a.Go.transform.position - s.Pos).sqrMagnitude > 9f)
                    a.Go.transform.SetPositionAndRotation(s.Pos, s.Rot);
                a.Target = s;
                for (int k = 0; k < a.Props.Length; k++)
                {
                    bool on = (s.Props & (1u << k)) != 0;
                    if (a.Props[k] != null && a.Props[k].activeSelf != on) a.Props[k].SetActive(on);
                }
                SetRagdoll(a, s.Ragdoll);
                if (!s.Ragdoll)
                    for (int k = 0; k < a.Anims.Length && k < s.Anims.Length; k++) AnimSync.Apply(a.Anims[k], a.Layouts[k], s.Anims[k]);
            }
        }

        /// Knocked down on the host: the body falls here too (by our own physics; where exactly does not matter).
        static void SetRagdoll(Actor a, bool on)
        {
            if (a.GuestRagdoll == on || a.Bones.Length == 0) return;
            a.GuestRagdoll = on;
            if (a.Anims.Length > 0 && a.Anims[0] != null) a.Anims[0].enabled = !on;
            foreach (var rb in a.Bones) if (rb != null) { rb.isKinematic = !on; if (on) rb.velocity = Vector3.zero; }
            foreach (var c in a.BoneColliders) if (c != null) c.enabled = on;
        }

        static void Smooth()
        {
            float k = 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime);
            foreach (var a in s_Actors)
            {
                if (a == null || a.Target == null || a.Go == null || !a.Go.activeSelf || a.GuestRagdoll) continue;
                var t = a.Go.transform;
                t.SetPositionAndRotation(Vector3.Lerp(t.position, a.Target.Pos, k), Quaternion.Slerp(t.rotation, a.Target.Rot, k));
            }
        }

        void OnMessage(int from, Msg id, NetDataReader r)
        {
            if (CoopSession.IsHost) return;
            if (id == Msg.ActorTable)
            {
                var paths = new string[r.GetInt()];
                for (int i = 0; i < paths.Length; i++) paths[i] = r.GetString();
                s_PendingTable = paths;
                s_Bound = false;
                s_GuestSetup = false;
                s_TableReceived = true;
            }
            else if (id == Msg.ActorStates && SceneManager.GetActiveScene().name == "MainScene") Apply(r);
        }

        // ---- tests ----------------------------------------------------------------------------------------
        public static void Report(StringBuilder sb, string detail)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            sb.Append("actors.count=").Append(s_Bound ? s_Actors.Count : -1).Append('\n');
            sb.Append("actors.table=").Append(s_TableReceived).Append('\n');
            if (!s_Bound) return;
            for (int i = 0; i < s_Actors.Count; i++)
            {
                var a = s_Actors[i];
                if (a == null || a.Go == null) continue;
                string k = "actor." + i;
                sb.Append(k).Append(".name=").Append(a.Go.name).Append('\n');
                var p = a.Go.transform.position;
                sb.Append(k).Append(".active=").Append(a.Go.activeInHierarchy).Append('\n');
                sb.Append(k).Append(".pos=").Append(p.x.ToString("0.00", inv)).Append(',').Append(p.y.ToString("0.00", inv)).Append(',').Append(p.z.ToString("0.00", inv)).Append('\n');
                var an = a.Anims.Length > 0 ? a.Anims[0] : null;
                sb.Append(k).Append(".state=").Append(an != null && an.isActiveAndEnabled ? an.GetCurrentAnimatorStateInfo(0).fullPathHash : 0).Append('\n');
                sb.Append(k).Append(".ai=").Append(a.Script != null && a.Script.enabled).Append('\n');
            }
        }
    }
}
