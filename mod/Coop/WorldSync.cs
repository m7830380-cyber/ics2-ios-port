using System.Collections.Generic;
using ICS2Coop;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace ICS2iOS
{
    // Shared money and the shared clock.
    //
    // Money: the game changes GameManager.money directly in ~100 places, so it is synced by watching the
    // value. A guest sends every change it sees (spent at the shop, paid a bill ...) to the host as an amount;
    // the host adds it to its money and answers with the total and the last change it applied. The guest
    // shows that total plus its own changes the host has not confirmed yet, so nothing is lost or counted
    // twice when both players spend at the same moment.
    //
    // Clock: the host's day and hour of day, once a second. The guest's sky keeps running on its own and is
    // pulled toward the host's time, or set outright when it is more than 15 game minutes off.
    public class WorldSync : MonoBehaviour
    {
        const float MoneyInterval = 0.1f, MoneyKeepAlive = 1f, ClockInterval = 1f;

        // host
        readonly Dictionary<int, int> m_Acked = new Dictionary<int, int>();
        float m_LastSentMoney = float.NaN, m_NextMoney, m_NextClock;

        // guest
        static readonly List<KeyValuePair<int, float>> s_Pending = new List<KeyValuePair<int, float>>();
        static int s_Seq;
        static bool s_HaveTotal;
        static float s_Expected;     // what money should be after our own last write
        static float s_HostTotal;

        public static float LastHostHour { get; private set; } = -1f;

        void OnEnable()
        {
            CoopSession.Received += OnMessage;
            CoopSession.Net.PlayerLeft += (id, n) => m_Acked.Remove(id);
        }

        void OnDisable() => CoopSession.Received -= OnMessage;

        public static void ResetGuest()
        {
            s_Pending.Clear();
            s_Seq = 0;
            s_HaveTotal = false;
            LastHostHour = -1f;
        }

        void Update()
        {
            if (!CoopSession.Active) return;
            var gm = GameManager.Instance;
            if (gm == null) return;
            if (CoopSession.IsHost) HostUpdate(gm);
            else GuestUpdate(gm);
        }

        // ---- host ---------------------------------------------------------------------------------------
        void HostUpdate(GameManager gm)
        {
            float now = Time.unscaledTime;
            bool changed = gm.money != m_LastSentMoney;
            if ((changed && now >= m_NextMoney) || now >= m_NextMoney + MoneyKeepAlive)
            {
                m_NextMoney = now + MoneyInterval;
                m_LastSentMoney = gm.money;
                foreach (int p in CoopSession.Net.Players)
                    if (p != 0) SendMoneyState(p, gm.money);
            }
            if (now >= m_NextClock && EnviroSky.instance != null)
            {
                m_NextClock = now + ClockInterval;
                var e = EnviroSky.instance;
                var w = CoopSession.Net.Begin(Msg.Clock);
                w.Put(gm.day);
                w.Put(e.GameTime.Days);
                w.Put(e.GameTime.Years);
                w.Put(e.internalHour);
                CoopSession.Net.SendAll(DeliveryMethod.Sequenced);
            }
        }

        void SendMoneyState(int player, float total)
        {
            var w = CoopSession.Net.Begin(Msg.MoneyState);
            w.Put(total);
            w.Put(m_Acked.TryGetValue(player, out int a) ? a : 0);
            CoopSession.Net.SendTo(player);
        }

        // ---- guest --------------------------------------------------------------------------------------
        static void GuestUpdate(GameManager gm)
        {
            if (!s_HaveTotal) return; // the cafe is still loading; the host's first total starts the sync
            float d = gm.money - s_Expected;
            if (Mathf.Abs(d) < 0.0005f) return;
            int seq = ++s_Seq;
            s_Pending.Add(new KeyValuePair<int, float>(seq, d));
            s_Expected = gm.money;
            var w = CoopSession.Net.Begin(Msg.MoneyDelta);
            w.Put(seq);
            w.Put(d);
            CoopSession.Net.SendTo(0);
            Log.Info($"coop: money change {d:+0.##;-0.##} sent to the host (#{seq})");
        }

        void OnMessage(int from, Msg id, NetDataReader r)
        {
            var gm = GameManager.Instance;
            switch (id)
            {
                case Msg.MoneyDelta when CoopSession.IsHost:
                {
                    int seq = r.GetInt();
                    float d = r.GetFloat();
                    if (m_Acked.TryGetValue(from, out int last) && seq <= last) return; // repeated
                    m_Acked[from] = seq;
                    if (gm == null) return;
                    gm.money += d;
                    ShowChange(gm, d);
                    Log.Info($"coop: {CoopSession.Net.NameOf(from)} money change {d:+0.##;-0.##} -> {gm.money:0.##}");
                    m_NextMoney = 0f; // answer right away
                    break;
                }
                case Msg.MoneyState when CoopSession.IsClient:
                {
                    float total = r.GetFloat();
                    int ack = r.GetInt();
                    float confirmed = 0f, pending = 0f;
                    foreach (var kv in s_Pending) { if (kv.Key <= ack) confirmed += kv.Value; else pending += kv.Value; }
                    s_Pending.RemoveAll(kv => kv.Key <= ack);
                    if (gm == null || UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "MainScene") return;
                    float others = total - s_HostTotal - confirmed; // what the other players earned / spent
                    if (s_HaveTotal && Mathf.Abs(others) > 0.0005f) ShowChange(gm, others);
                    s_HostTotal = total;
                    gm.money = total + pending;
                    s_Expected = gm.money;
                    s_HaveTotal = true;
                    break;
                }
                case Msg.Clock when CoopSession.IsClient:
                    ApplyClock(gm, r.GetInt(), r.GetInt(), r.GetInt(), r.GetFloat());
                    break;
            }
        }

        static void ApplyClock(GameManager gm, int day, int enviroDays, int years, float hour)
        {
            LastHostHour = hour;
            var e = EnviroSky.instance;
            if (gm == null || e == null) return;
            gm.day = day;
            e.GameTime.Days = enviroDays;
            e.GameTime.Years = years;
            float diff = hour - e.internalHour;
            if (diff > 12f) diff -= 24f;
            if (diff < -12f) diff += 24f;
            if (Mathf.Abs(diff) > 0.25f) e.SetInternalTimeOfDay(hour);
            else e.internalHour = Mathf.Repeat(e.internalHour + diff * 0.5f, 24f);
        }

        static void ShowChange(GameManager gm, float d)
        {
            try
            {
                if (d > 0) gm.OpenMoneyAddedUI(d);
                else gm.OpenMoneyDecreaseUI(-d);
            }
            catch (System.Exception e)
            {
                Log.Warn("coop: money popup failed: " + e.Message);
            }
        }

        public static void Report(System.Text.StringBuilder sb)
        {
            sb.Append("coop.guest=").Append(ICS2Shim.Platform.IsCoopGuest).Append('\n');
            sb.Append("money.pending=").Append(s_Pending.Count).Append('\n');
            sb.Append("money.synced=").Append(s_HaveTotal).Append('\n');
        }
    }
}
