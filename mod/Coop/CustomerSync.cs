using System.Collections.Generic;
using System.Text;
using ICS2Coop;
using ICS2Shim;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ICS2iOS
{
    // Customers and the other walking people of the town (CivilManager.allCivils, a fixed pool placed in the
    // scene, so index i is the same person on every device).
    //
    // The host runs them. A guest switches their AI and navigation off and shows them where the host has them:
    // position, facing, animation (parameters and animator states, see AnimSync), state (waiting to pay, price) and the props they
    // hold (cash, cigarette, "did not pay" timer). They keep their colliders, so the guest can still look at a
    // customer and press the key to take the cash; that, and answering PC requests in the CafeSoft app, is
    // sent to the host, which runs the game's own code for it (Platform.Forward hooks in the patched game).
    public class CustomerSync : MonoBehaviour
    {
        const float SendInterval = 0.1f, FullInterval = 1f, RequestCheck = 0.25f;
        const int ChunkBytes = 900;

        sealed class Mirror
        {
            public bool Active, HasTarget;
            public Vector3 Pos;
            public float Yaw;
            public byte Enum, Props;
            public float Price, Patience;
            public AnimSync.State Anim;
            public bool Sitting;
            public byte Layer;      // the host's physics layer for the person ("Customer" near the chair: no pushing)
        }

        static Civil[] s_Civils;
        static AnimSync.Layout[] s_Params;
        static Mirror[] s_Last, s_Target;
        static bool[] s_Ragdoll;                 // guest: knocked down here (bones on physics)
        static bool s_GuestSetup;
        float m_NextSend, m_NextFull, m_NextRequestCheck, m_NextLinks;
        static readonly Dictionary<TableController, int> s_OpenRequests = new Dictionary<TableController, int>();

        void OnEnable()
        {
            CoopSession.Received += OnMessage;
            SceneManager.sceneLoaded += (s, m) => { s_Civils = null; s_GuestSetup = false; s_OpenRequests.Clear(); };
            Platform.ForwardHandler = Forward;
            Platform.NotificationGenerated = OnNotification;
        }

        static bool Bind()
        {
            if (s_Civils != null) return true;
            var cm = GameManager.Instance != null ? GameManager.Instance.civilManager : null;
            if (cm == null || cm.allCivils == null) return false;
            s_Civils = cm.allCivils;
            s_Params = new AnimSync.Layout[s_Civils.Length];
            s_Last = new Mirror[s_Civils.Length];
            s_Target = new Mirror[s_Civils.Length];
            s_Ragdoll = new bool[s_Civils.Length];
            for (int i = 0; i < s_Civils.Length; i++)
                s_Params[i] = AnimSync.LayoutOf(s_Civils[i] != null ? s_Civils[i].anim : null);
            return true;
        }

        void Update()
        {
            if (!CoopSession.Active || !Bind()) return;
            if (CoopSession.IsHost)
            {
                float now = Time.unscaledTime;
                if (now >= m_NextSend)
                {
                    bool full = now >= m_NextFull;
                    if (full) m_NextFull = now + FullInterval;
                    m_NextSend = now + SendInterval;
                    SendStates(full);
                }
                if (now >= m_NextRequestCheck) { m_NextRequestCheck = now + RequestCheck; CheckRequests(); }
                if (now >= m_NextLinks) { m_NextLinks = now + 1f; SendTableLinks(); }
            }
            else if (s_GuestSetup) Smooth();
        }

        // ---- host -----------------------------------------------------------------------------------------
        static Mirror Capture(int i)
        {
            var c = s_Civils[i];
            var m = new Mirror { Active = c.gameObject.activeInHierarchy, Pos = c.transform.position, Yaw = c.transform.eulerAngles.y };
            m.Enum = (byte)c.myEnum;
            m.Price = c.payedPrice;
            m.Patience = c.patientTimer;
            m.Sitting = c.sittedComputer;
            m.Layer = (byte)c.gameObject.layer;
            byte props = 0;
            if (c.cigarette != null && c.cigarette.activeSelf) props |= 1;
            if (c.fiveDollar != null && c.fiveDollar.activeSelf) props |= 2;
            if (c.hundredDollar != null && c.hundredDollar.activeSelf) props |= 4;
            if (c.oneDollar != null && c.oneDollar.activeSelf) props |= 8;
            if (c.TenDollar != null && c.TenDollar.activeSelf) props |= 16;
            if (c.TwentyDollar != null && c.TwentyDollar.activeSelf) props |= 32;
            if (c.noPayedObj != null && c.noPayedObj.activeSelf) props |= 64;
            m.Props = props;
            m.Anim = AnimSync.Capture(c.anim, s_Params[i]);
            return m;
        }

        static bool Changed(Mirror a, Mirror b)
        {
            if (a == null || a.Active != b.Active) return true;
            if (!b.Active) return false;
            if ((a.Pos - b.Pos).sqrMagnitude > 0.0004f || Mathf.Abs(Mathf.DeltaAngle(a.Yaw, b.Yaw)) > 1f) return true;
            if (a.Enum != b.Enum || a.Props != b.Props || a.Sitting != b.Sitting || a.Layer != b.Layer || Mathf.Abs(a.Price - b.Price) > 0.01f) return true;
            if (Mathf.Abs(a.Patience - b.Patience) > 0.5f) return true;
            return AnimSync.Changed(a.Anim, b.Anim);
        }

        static void SendStates(bool full)
        {
            NetDataWriter w = null;
            int count = 0, countPos = 0;
            for (int i = 0; i < s_Civils.Length; i++)
            {
                if (s_Civils[i] == null) continue;
                var m = Capture(i);
                if (!full && !Changed(s_Last[i], m)) continue;
                s_Last[i] = m;
                if (w == null)
                {
                    w = CoopSession.Net.Begin(Msg.CustomerStates);
                    countPos = w.Length;
                    w.Put((short)0);
                    count = 0;
                }
                w.Put((short)i);
                w.Put(m.Active);
                if (m.Active)
                {
                    w.Put(m.Pos.x); w.Put(m.Pos.y); w.Put(m.Pos.z);
                    w.Put(m.Yaw);
                    w.Put(m.Enum); w.Put(m.Price); w.Put(m.Patience); w.Put(m.Sitting); w.Put(m.Props); w.Put(m.Layer);
                    AnimSync.Write(w, m.Anim);
                }
                count++;
                if (w.Length > ChunkBytes) { Flush(w, countPos, count); w = null; }
            }
            if (w != null) Flush(w, countPos, count);
        }

        static void Flush(NetDataWriter w, int countPos, int count)
        {
            var data = w.Data;
            data[countPos] = (byte)(count & 0xff);
            data[countPos + 1] = (byte)(count >> 8);
            CoopSession.Net.SendAll(DeliveryMethod.Unreliable);
        }

        static int CivIndex(Civil c) => c == null || s_Civils == null ? -1 : System.Array.IndexOf(s_Civils, c);

        static void SendTableLinks()
        {
            var w = CoopSession.Net.Begin(Msg.TableLinks);
            var tables = FindObjectsOfType<TableController>();
            int countPos = w.Length;
            w.Put((short)0);
            int n = 0;
            foreach (var t in tables)
            {
                int id = ItemSync.IdOf(t.gameObject);
                if (id <= 0) continue;
                w.Put(id); w.Put((short)CivIndex(t.myCivil)); w.Put(t.used);
                n++;
                if (w.Length > ChunkBytes) { Flush(w, countPos, n); w = CoopSession.Net.Begin(Msg.TableLinks); countPos = w.Length; w.Put((short)0); n = 0; }
            }
            Flush(w, countPos, n);
        }

        static void LinkTable(TableController t, int civ, bool used)
        {
            var c = s_Civils != null && civ >= 0 && civ < s_Civils.Length ? s_Civils[civ] : null;
            t.myCivil = c;
            t.used = used;
            if (c != null) c.myTableController = t;
        }

        /// A customer asks for a PC on the host: show the request in the guests' CafeSoft app too.
        static void OnNotification(object table, string pcName, float timer, int extra, string message)
        {
            if (!CoopSession.IsHost) return;
            var t = table as TableController;
            int id = ItemSync.IdOf(t != null ? t.gameObject : null);
            if (id <= 0) { Log.Warn("coop: PC request for a table without network id"); return; }
            s_OpenRequests[t] = id;
            var w = CoopSession.Net.Begin(Msg.RequestOpen);
            w.Put(id); w.Put(pcName ?? ""); w.Put(timer); w.Put(extra); w.Put(message ?? "casual");
            w.Put(CivIndex(t.myCivil));
            CoopSession.Net.SendAll();
        }

        static void CheckRequests()
        {
            List<TableController> closed = null;
            foreach (var kv in s_OpenRequests)
                if (kv.Key == null || kv.Key.myCustomerRequest == null) (closed = closed ?? new List<TableController>()).Add(kv.Key);
            if (closed == null) return;
            foreach (var t in closed)
            {
                CoopSession.Net.Begin(Msg.RequestClose).Put(s_OpenRequests[t]);
                CoopSession.Net.SendAll();
                s_OpenRequests.Remove(t);
            }
        }

        // ---- guest ----------------------------------------------------------------------------------------
        static void SetupGuest()
        {
            if (s_GuestSetup) return;
            foreach (var c in s_Civils)
            {
                if (c == null) continue;
                c.enabled = false;            // no AI on the guest: the host drives the person
                if (c.myAgent != null) c.myAgent.enabled = false;
            }
            s_GuestSetup = true;
            Log.Info($"coop: {s_Civils.Length} town people now follow the host");
        }

        static void ApplyStates(NetDataReader r)
        {
            if (SceneManager.GetActiveScene().name != "MainScene" || !Bind()) return;
            SetupGuest();
            int n = r.GetShort();
            for (int e = 0; e < n; e++)
            {
                int i = r.GetShort();
                bool active = r.GetBool();
                var c = i >= 0 && i < s_Civils.Length ? s_Civils[i] : null;
                if (!active)
                {
                    if (c != null && c.gameObject.activeSelf) c.gameObject.SetActive(false);
                    if (c != null) s_Target[i] = null;
                    continue;
                }
                var m = new Mirror { Active = true, HasTarget = true };
                m.Pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                m.Yaw = r.GetFloat();
                m.Enum = r.GetByte(); m.Price = r.GetFloat(); m.Patience = r.GetFloat(); m.Sitting = r.GetBool(); m.Props = r.GetByte(); m.Layer = r.GetByte();
                m.Anim = AnimSync.Read(r);
                if (c == null) continue;
                bool wasActive = c.gameObject.activeSelf;
                if (!wasActive) c.gameObject.SetActive(true);
                // the host's layer first: walking into the chair on the "Default" layer, the guest's copy shoved
                // the keyboard, mouse and monitor off the desk, and that motion went back to the host (ItemSync),
                // so the PC was no longer ready and the customer got up again at once
                if (c.gameObject.layer != m.Layer) c.gameObject.layer = m.Layer;
                // sitting down / getting up is a jump on the host too (into the chair, back to the floor)
                bool jump = s_Target[i] != null && s_Target[i].Sitting != m.Sitting;
                if (!wasActive || jump || s_Target[i] == null || (c.transform.position - m.Pos).sqrMagnitude > 9f)
                    c.transform.SetPositionAndRotation(m.Pos, Quaternion.Euler(0f, m.Yaw, 0f));
                s_Target[i] = m;
                c.myEnum = (CivilEnum)m.Enum;
                c.payedPrice = m.Price;
                c.patientTimer = m.Patience;
                c.sittedComputer = m.Sitting;
                SetProp(c.cigarette, (m.Props & 1) != 0);
                SetProp(c.fiveDollar, (m.Props & 2) != 0);
                SetProp(c.hundredDollar, (m.Props & 4) != 0);
                SetProp(c.oneDollar, (m.Props & 8) != 0);
                SetProp(c.TenDollar, (m.Props & 16) != 0);
                SetProp(c.TwentyDollar, (m.Props & 32) != 0);
                SetProp(c.noPayedObj, (m.Props & 64) != 0);
                bool down = m.Enum == (byte)CivilEnum.Dead;
                SetRagdoll(i, down);
                if (!down) AnimSync.Apply(c.anim, s_Params[i], m.Anim);
            }
        }

        static void SetProp(GameObject go, bool on) { if (go != null && go.activeSelf != on) go.SetActive(on); }

        /// Knocked down / killed on the host (Civil.RagdollON): the body falls here too, by this device's own
        /// physics, and stands up again when the host's does (RagdollOFF). Without this the guest's copy froze
        /// in its last pose (its animator gets no more states from a dead person) and vanished later.
        static void SetRagdoll(int i, bool on)
        {
            if (s_Ragdoll[i] == on) return;
            s_Ragdoll[i] = on;
            var c = s_Civils[i];
            try
            {
                if (c.anim != null) c.anim.enabled = !on;
                // pushed away from whoever stands closest here (the host pushes along its hitter's view)
                var push = -c.transform.forward;
                var me = GameManager.Instance != null ? GameManager.Instance.player : null;
                if (me != null)
                {
                    var d = c.transform.position - me.position; d.y = 0f;
                    if (d.sqrMagnitude < 16f && d.sqrMagnitude > 1e-4f) push = d.normalized;
                }
                foreach (var rb in c.GetComponentsInChildren<Rigidbody>(true))
                {
                    if (rb.gameObject == c.gameObject) continue;   // the root follows the host's position
                    rb.isKinematic = false;
                    rb.useGravity = on;
                    if (on) { rb.velocity = Vector3.zero; rb.AddForce(push * 300f); }
                }
                foreach (var col in c.GetComponentsInChildren<Collider>(true))
                    if (col.gameObject != c.gameObject) col.enabled = on;
            }
            catch (System.Exception e) { Log.Warn($"coop: ragdoll of customer {i}: {e.Message}"); }
        }

        static void Smooth()
        {
            float k = 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime);
            for (int i = 0; i < s_Civils.Length; i++)
            {
                var m = s_Target[i];
                var c = s_Civils[i];
                if (m == null || c == null || !c.gameObject.activeSelf || s_Ragdoll[i]) continue;
                c.transform.position = Vector3.Lerp(c.transform.position, m.Pos, k);
                c.transform.rotation = Quaternion.Slerp(c.transform.rotation, Quaternion.Euler(0f, m.Yaw, 0f), k);
            }
        }

        /// Guest side of the Platform.Forward hooks.
        static bool Forward(object self, string action, int arg)
        {
            if (!CoopSession.IsClient || CallSync.Replaying) return false;   // a replay of the host's call runs here
            Log.Info($"coop: guest action {action} ({arg})");
            switch (action)
            {
                case "TableController.Approved":
                case "TableController.ExtraTimeApproved":
                case "TableController.Canceled":
                {
                    int id = ItemSync.IdOf((self as TableController)?.gameObject);
                    if (id <= 0)
                    {
                        Log.Warn($"coop: {action} on a table without network id ({(self as Component)?.name}); ignored");
                        return true; // a table the host does not know: nothing to do
                    }
                    var w = CoopSession.Net.Begin(Msg.RequestAnswer);
                    w.Put(id);
                    w.Put(action == "TableController.Canceled" ? 1 : 0);
                    CoopSession.Net.SendTo(0);
                    Log.Info($"coop: PC request on table {id} {(action.EndsWith("Canceled") ? "refused" : "approved")} (sent to the host)");
                    return true;
                }
                case "Civil.TakedMoney":
                {
                    var c = self as Civil;
                    int index = s_Civils != null ? System.Array.IndexOf(s_Civils, c) : -1;
                    if (index < 0) return true;
                    var w = CoopSession.Net.Begin(Msg.CustomerAction);
                    w.Put(index); w.Put(1); w.Put(arg);
                    CoopSession.Net.SendTo(0);
                    // until the host's state arrives: this customer has paid (no second take)
                    c.payedPrice = 0f;
                    c.myEnum = CivilEnum.Waiting;
                    Log.Info($"coop: cash taken from customer {index} (sent to the host)");
                    return true;
                }
            }
            return false;
        }

        // ---- messages ---------------------------------------------------------------------------------------
        void OnMessage(int from, Msg id, NetDataReader r)
        {
            switch (id)
            {
                case Msg.CustomerStates when CoopSession.IsClient:
                    ApplyStates(r);
                    break;
                case Msg.RequestOpen when CoopSession.IsClient:
                {
                    int tid = r.GetInt(); string pc = r.GetString(); float timer = r.GetFloat(); int extra = r.GetInt(); string msg = r.GetString();
                    int civ = r.GetInt();
                    var table = ItemSync.Get(tid)?.GetComponent<TableController>();
                    if (table == null) { Log.Warn($"coop: PC request for unknown table {tid}"); break; }
                    // the table drops a request without a customer (TableController.Update)
                    LinkTable(table, civ, table.used);
                    if (table.myCustomerRequest != null) table.myCustomerRequest.DestroyForce();
                    GameManager.Instance.cafeSoftManager.GenerateNotification(table, pc, timer, extra, msg);
                    break;
                }
                case Msg.TableLinks when CoopSession.IsClient:
                {
                    if (!Bind()) break;
                    int n = r.GetShort();
                    for (int k = 0; k < n; k++)
                    {
                        int tid = r.GetInt(); int civ = r.GetShort(); bool used = r.GetBool();
                        var table = ItemSync.Get(tid)?.GetComponent<TableController>();
                        if (table != null) LinkTable(table, civ, used);
                    }
                    break;
                }
                case Msg.RequestClose when CoopSession.IsClient:
                {
                    var table = ItemSync.Get(r.GetInt())?.GetComponent<TableController>();
                    if (table != null && table.myCustomerRequest != null) { table.myCustomerRequest.DestroyForce(); table.myCustomerRequest = null; }
                    break;
                }
                case Msg.RequestAnswer when CoopSession.IsHost:
                {
                    int tid = r.GetInt(); int answer = r.GetInt();
                    var table = ItemSync.Get(tid)?.GetComponent<TableController>();
                    var req = table != null ? table.myCustomerRequest : null;
                    if (req == null) break; // already answered or timed out
                    Log.Info($"coop: {CoopSession.Net.NameOf(from)} {(answer == 0 ? "approved" : "refused")} the PC request on table {tid}");
                    if (table.myCivil == null) req.DestroyForce(); // nobody waiting any more
                    else if (answer == 0) req.Approved();
                    else req.Refuse();
                    break;
                }
                case Msg.CustomerAction when CoopSession.IsHost:
                {
                    int index = r.GetInt(), action = r.GetInt(), arg = r.GetInt();
                    if (!Bind() || index < 0 || index >= s_Civils.Length) break;
                    var c = s_Civils[index];
                    if (action == 1 && c != null && c.myEnum == CivilEnum.PayTime)
                    {
                        Log.Info($"coop: {CoopSession.Net.NameOf(from)} took the cash of customer {index}");
                        c.TakedMoney(arg != 0);
                    }
                    break;
                }
            }
        }

        // ---- tests ----------------------------------------------------------------------------------------
        public static void Report(StringBuilder sb, string detail)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            sb.Append("civils.count=").Append(s_Civils != null ? s_Civils.Length : -1).Append('\n');
            sb.Append("civils.guestSetup=").Append(s_GuestSetup).Append('\n');
            sb.Append("requests.open=").Append(OpenRequestCount()).Append('\n');
            if (s_Civils == null || string.IsNullOrEmpty(detail)) return;
            foreach (var part in detail.Split(','))
            {
                if (!int.TryParse(part, out int i) || i < 0 || i >= s_Civils.Length || s_Civils[i] == null) continue;
                var c = s_Civils[i];
                var p = c.transform.position;
                string k = "civ." + i;
                sb.Append(k).Append(".active=").Append(c.gameObject.activeInHierarchy).Append('\n');
                sb.Append(k).Append(".pos=").Append(p.x.ToString("0.00", inv)).Append(',').Append(p.y.ToString("0.00", inv)).Append(',').Append(p.z.ToString("0.00", inv)).Append('\n');
                sb.Append(k).Append(".enum=").Append(c.myEnum).Append('\n');
                sb.Append(k).Append(".price=").Append(c.payedPrice.ToString("0.##", inv)).Append('\n');
                sb.Append(k).Append(".ai=").Append(c.enabled).Append('\n');
                var names = new List<string>();
                foreach (var prm in s_Params[i].Params) names.Add(prm.name);
                sb.Append(k).Append(".params=").Append(string.Join(",", names)).Append('\n');
                sb.Append(k).Append(".walk=").Append(c.anim != null && c.anim.isActiveAndEnabled && c.anim.GetBool("walk")).Append('\n');
            }
        }

        static int OpenRequestCount()
        {
            int n = 0;
            foreach (var t in FindObjectsOfType<TableController>()) if (t.myCustomerRequest != null) n++;
            return n;
        }
    }
}
