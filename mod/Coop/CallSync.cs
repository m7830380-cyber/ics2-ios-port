using System;
using System.Collections.Generic;
using System.Reflection;
using ICS2Coop;
using ICS2Shim;
using LiteNetLib.Utils;
using UnityEngine;

namespace ICS2iOS
{
    // Game methods that change the shared world (tools/patcher/CoopHooks.cs patches them to call
    // Platform.Call first). Each has a rule:
    //   Mirror      runs where it was called and is replayed on every other device (doors, lights, elevators,
    //               buying rooms, paying bills). Random is seeded the same on all devices for the call.
    //   Host        a guest sends it to the host instead of running it (it spawns items or pays out: the host's
    //               result reaches the guests through the item / money / NPC sync).
    //   HostReplay  only the host runs it by itself (dice rolls: dirt, fires, bills, mail ...); a guest's own
    //               call is dropped, and the host's call is replayed on the guests with the host's seed.
    //   HostOnly    only the host runs it; guests drop it and see the result through the other syncs (NPC
    //               events: the robber, the bomber, the police inspection ...).
    //   HostEcho    a guest runs it as before (the game's own guest path sends it to the host); when the host
    //               runs it, the guests replay it (a customer's PC switching on).
    //   Punch       a guest's punch hit one of the host's people: the host runs the hit.
    //   HostAlways  only the host runs it, even inside a replay (elevator trips: a guest's car follows the host's,
    //               see StateWatch).
    //   Hit         a fighting customer's blow: the host gives it to the player it went for (FightSync).
    //   Door        an elevator door: sent as "open" or "close", not as a toggle (a door that is half way on one
    //               device would otherwise go the other way there).
    // A replay never changes money (the device that made the call already counted it) and its FBPP writes are
    // not sent again. Calls made inside a call that is already being replayed elsewhere are not sent again.
    public class CallSync : MonoBehaviour
    {
        enum Rule { Mirror, Host, HostReplay, HostOnly, HostEcho, Punch, HostAlways, Hit, Door }

