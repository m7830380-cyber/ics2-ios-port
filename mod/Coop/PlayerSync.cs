using System.Collections.Generic;
using ICS2Coop;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace ICS2iOS
{
    // Remote players on screen. Each side sends its own body 20 times a second; every remote player is shown
    // as a puppet: a copy of one of the cafe's customer characters with its AI, navigation, physics and UI
    // removed, moved by snapshot interpolation (rendered 100 ms in the past) and animated walk / run / idle.
    public class PlayerSync : MonoBehaviour
    {
        const float SendInterval = 0.05f;
        const float InterpDelay = 0.1f;

        sealed class Puppet
        {
            public int Player;
            public string Name;
            public GameObject Body;
            public Animator Anim;
            public readonly List<PlayerState> Snapshots = new List<PlayerState>();
            public float ClockOffset;      // local time - sender time, smoothed
            public bool HasOffset;
            public Vector3 LastPos;
            public float Speed;
            public PlayerState Latest;
        }

        static readonly Dictionary<int, Puppet> s_Puppets = new Dictionary<int, Puppet>();
        float m_NextSend;

        void OnEnable()
        {
            CoopSession.Received += OnMessage;
            CoopSession.Net.PlayerLeft += OnPlayerLeft;
            CoopSession.Net.Disconnected += OnDisconnected;
        }

        void OnDisable()
        {
            CoopSession.Received -= OnMessage;
            CoopSession.Net.PlayerLeft -= OnPlayerLeft;
            CoopSession.Net.Disconnected -= OnDisconnected;
        }

        void OnPlayerLeft(int id, string name) => Remove(id);
        void OnDisconnected(string reason) { foreach (var id in new List<int>(s_Puppets.Keys)) Remove(id); }

        static void Remove(int id)
        {
            if (!s_Puppets.TryGetValue(id, out var p)) return;
            if (p.Body != null) Destroy(p.Body);
            s_Puppets.Remove(id);
            Log.Info($"coop: removed puppet of player {id}");
        }

        void OnMessage(int from, Msg id, NetDataReader r)
        {
            if (id != Msg.PlayerState) return;
            var s = PlayerState.Read(r);
            if (CoopSession.IsHost)
            {
                if (s.Player != from) return; // a client may only move itself
                var w = CoopSession.Net.Begin(Msg.PlayerState);
                s.Write(w);
                CoopSession.Net.SendAll(DeliveryMethod.Sequenced, from);
            }
            if (s.Player == CoopSession.Net.LocalId) return;
            var p = GetPuppet(s.Player);
            if (p == null) return;
            float offset = Time.unscaledTime - s.Time;
            if (!p.HasOffset || offset < p.ClockOffset) p.ClockOffset = offset; // lowest delay seen = best estimate
            else p.ClockOffset = Mathf.Lerp(p.ClockOffset, offset, 0.01f);
            p.HasOffset = true;
            p.Snapshots.Add(s);
            if (p.Snapshots.Count > 40) p.Snapshots.RemoveAt(0);
            p.Latest = s;
        }

        void Update()
        {
            if (!CoopSession.Active || CoopSession.Net.LocalId < 0) return;
            var gm = GameManager.Instance;
            if (gm == null || gm.player == null) return;
            if (Time.unscaledTime >= m_NextSend)
            {
                m_NextSend = Time.unscaledTime + SendInterval;
                SendLocal(gm);
            }
            foreach (var p in s_Puppets.Values) Animate(p);
        }

        static void SendLocal(GameManager gm)
        {
            var t = gm.player;
            float pitch = 0f;
            if (gm.playerCamera != null)
            {
                pitch = gm.playerCamera.transform.eulerAngles.x;
                if (pitch > 180f) pitch -= 360f;
            }
            byte flags = 0;
            var ray = gm.playerRay;
            if (ray != null && ray.freezed) flags |= PlayerFlags.Frozen;
            if (ray != null && ray.currentPlayerComputer != null && ray.currentPlayerComputer.enterPc) flags |= PlayerFlags.AtComputer;
            var s = new PlayerState
            {
                Player = CoopSession.Net.LocalId,
                Time = Time.unscaledTime,
                X = t.position.x, Y = t.position.y, Z = t.position.z,
                Yaw = t.eulerAngles.y, Pitch = pitch, Flags = flags,
            };
            var w = CoopSession.Net.Begin(Msg.PlayerState);
            s.Write(w);
            CoopSession.Net.SendAll(DeliveryMethod.Sequenced);
        }

        static void Animate(Puppet p)
        {
            if (p.Body == null || p.Snapshots.Count == 0) return;
            float renderTime = Time.unscaledTime - p.ClockOffset - InterpDelay;
            var snaps = p.Snapshots;
            // drop snapshots older than the pair we interpolate between
            while (snaps.Count > 2 && snaps[1].Time <= renderTime) snaps.RemoveAt(0);
            PlayerState a = snaps[0], b = snaps.Count > 1 ? snaps[1] : snaps[0];
            float k = b.Time > a.Time ? Mathf.Clamp01((renderTime - a.Time) / (b.Time - a.Time)) : 1f;
            var pos = Vector3.Lerp(new Vector3(a.X, a.Y, a.Z), new Vector3(b.X, b.Y, b.Z), k);
            float yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, k);

            var tr = p.Body.transform;
            tr.position = pos + Vector3.up * FeetOffset();
            tr.rotation = Quaternion.Euler(0f, yaw, 0f);

            float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
            var flat = pos - p.LastPos; flat.y = 0f;
            p.Speed = Mathf.Lerp(p.Speed, flat.magnitude / dt, 0.2f);
            p.LastPos = pos;
            if (p.Anim != null && p.Anim.isActiveAndEnabled)
            {
                p.Anim.SetBool("walk", p.Speed > 0.3f && p.Speed <= 2.6f);
                p.Anim.SetBool("run", p.Speed > 2.6f);
            }
        }

        /// The synced position is the character controller's transform; the puppet's root is at its feet.
        static float FeetOffset()
        {
            var cc = GameManager.Instance != null && GameManager.Instance.player != null
                ? GameManager.Instance.player.GetComponent<CharacterController>() : null;
            return cc == null ? -1f : cc.center.y - cc.height * 0.5f;
        }

        Puppet GetPuppet(int player)
        {
            if (s_Puppets.TryGetValue(player, out var p)) return p;
            var body = MakeBody(player);
            if (body == null) return null;
            p = new Puppet { Player = player, Name = CoopSession.Net.NameOf(player), Body = body, Anim = body.GetComponentInChildren<Animator>() };
            s_Puppets[player] = p;
            Log.Info($"coop: created puppet for player {player} '{p.Name}' from '{body.name}'");
            return p;
        }

        /// A customer copied while inactive (so none of its scripts wake up), stripped to renderers + animator.
        static GameObject MakeBody(int player)
        {
            var civils = GameManager.Instance?.civilManager?.allCivils;
            if (civils == null || civils.Length == 0) { Log.Warn("coop: no customer model for the puppet"); return null; }
            var src = civils[(player * 7) % civils.Length];
            var holder = new GameObject("CoopPuppetHolder");
            holder.SetActive(false);
            var body = Instantiate(src.gameObject, holder.transform);
            body.name = "CoopPlayer" + player;
            // props the customer may be showing (cigarette, cash, "didn't pay" timer, emoji bubble) and UI
            var civil = body.GetComponent<Civil>();
            if (civil != null)
                foreach (var prop in new[] { civil.cigarette, civil.fiveDollar, civil.hundredDollar, civil.oneDollar, civil.TenDollar,
                                             civil.TwentyDollar, civil.noPayedObj, civil.emojies != null ? civil.emojies.gameObject : null })
                    if (prop != null) prop.SetActive(false);
            foreach (var canvas in body.GetComponentsInChildren<Canvas>(true)) canvas.gameObject.SetActive(false);
            // dependents first: joints before rigidbodies, scripts and audio filters before Canvas / AudioSource
            var strip = new List<Component>();
            foreach (var c in body.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c is Transform || c is Animator || c is Renderer || c is MeshFilter) continue;
                if (c is Behaviour || c is Collider || c is Rigidbody || c is Joint || c is Cloth || c is ParticleSystem)
                    strip.Add(c);
            }
            strip.Sort((x, y) => StripOrder(x).CompareTo(StripOrder(y)));
            foreach (var c in strip) if (c != null) DestroyImmediate(c);
            body.transform.SetParent(null, false);
            Destroy(holder);
            body.SetActive(true);
            var anim = body.GetComponentInChildren<Animator>();
            if (anim != null)
            {
                anim.enabled = true;
                anim.applyRootMotion = false;
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            return body;
        }

        public static GameObject FirstPuppetBody()
        {
            foreach (var p in s_Puppets.Values) if (p.Body != null) return p.Body;
            return null;
        }

        static int StripOrder(Component c)
        {
            if (c is Joint || c is MonoBehaviour) return 0;
            if (c is Canvas || c is AudioSource || c is Rigidbody) return 2;
            return 1;
        }

        // ---- test reports -------------------------------------------------------------------------------
        public static void Report(System.Text.StringBuilder sb)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            sb.Append("puppets=").Append(s_Puppets.Count).Append('\n');
            foreach (var p in s_Puppets.Values)
            {
                string k = "puppet." + p.Player;
                if (p.Body == null) { sb.Append(k).Append(".body=missing\n"); continue; }
                var pos = p.Body.transform.position - Vector3.up * FeetOffset();
                sb.Append(k).Append(".pos=").Append(pos.x.ToString("0.00", inv)).Append(',').Append(pos.y.ToString("0.00", inv)).Append(',').Append(pos.z.ToString("0.00", inv)).Append('\n');
                sb.Append(k).Append(".active=").Append(p.Body.activeInHierarchy).Append('\n');
                sb.Append(k).Append(".visible=").Append(Visible(p.Body)).Append('\n');
                sb.Append(k).Append(".walk=").Append(p.Anim != null && (p.Anim.GetBool("walk") || p.Anim.GetBool("run"))).Append('\n');
                sb.Append(k).Append(".speed=").Append(p.Speed.ToString("0.00", inv)).Append('\n');
                var rs = p.Body.GetComponentsInChildren<Renderer>();
                if (rs.Length > 0)
                {
                    var b = rs[0].bounds;
                    foreach (var r in rs) b.Encapsulate(r.bounds);
                    sb.Append(k).Append(".size=").Append(b.size.x.ToString("0.00", inv)).Append(',').Append(b.size.y.ToString("0.00", inv)).Append(',').Append(b.size.z.ToString("0.00", inv)).Append('\n');
                    sb.Append(k).Append(".renderers=").Append(rs.Length).Append('\n');
                }
            }
        }

        static bool Visible(GameObject go)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>()) if (r.enabled && r.gameObject.activeInHierarchy) return true;
            return false;
        }
    }
}
