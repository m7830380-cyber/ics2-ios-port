using ICS2Coop;
using LiteNetLib.Utils;
using UnityEngine;

namespace ICS2iOS
{
    // The tutorial's steps and the beggar's and the hobo's quest lines are the world's, not one player's: whoever does a step (kills the
    // target, places the bug, brings the bats, talks to them), it counts for everybody. The game keeps the
    // progress in FBPP (PrefSync shares that) but also in fields and markers it only reads back when the save
    // loads: the quest number, the "!" / "?" markers over their heads, the objective shown on screen, the
    // listening-device spot, the tutorial step (TutorialHelper.lastID: only ever goes forward, so the furthest
    // one wins; many steps are checked only against the step before, so a player left behind never caught up). Those are watched here on every device; a change is sent, a change from
    // elsewhere is applied (last one wins). The host also repeats its state every few seconds, so a guest
    // that just joined (or missed a change) ends up the same.
    public class QuestSync : MonoBehaviour
    {
        const float ScanInterval = 0.25f, RepeatInterval = 3f;

        struct State
        {
            public int Beggar, Hobo, Objective, Tutorial;
            public byte Marks;   // 1 beggar "!", 2 beggar done, 4 hobo "!", 8 hobo done, 16 bug spot shown

            public bool Same(State o) => Beggar == o.Beggar && Hobo == o.Hobo && Objective == o.Objective && Marks == o.Marks && Tutorial == o.Tutorial;
            public override string ToString() => $"tutorial {Tutorial} beggar {Beggar} hobo {Hobo} objective {Objective} marks {Marks}";
        }

        static State s_Known;
        static bool s_HaveKnown, s_GuestSynced;
        static int s_Sent, s_Applied;
        float m_NextScan, m_NextRepeat;

        void OnEnable()
        {
            CoopSession.Received += OnMessage;
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (s, m) => { s_HaveKnown = false; s_GuestSynced = false; };
            CoopSession.Net.Stopped += () => { s_HaveKnown = false; s_GuestSynced = false; };
        }

        static bool Ready => CoopSession.WorldReady && CoopSession.Net.PlayerCount >= 2 &&
                             GameManager.Instance.beggar != null && GameManager.Instance.hobo != null &&
                             GameManager.Instance.objectiveHelper != null;

        void Update()
        {
            if (!Ready) return;
            float now = Time.unscaledTime;
            if (now < m_NextScan) return;
            m_NextScan = now + ScanInterval;
            try
            {
                var s = Capture();
                // a guest's own markers are what its save had: the host's state comes first
                if (CoopSession.IsClient && !s_GuestSynced) return;
                bool repeat = CoopSession.IsHost && now >= m_NextRepeat;
                if (s_HaveKnown && s.Same(s_Known) && !repeat) return;
                if (!s_HaveKnown || !s.Same(s_Known)) Log.Info("coop: quests changed here: " + s);
                s_Known = s;
                s_HaveKnown = true;
                if (CoopSession.IsHost) m_NextRepeat = now + RepeatInterval;
                Send(s, -1);
            }
            catch (System.Exception e) { Log.Error("coop: quest sync: " + e); m_NextScan = now + 5f; }
        }

        static State Capture()
        {
            var gm = GameManager.Instance;
            var b = gm.beggar;
            var h = gm.hobo;
            var s = new State { Beggar = b.questNumber, Hobo = h.hoboQuestNumber, Objective = ObjectiveOf(gm.objectiveHelper) };
            s.Tutorial = gm.tutorialHelper != null ? gm.tutorialHelper.lastID : 0;
            if (On(b.questMark)) s.Marks |= 1;
            if (On(b.completeQuestMark)) s.Marks |= 2;
            if (On(h.hoboQuestMarker)) s.Marks |= 4;
            if (On(h.hoboCompleteQuestMarker)) s.Marks |= 8;
            if (On(gm.beggarQuest5InteractObj)) s.Marks |= 16;
            return s;
        }

        static bool On(GameObject go) => go != null && go.activeSelf;

        /// The number ObjectiveHelper.OpenObjective was last called with (it only shows / hides lines).
        static int ObjectiveOf(ObjectiveHelper o)
        {
            if (!o.gameObject.activeSelf) return 0;
            if (On(o.talktoBeggar)) return -1;
            if (On(o.talktoHobo)) return -2;
            if (On(o.beggarquest2)) return 2;
            if (On(o.beggarquest4)) return 4;
            if (On(o.beggarQuest5)) return 5;
            if (On(o.hoboQuest1)) return 6;
            if (On(o.hoboQuest2)) return 7;
            if (On(o.hoboQuest3)) return 8;
            return 0;
        }

        static void Send(State s, int except)
        {
            var w = CoopSession.Net.Begin(Msg.QuestState);
            w.Put(s.Beggar); w.Put(s.Hobo); w.Put(s.Objective); w.Put(s.Marks); w.Put(s.Tutorial);
            CoopSession.Net.SendAll(LiteNetLib.DeliveryMethod.ReliableOrdered, except);
            s_Sent++;
        }

        void OnMessage(int from, Msg id, NetDataReader r)
        {
            if (id != Msg.QuestState) return;
            var s = new State { Beggar = r.GetInt(), Hobo = r.GetInt(), Objective = r.GetInt(), Marks = r.GetByte(), Tutorial = r.GetInt() };
            if (!Ready) return;   // the host repeats it once this device is up
            try
            {
                if (CoopSession.IsClient) s_GuestSynced = true;
                var here = Capture();
                if (!here.Same(s))
                {
                    Apply(s);
                    s_Applied++;
                    Log.Info($"coop: quests from {CoopSession.Net.NameOf(from)}: {s}");
                }
                s_Known = Capture();
                s_HaveKnown = true;
                // the host passes a guest's step on to the other guests. Still different afterwards: this device's
                // tutorial is further on (it never goes back), so the sender gets that step too.
                bool ahead = !s_Known.Same(s);
                if (CoopSession.IsHost) Send(s_Known, ahead ? -1 : from);
                else if (ahead) Send(s_Known, -1);
            }
            catch (System.Exception e) { Log.Error("coop: applying quests failed: " + e); }
        }

        static void Apply(State s)
        {
            var gm = GameManager.Instance;
            var b = gm.beggar;
            var h = gm.hobo;
            b.questNumber = s.Beggar;
            h.hoboQuestNumber = s.Hobo;
            Set(b.questMark, (s.Marks & 1) != 0);
            Set(b.completeQuestMark, (s.Marks & 2) != 0);
            Set(h.hoboQuestMarker, (s.Marks & 4) != 0);
            Set(h.hoboCompleteQuestMarker, (s.Marks & 8) != 0);
            Set(gm.beggarQuest5InteractObj, (s.Marks & 16) != 0);
            if (ObjectiveOf(gm.objectiveHelper) != s.Objective) gm.objectiveHelper.OpenObjective(s.Objective);
            // the game's own step change (next hint, the sound; the last one ends the tutorial)
            var tut = gm.tutorialHelper;
            if (tut != null && s.Tutorial > tut.lastID) tut.OpenTutorialHelperPanel(s.Tutorial);
        }

        static void Set(GameObject go, bool on) { if (go != null && go.activeSelf != on) go.SetActive(on); }

        public static void Report(System.Text.StringBuilder sb)
        {
            sb.Append("quests.state=").Append(s_HaveKnown ? s_Known.ToString() : "-").Append('\n');
            sb.Append("quests.sent=").Append(s_Sent).Append('\n');
            sb.Append("quests.applied=").Append(s_Applied).Append('\n');
        }
    }
}