        static readonly Dictionary<string, Rule> Rules = new Dictionary<string, Rule>
        {
            ["LightSwitch.OpenLight"] = Rule.Mirror, ["LightSwitch.CloseLight"] = Rule.Mirror,
            ["Door.OpenDoor"] = Rule.Mirror, ["Door.CloseDoor"] = Rule.Mirror,
            ["ElectricalSwitch.SwitchOpened"] = Rule.Mirror, ["ElectricalSwitch.SwitchClosed"] = Rule.Mirror,
            ["OpenableItem.Interact"] = Rule.Mirror,
            ["CivilManager.OpenShop"] = Rule.Mirror, ["CivilManager.CloseShop"] = Rule.Mirror,
            ["OpenCloseSwitch.Clicked"] = Rule.Mirror, ["OpenCloseSwitch.ForceCloseSwitch"] = Rule.Mirror,
            ["OldElevator.ElevatorButton.Press"] = Rule.Mirror, ["OldElevator.DoorTrigger.Toggle"] = Rule.Door,
            ["OldElevator.ElevatorBrain.GoToPosition"] = Rule.HostAlways, ["OldElevator.ElevatorBrain.EmergencyStopBegin"] = Rule.HostAlways,
            ["Toaster.AddBread"] = Rule.Host, ["Toaster.StartToast"] = Rule.Host,
            ["CoffeeMachine.JugDropped"] = Rule.Mirror, ["CoffeeMachine.StartThePourCoffee"] = Rule.Mirror,
            ["CoffeeCup.Fill"] = Rule.Mirror, ["Oven.StartCookingButton"] = Rule.Mirror, ["Oven.StopCookingButton"] = Rule.Mirror,
            ["HomeFridge.AddFood"] = Rule.Mirror, ["HomeFridge.MinesFood"] = Rule.Mirror,
            ["VendingMachine.AddNumberToMyOrder"] = Rule.Mirror, ["VendingMachine.ResetMachine"] = Rule.Mirror,
            ["VendingMachine.CompleteOrder"] = Rule.Host,
            ["Fan.StartFan"] = Rule.Mirror, ["Fan.StopFan"] = Rule.Mirror,
            ["MinerRigCase.AddNewGraphicCard"] = Rule.Mirror, ["CdManager.OpenedCDROm"] = Rule.Mirror,
            ["AmongDie.ClickedAmogus"] = Rule.Mirror,
            ["objectInteraction.Siphon"] = Rule.Mirror, ["FootstepsManager.DestroyMe"] = Rule.Mirror,
            ["FootstepsManager.SpawnFootPrints"] = Rule.HostReplay, ["FootstepsManager.RandomSpawnTrash"] = Rule.HostReplay,
            ["FootstepsManager.SpawnPoo"] = Rule.HostReplay, ["FootstepsManager.RandomSpawnPoo"] = Rule.HostReplay,
            ["TableManager.SpawnRandomFire"] = Rule.HostReplay, ["FireableItem.SpawnFire"] = Rule.HostReplay,
            ["Fire.FireEnd"] = Rule.Mirror, ["SmokeDetector.SmokeDetecterON"] = Rule.HostReplay,
            ["CafeTemperatureManager.ChangeTemp"] = Rule.HostReplay, ["CafeTemperatureManager.ChangeTempCold"] = Rule.HostReplay,
            ["UpgradeItem.Buy"] = Rule.Mirror, ["UpgradeItem.TextureBuy"] = Rule.Mirror,
            ["SecondCafeManager.BuySecondCafe"] = Rule.Mirror, ["SecondCafeManager.BuyMoreCabin"] = Rule.Mirror,
            ["GameManager.ChangeLedColor"] = Rule.Mirror,
            ["Employee.RecruitButton"] = Rule.Mirror, ["Employee.FireButton"] = Rule.Mirror,
            ["BillManager.SpawnNewBill"] = Rule.HostReplay, ["BillManager.PayedThisBill"] = Rule.Mirror,
            ["BillManager.PayAllBtn"] = Rule.Mirror, ["MailManager.SpawnMail"] = Rule.HostReplay,
            ["Robber.StealItem"] = Rule.HostOnly, ["Bomberman.GoBomb"] = Rule.HostOnly, ["Policeman.ShopControl"] = Rule.HostOnly,
            ["Chef.AddNewOrder"] = Rule.HostOnly,
            ["RaceManager.StartRace"] = Rule.HostReplay, ["RaceManager.StopRace"] = Rule.HostReplay,
            ["RaceManager.OpenWinnerPanel"] = Rule.HostReplay,   // pays the bets: once, on the host
            ["Mascot.MascotGoHome"] = Rule.HostOnly, ["EmployeeManager.OpenMascot"] = Rule.HostOnly,
            ["Bouncer.SetTarget"] = Rule.HostOnly, ["CheckAIMoneyPayed.OnTriggerEnter"] = Rule.HostOnly,
            ["RivalInternetCafe.SetFakeCustomers"] = Rule.HostReplay, ["CİvilOptimizer.OpenRandomFiveCivils"] = Rule.HostOnly,
            ["SlotMachineClub.Roll"] = Rule.Host, ["StripClubBouncer.EntranceFeePayed"] = Rule.Mirror,
            ["BunnyGirl.Payed"] = Rule.Host, ["PirateGamer.DeliveryCompleted"] = Rule.Host, ["Civil.DeliveredOrder"] = Rule.Host,
            ["TableController.Approved"] = Rule.HostEcho, ["TableController.Canceled"] = Rule.HostEcho,
            ["TableController.ExtraTimeApproved"] = Rule.HostEcho,
            ["TableController.MonitorOpened"] = Rule.HostReplay, ["TableController.CivilGetUp"] = Rule.HostReplay,
            ["FoodManager.SendFoodRequest"] = Rule.HostReplay,
            ["OrderRequest.OrderDelivered"] = Rule.Mirror, ["OrderRequest.ApproveOrder"] = Rule.Mirror,
            ["OrderRequest.CloseButton"] = Rule.Mirror, ["Civil.CustomerForceOrderDeliveredComment"] = Rule.Host,
            ["Cabin.SetCivilAnimAndPos"] = Rule.HostReplay, ["Cabin.UpgradeCabin"] = Rule.Mirror,
            ["Civil.PunchSimulate"] = Rule.Host, ["Robber.PunchSimulate"] = Rule.Host, ["Bomberman.PunchRagdoll"] = Rule.Host,
            ["Robber.OnTriggerEnter"] = Rule.Punch, ["Bomberman.OnTriggerEnter"] = Rule.Punch, ["Civil.OnTriggerEnter"] = Rule.Punch,
            ["Civil.Attacked"] = Rule.Hit,
        };

