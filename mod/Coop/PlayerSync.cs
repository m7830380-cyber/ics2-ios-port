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
    // Poses: someone at a PC (their camera zooms into the screen; the others see them sitting on the chair),
    // on the strip club couch or at the blackjack table sits there, someone in bed lies down, punches show.
    // Remote players also open the cafe's sliding doors on each device (the doors only react to local bodies).
    // Someone riding an elevator is placed by their height above the car, so they ride along on every screen
    // even though each device's car is a moment ahead or behind.
    // One player at a PC at a time: sitting down at a PC someone else is at is refused (CallSync, Rule.Seat); two
    // who sat down at the same moment are sorted out here (the lower player id keeps the seat, the host first).
    public class PlayerSync : MonoBehaviour
    {
        const float SendInterval = 0.05f;
        const float InterpDelay = 0.1f;

        sealed class Puppet
        {
            public int Player;
            public string Name;
            public GameObject Body;
            public Transform Target;       // where the body's character controller would be (fighting customers aim here)
            public Animator Anim;
            public readonly List<PlayerState> Snapshots = new List<PlayerState>();
            public float ClockOffset;      // local time - sender time, smoothed
            public bool HasOffset;
            public Vector3 LastPos;
            public float Speed;
            public PlayerState Latest;
            public byte Pose;
            public byte Punches;
            public bool PunchesKnown;
            public float FightUntil;
        }

        static readonly Dictionary<int, Puppet> s_Puppets = new Dictionary<int, Puppet>();
        static readonly Dictionary<int, PlayerState> s_LastStates = new Dictionary<int, PlayerState>();   // kept after they leave
        float m_NextSend, m_NextDoors, m_NextSeatCheck, m_NextShow;
        static byte s_Punches;
        static bool s_LastPunchReady = true;
        static System.Reflection.FieldInfo s_PunchField;

        void OnEnable()
        {
            CoopSession.Received += OnMessage;
            CoopSession.Net.PlayerLeft += OnPlayerLeft;
            CoopSession.Net.Disconnected += OnDisconnected;
            CoopSession.Net.Stopped += OnStopped;
        }

        void OnDisable()
        {
            CoopSession.Received -= OnMessage;
            CoopSession.Net.PlayerLeft -= OnPlayerLeft;
            CoopSession.Net.Disconnected -= OnDisconnected;
            CoopSession.Net.Stopped -= OnStopped;
        }

        void OnPlayerLeft(int id, string name) => Remove(id);
        void OnDisconnected(string reason) => OnStopped();
        void OnStopped() { foreach (var id in new List<int>(s_Puppets.Keys)) Remove(id); s_LastStates.Clear(); }

        /// The last body state a player sent (also once they have left: PlayerRecords keeps where they were).
        public static bool LastState(int player, out PlayerState s) => s_LastStates.TryGetValue(player, out s);

        static void Remove(int id)
        {
            if (!s_Puppets.TryGetValue(id, out var p)) return;
            if (p.Body != null) Destroy(p.Body);
            if (p.Target != null) Destroy(p.Target.gameObject);
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
            s_LastStates[s.Player] = s;
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
            CountPunches(gm.playerRay);
            foreach (var p in s_Puppets.Values) Animate(p);
            if (Time.unscaledTime >= m_NextDoors) { m_NextDoors = Time.unscaledTime + 0.2f; OpenSlidingDoors(); }
            if (Time.unscaledTime >= m_NextSeatCheck) { m_NextSeatCheck = Time.unscaledTime + 0.5f; GiveUpSharedSeat(gm.playerRay); }
            if (Time.unscaledTime >= m_NextShow)
            {
                m_NextShow = Time.unscaledTime + 1f;
                foreach (var p in s_Puppets.Values) if (p.Body != null && !Visible(p.Body)) { Log.Warn($"coop: puppet {p.Player} was hidden; shown again"); Show(p.Body); }
            }
        }

        // ---- one player per PC --------------------------------------------------------------------------
        /// The name of the remote player sitting at this PC, or null.
        public static string SeatTakenBy(PlayerComputer pc, int below = int.MaxValue)
        {
            if (pc == null || pc.chairPos == null) return null;
            var c = pc.chairPos.position;
            foreach (var p in s_Puppets.Values)
            {
                if (p.Player >= below || p.Snapshots.Count == 0 || (p.Latest.Flags & PlayerFlags.AtComputer) == 0) continue;
                float dx = p.Latest.SeatX - c.x, dz = p.Latest.SeatZ - c.z;
                if (dx * dx + dz * dz < 0.25f) return p.Name;
            }
            return null;
        }

        /// PlayerComputer.EnterComputer: true = do not sit down (someone else is at this PC).
        public static bool BlockSeat(PlayerComputer pc)
        {
            string who = SeatTakenBy(pc);
            if (who == null) return false;
            var ray = GameManager.Instance != null ? GameManager.Instance.playerRay : null;
            if (ray != null && ray.currentPlayerComputer == pc) ray.currentPlayerComputer = null;   // PlayerRay set it first
            DebugOverlay.Toast(who + " is using this PC");
            Log.Info($"coop: PC '{pc.name}' is taken by {who}");
            return true;
        }

        /// Two players who sat down at the same PC at the same moment: the one with the higher id stands up.
        static void GiveUpSharedSeat(PlayerRay ray)
        {
            var pc = ray != null ? ray.currentPlayerComputer : null;
            if (pc == null || !pc.enterPc || !pc.enterCompleted) return;
            string who = SeatTakenBy(pc, CoopSession.Net.LocalId);
            if (who == null) return;
            pc.ExitComputer();
            ray.currentPlayerComputer = null;
            DebugOverlay.Toast(who + " is using this PC");
            Log.Info($"coop: {who} sat down at '{pc.name}' at the same time; standing up");
        }

        /// PlayerRay._punch goes false while a punch (or the middle finger) plays.
        static void CountPunches(PlayerRay ray)
        {
            if (ray == null) return;
            if (s_PunchField == null) s_PunchField = typeof(PlayerRay).GetField("_punch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (s_PunchField == null) return;
            bool ready = (bool)s_PunchField.GetValue(ray);
            if (s_LastPunchReady && !ready) s_Punches++;
            s_LastPunchReady = ready;
        }

        /// What the local body is doing, and where its seat is.
        static byte LocalPose(GameManager gm, out Vector3 seat, out float seatYaw)
        {
            seat = Vector3.zero; seatYaw = 0f;
            var ray = gm.playerRay;
            if (ray == null) return PlayerPoses.Stand;
            float feet = gm.player.position.y + FeetOffset();
            var pc = ray.currentPlayerComputer;
            if (pc != null && pc.enterPc && pc.chairPos != null && pc.lookPos != null)
            {
                var look = pc.lookPos.position - pc.chairPos.position; look.y = 0f;
                seat = new Vector3(pc.chairPos.position.x, feet, pc.chairPos.position.z);
                seatYaw = look.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(look).eulerAngles.y : pc.chairPos.eulerAngles.y;
                return PlayerPoses.Sit;
            }
            foreach (var cam in new[] { ray.stripClubSitCameraParent, ray.blackjackSitCameraParent })
                if (cam != null && cam.activeInHierarchy)
                {
                    seat = new Vector3(cam.transform.position.x, feet, cam.transform.position.z);
                    seatYaw = cam.transform.eulerAngles.y;
                    return PlayerPoses.Sit;
                }
            if (ray.sleepingObj != null && ray.sleepingObj.activeSelf)
            {
                seat = new Vector3(gm.player.position.x, feet, gm.player.position.z);
                seatYaw = gm.player.eulerAngles.y;
                return PlayerPoses.Sleep;
            }
            return PlayerPoses.Stand;
        }

        /// The cafe's automatic doors open for bodies that walk into them on this device (PlayerRay, Civil ...);
        /// remote players are puppets without those scripts, so they are checked here.
        static autoDoorSlide[] s_Doors;
        static System.Reflection.FieldInfo s_DoorOpen, s_DoorCloseTime, s_DoorDuration, s_DoorInZone;
        static readonly HashSet<autoDoorSlide> s_OpenedByPuppet = new HashSet<autoDoorSlide>();

        static void OpenSlidingDoors()
        {
            if (s_Puppets.Count == 0) return;
            if (s_Doors == null || (s_Doors.Length > 0 && s_Doors[0] == null))
            {
                s_Doors = FindObjectsOfType<autoDoorSlide>();
                var t = typeof(autoDoorSlide);
                const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                s_DoorOpen = t.GetField("openDoors", all);
                s_DoorCloseTime = t.GetField("doorCloseTime", all);
                s_DoorDuration = t.GetField("doorOpenDuration", all);
                s_DoorInZone = t.GetField("objectsInTriggerZone", all);
            }
            if (s_DoorOpen == null) return;
            foreach (var door in s_Doors)
            {
                if (door == null) continue;
                var zone = door.GetComponent<Collider>();
                if (zone == null) continue;
                var b = zone.bounds;
                b.Expand(new Vector3(0.6f, 2f, 0.6f));
                bool inside = false;
                foreach (var p in s_Puppets.Values)
                    if (p.Body != null && b.Contains(p.Body.transform.position + Vector3.up)) { inside = true; break; }
                if (inside)
                {
                    s_OpenedByPuppet.Add(door);
                    s_DoorOpen.SetValue(door, true);
                }
                else if (s_OpenedByPuppet.Remove(door))
                {
                    // let it close unless a local body is still in it
                    var list = s_DoorInZone?.GetValue(door) as System.Collections.ICollection;
                    if (list == null || list.Count == 0)
                    {
                        s_DoorOpen.SetValue(door, false);
                        if (s_DoorCloseTime != null && s_DoorDuration != null)
                            s_DoorCloseTime.SetValue(door, Time.time + (float)s_DoorDuration.GetValue(door));
                    }
                }
            }
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
            if (ray != null && (ray.health <= 0f || ray.falling)) flags |= PlayerFlags.Down;
            byte pose = LocalPose(gm, out var seat, out float seatYaw);
            int lift = StateWatch.CarUnder(t.position, out var car);
            var s = new PlayerState
            {
                Player = CoopSession.Net.LocalId,
                Time = Time.unscaledTime,
                X = t.position.x, Y = t.position.y, Z = t.position.z,
                Yaw = t.eulerAngles.y, Pitch = pitch, Flags = flags,
                Pose = pose, SeatX = seat.x, SeatY = seat.y, SeatZ = seat.z, SeatYaw = seatYaw,
                Punches = s_Punches,
                Lift = lift >= 0 && lift < PlayerState.NoLift ? (byte)lift : PlayerState.NoLift,
                LiftY = car != null ? t.position.y - car.position.y : 0f,
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
            // in an elevator: on this device's car, at the height they have above theirs
            if (a.Lift != PlayerState.NoLift && a.Lift == b.Lift)
            {
                var car = StateWatch.CarAt(a.Lift);
                if (car != null) pos.y = car.position.y + Mathf.Lerp(a.LiftY, b.LiftY, k);
            }

            var tr = p.Body.transform;
            var latest = p.Latest;
            if (p.Target != null) p.Target.position = pos;
            if (latest.Pose != PlayerPoses.Stand)
            {
                // sitting / lying where the seat is (the player's own body did not move there)
                tr.position = new Vector3(latest.SeatX, latest.SeatY, latest.SeatZ);
                tr.rotation = Quaternion.Euler(0f, latest.SeatYaw, 0f);
                p.Speed = 0f;
                p.LastPos = pos;
            }
            else
            {
                tr.position = pos + Vector3.up * FeetOffset();
                tr.rotation = Quaternion.Euler(0f, yaw, 0f);
                float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
                var flat = pos - p.LastPos; flat.y = 0f;
                p.Speed = Mathf.Lerp(p.Speed, flat.magnitude / dt, 0.2f);
                p.LastPos = pos;
            }
            var anim = p.Anim;
            if (anim == null || !anim.isActiveAndEnabled) return;
            if (latest.Pose != p.Pose)
            {
                byte was = p.Pose;
                p.Pose = latest.Pose;
                if (was == PlayerPoses.Sleep) anim.Rebind();            // "Sleep" has no way back by itself
                if (p.Pose == PlayerPoses.Sleep) anim.Play("Sleep", 0, 0f);
            }
            anim.SetBool("sitbank", p.Pose == PlayerPoses.Sit);
            anim.SetBool("walk", p.Pose == PlayerPoses.Stand && p.Speed > 0.3f && p.Speed <= 2.6f);
            anim.SetBool("run", p.Pose == PlayerPoses.Stand && p.Speed > 2.6f);
            // punches: one per step of the sender's counter (fists up for a moment)
            if (!p.PunchesKnown) { p.Punches = latest.Punches; p.PunchesKnown = true; }
            if (latest.Punches != p.Punches)
            {
                p.Punches = latest.Punches;
                if (p.Pose == PlayerPoses.Stand)
                {
                    anim.SetBool("fight", true);
                    anim.SetTrigger("punch");
                    p.FightUntil = Time.unscaledTime + 1.2f;
                }
            }
            if (p.FightUntil > 0f && Time.unscaledTime > p.FightUntil) { anim.SetBool("fight", false); p.FightUntil = 0f; }
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
            if (s_Puppets.TryGetValue(player, out var p))
            {
                if (p.Body != null) return p;
                Remove(player);     // its body went with an old scene: a new one
            }
            var body = MakeBody(player);
            if (body == null) return null;
            p = new Puppet { Player = player, Name = CoopSession.Net.NameOf(player), Body = body, Anim = body.GetComponentInChildren<Animator>() };
            p.Target = new GameObject("CoopTarget" + player).transform;
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
            Show(body);
            var anim = body.GetComponentInChildren<Animator>();
            if (anim != null)
            {
                anim.enabled = true;
                anim.applyRootMotion = false;
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            return body;
        }

        /// The game's distance optimizer (FIMSpace Optimizers) switches far-away customers' renderers and LOD groups
        /// off; a copy made while its customer was switched off stayed invisible (the optimizer is stripped from it).
        static void Show(GameObject body)
        {
            foreach (var r in body.GetComponentsInChildren<Renderer>())
            {
                r.enabled = true;
                r.forceRenderingOff = false;
                if (r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                if (r is SkinnedMeshRenderer sm) sm.updateWhenOffscreen = true;
            }
            foreach (var lod in body.GetComponentsInChildren<LODGroup>()) lod.enabled = true;
        }

        public static GameObject PuppetBody(int player) => s_Puppets.TryGetValue(player, out var p) ? p.Body : null;

        /// Remote players' bodies, as the game's player transform would be (character controller centre); not
        /// those knocked out (a fight is over for them, as for the host's own player).
        public static IEnumerable<KeyValuePair<int, Transform>> Targets()
        {
            foreach (var p in s_Puppets.Values)
                if (p.Target != null && p.Body != null && p.Body.activeInHierarchy && p.Snapshots.Count > 0
                    && (p.Latest.Flags & PlayerFlags.Down) == 0)
                    yield return new KeyValuePair<int, Transform>(p.Player, p.Target);
        }

        public static Transform TargetOf(int player) => s_Puppets.TryGetValue(player, out var p) ? p.Target : null;

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
                sb.Append(k).Append(".pose=").Append(p.Pose).Append('\n');
                sb.Append(k).Append(".sitting=").Append(p.Anim != null && p.Anim.isActiveAndEnabled && p.Anim.GetBool("sitbank")).Append('\n');
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
            foreach (var r in go.GetComponentsInChildren<Renderer>()) if (r.enabled && !r.forceRenderingOff && r.gameObject.activeInHierarchy) return true;
            return false;
        }
    }
}