        static bool s_Replaying;          // running a call that came from another device
        static int s_Sent, s_Replayed, s_Failed;
        static readonly Dictionary<string, MethodInfo> s_Methods = new Dictionary<string, MethodInfo>();
        static readonly List<string> s_Recent = new List<string>();
        static int s_Seed = Environment.TickCount;

        public static bool Replaying => s_Replaying;

        void OnEnable()
        {
            Platform.CallHandler = OnCall;
            CoopSession.Received += OnMessage;
        }

        // ---- local calls --------------------------------------------------------------------------------------
        static bool OnCall(object self, string action, object[] args)
        {
            if (!CoopSession.Active || CoopSession.Net.LocalId < 0) return false;
            if (!Rules.TryGetValue(action, out var rule)) return false;
            bool guest = CoopSession.IsClient;
            // a guest's copy of the customer plays the host's blows (animation events still reach its switched-off
            // script): the host already gave that blow to whoever it hit
            if (rule == Rule.Hit) return guest || FightSync.HostHit(self as Civil);
            if (rule == Rule.HostAlways) return guest && CoopSession.WorldReady;
            if (s_Replaying)
            {
                // the replay itself runs. The host running a guest's call also passes on the dice rolls inside
                // it: the guest dropped its own (HostReplay), so they would happen on the host only
                if (rule == Rule.HostReplay && !guest && CoopSession.WorldReady)
                {
                    try { Send(self, action, args, -1); }
                    catch (Exception e) { s_Failed++; Log.Error($"coop: sending {action} failed: {e}"); }
                }
                return false;
            }
            // part of a call replayed elsewhere, or the game setting itself up while it loads
            bool covered = Platform.Covered || !CoopSession.WorldReady;
            try
            {
                switch (rule)
                {
                    case Rule.Mirror:
                        if (!covered) Send(self, action, args, -1);
                        return false;
                    case Rule.Door:
                        // a locked door does nothing (the game says "same floor as the elevator" here only)
                        if (!covered && self is OldElevator.DoorTrigger door && !door.locked)
                            Send(self, action, new object[] { door.isFullyClosed }, -1);
                        return false;
                    case Rule.Host:
                        if (!guest) return false;
                        if (!CoopSession.WorldReady) return false;  // loading: the game sets itself up
                        if (!covered) Send(self, action, args, 0);  // the host runs it
                        return true;
                    case Rule.HostReplay:
                        if (guest) return true;
                        if (!covered) Send(self, action, args, -1);
                        return false;
                    case Rule.HostOnly:
                        return guest;
                    case Rule.HostEcho:
                        if (!guest && !covered) Send(self, action, args, -1);
                        return false;
                    case Rule.Punch:
                        if (!guest) return false;
                        var other = args.Length > 0 ? args[0] as Collider : null;
                        var punch = other != null ? other.GetComponent<Punch>() : null;
                        if (punch != null) Send(self, action, new object[] { punch.baseballBat }, 0);
                        return true;                                // the host's people react on the host only
                }
            }
            catch (Exception e)
            {
                s_Failed++;
                Log.Error($"coop: sending {action} failed: {e}");
            }
            return false;
        }

        /// to = -1: everybody else (a guest's goes to the host, which replays and passes it on).
        static void Send(object self, string action, object[] args, int to)
        {
            // the call runs now (or on the host) with this seed, and with the same seed everywhere else
            int seed = ++s_Seed * 7919 + Time.frameCount;
            UnityEngine.Random.InitState(seed);
            if (to < 0) Platform.CoveredDepth = Platform.CallDepth;
            var w = CoopSession.Net.Begin(Msg.Call);
            w.Put(action);
            w.Put(seed);
            NetRef.Put(w, self as UnityEngine.Object);
            w.Put((byte)args.Length);
            foreach (var a in args) NetRef.PutValue(w, NetRef.CanWrite(a) ? a : null);
            if (to < 0) CoopSession.Net.SendAll(); else CoopSession.Net.SendTo(to);
            s_Sent++;
            Note("sent " + action);
        }

        // ---- remote calls ---------------------------------------------------------------------------------------
        void OnMessage(int from, Msg id, NetDataReader r)
        {
            if (id != Msg.Call) return;
            byte[] raw = r.GetRemainingBytes();
            var rr = new NetDataReader(raw);
            string action = rr.GetString();
            int seed = rr.GetInt();
            UnityEngine.Object self;
            object[] args;
            try
            {
                self = NetRef.Get(rr);
                args = new object[rr.GetByte()];
                for (int i = 0; i < args.Length; i++) args[i] = NetRef.GetValue(rr);
            }
            catch (Exception e) { s_Failed++; Log.Error($"coop: bad call message {action}: {e.Message}"); return; }
            Rules.TryGetValue(action, out var rule);
            if (CoopSession.IsHost && (rule == Rule.Mirror || rule == Rule.Door))
            {
                // pass a guest's call on to the other guests
                var w = CoopSession.Net.Begin(Msg.Call);
                w.Put(raw);
                CoopSession.Net.SendAll(LiteNetLib.DeliveryMethod.ReliableOrdered, from);
            }
            if (rule == Rule.Host && CoopSession.IsHost) { RunAsHost(self, action, args, seed); return; }
            if (rule == Rule.Punch && CoopSession.IsHost) { RunPunch(self, action, args); return; }
            if (rule == Rule.Door) { ReplayDoor(self, args, seed); return; }
            Replay(self, action, args, seed, from);
        }

        /// A guest asked the host to do it: the host runs it as its own call (its results sync as usual).
        static void RunAsHost(UnityEngine.Object self, string action, object[] args, int seed)
        {
            UnityEngine.Random.InitState(seed);
            Invoke(self, action, args, replay: false);
        }

        static void RunPunch(UnityEngine.Object self, string action, object[] args)
        {
            // the hit as the game sees it: one of the host player's punch colliders (bat or fist) entered
            var ray = GameManager.Instance != null ? GameManager.Instance.playerRay : null;
            bool bat = args.Length > 0 && args[0] is bool b && b;
            Collider hit = null;
            if (ray != null && ray.allPunch != null)
                foreach (var p in ray.allPunch)
                    if (p != null && p.baseballBat == bat && p.GetComponent<Collider>() != null) { hit = p.GetComponent<Collider>(); break; }
            if (hit == null) { Log.Warn("coop: no punch collider to replay a guest's hit"); return; }
            Note("guest punched " + (self != null ? self.name : "?"));
            // as if the host had punched: the guest did not run it, so what it changes is sent from here
            Invoke(self, action, new object[] { hit }, replay: false);
        }

        static FieldInfo s_DoorClosing;

        /// An elevator door opened or closed on another device: the same here, unless it already goes that way.
        static void ReplayDoor(UnityEngine.Object self, object[] args, int seed)
        {
            var door = self is GameObject go ? go.GetComponent<OldElevator.DoorTrigger>() : self as OldElevator.DoorTrigger;
            if (door == null || args.Length < 1 || !(args[0] is bool open)) { s_Failed++; Log.Warn("coop: bad elevator door call"); return; }
            if (s_DoorClosing == null) s_DoorClosing = typeof(OldElevator.DoorTrigger).GetField("closing", BindingFlags.NonPublic | BindingFlags.Instance);
            bool closing = s_DoorClosing != null && (bool)s_DoorClosing.GetValue(door);
            bool goingOpen = !door.isFullyClosed && !closing;
            if (open == goingOpen) return;
            // the host's locks are the real ones; a guest's door follows the host's (its car is a moment behind)
            bool locked = door.locked;
            if (locked && CoopSession.IsHost) return;
            door.locked = false;
            UnityEngine.Random.InitState(seed);
            try
            {
                if (open) Invoke(door, "OldElevator.DoorTrigger.Open", new object[0], replay: true);
                else Invoke(door, "OldElevator.DoorTrigger.Close", new object[] { false }, replay: true);
                s_Replayed++;
            }
            finally { if (door != null) door.locked = locked; }
        }

        static void Replay(UnityEngine.Object self, string action, object[] args, int seed, int from)
        {
            UnityEngine.Random.InitState(seed);
            var gm = GameManager.Instance;
            float money = gm != null ? gm.money : 0f;
            var result = Invoke(self, action, args, replay: true);
            if (gm != null) gm.money = money;                       // counted where the call was made
            s_Replayed++;
            // the caller on the host keeps what the call made; here the replay has to put it there
            if (action == "FoodManager.SendFoodRequest" && args.Length > 2 && args[2] is TableController table && result is OrderRequest order)
                table.myOrderRequest = order;
        }

        static object Invoke(UnityEngine.Object self, string action, object[] args, bool replay)
        {
            int dot = action.LastIndexOf('.');
            var type = NetRef.TypeByName(action.Substring(0, dot));
            string name = action.Substring(dot + 1);
            if (type == null) { s_Failed++; Log.Warn("coop: unknown type in " + action); return null; }
            var method = MethodFor(type, name, args.Length);
            if (method == null) { s_Failed++; Log.Warn("coop: no method " + action); return null; }
            object target = null;
            if (!method.IsStatic)
            {
                target = self is GameObject go ? go.GetComponent(type) : self as Component;
                if (target == null || (target is UnityEngine.Object uo && uo == null))
                {
                    s_Failed++;
                    Log.Warn($"coop: {action}: the object is not here");
                    return null;
                }
            }
            var ps = method.GetParameters();
            var call = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++) call[i] = NetRef.As(i < args.Length ? args[i] : null, ps[i].ParameterType);
            bool wasReplaying = s_Replaying;
            int covered = Platform.CoveredDepth;
            s_Replaying = replay;
            if (replay) Platform.CoveredDepth = Platform.CallDepth;
            try
            {
                var result = method.Invoke(target, call);
                Note((replay ? "replayed " : "ran ") + action);
                return result;
            }
            catch (TargetInvocationException e)
            {
                s_Failed++;
                Log.Error($"coop: {action} failed here: {e.InnerException}");
            }
            catch (Exception e)
            {
                s_Failed++;
                Log.Error($"coop: {action} failed here: {e}");
            }
            finally
            {
                s_Replaying = wasReplaying;
                Platform.CoveredDepth = covered;
            }
            return null;
        }

        static MethodInfo MethodFor(Type type, string name, int argCount)
        {
            string key = type.FullName + "." + name + "/" + argCount;
            if (s_Methods.TryGetValue(key, out var m)) return m;
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            foreach (var c in type.GetMethods(all))
                if (c.Name == name && c.GetParameters().Length == argCount) { m = c; break; }
            s_Methods[key] = m;
            return m;
        }

        static void Note(string what)
        {
            s_Recent.Add(what);
            if (s_Recent.Count > 12) s_Recent.RemoveAt(0);
            if (CoopSession.TestMode) Log.Info("coop: " + what);
        }

        // ---- tests --------------------------------------------------------------------------------------------
        public static void Report(System.Text.StringBuilder sb)
        {
            sb.Append("calls.sent=").Append(s_Sent).Append('\n');
            sb.Append("calls.replayed=").Append(s_Replayed).Append('\n');
            sb.Append("calls.failed=").Append(s_Failed).Append('\n');
            sb.Append("calls.recent=").Append(string.Join("|", s_Recent)).Append('\n');
        }
    }
}
