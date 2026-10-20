using System.Collections.Generic;
using ICS2Coop;
using ICS2Shim;
using LiteNetLib.Utils;
using UnityEngine;

namespace ICS2iOS
{
    // Health is each player's own. A customer who starts a fight (the host runs all customers) goes for the
    // nearest player, local or remote (the patched Civil.Update asks Platform.FightTarget who "the player" is),
    // and its blows land only on that player: on the host's own body as in the game, or as a PlayerHit sent to
    // the guest it hit, whose game takes the damage like a local blow.
    public class FightSync : MonoBehaviour
    {
        const float HitReach = 1.8f;        // Civil.Attacked: _dist <= 1.8
        const float SwitchMargin = 1.5f;    // keep the current victim unless someone else is this much closer

        static readonly Dictionary<Civil, int> s_Victim = new Dictionary<Civil, int>();
        static int s_HitsSent, s_HitsTaken;

        void OnEnable()
        {
            Platform.FightTargetHandler = Target;
            Platform.FightHealthHandler = Health;
            CoopSession.Received += OnMessage;
            CoopSession.Net.Disconnected += _ => s_Victim.Clear();
            CoopSession.Net.Stopped += s_Victim.Clear;
        }

        void OnDisable() => CoopSession.Received -= OnMessage;

        /// Host: the body this fighting customer chases and hits.
        static Transform Target(Transform player, object self)
        {
            var civ = self as Civil;
            if (civ == null || !CoopSession.IsHost) return player;
            if (civ.myEnum != CivilEnum.Fight) { s_Victim.Remove(civ); return player; }
            int local = CoopSession.Net.LocalId;
            var pos = civ.transform.position;
            int best = local;
            var ray = GameManager.Instance != null ? GameManager.Instance.playerRay : null;
            bool localDown = ray != null && (ray.health < 1f || ray.falling);
            float bestDist = player != null && !localDown ? Vector3.Distance(pos, player.position) : float.MaxValue;
            float currentDist = float.MaxValue;
            bool hasCurrent = s_Victim.TryGetValue(civ, out int current);
            if (hasCurrent && current == local) currentDist = bestDist;
            foreach (var kv in PlayerSync.Targets())
            {
                float d = Vector3.Distance(pos, kv.Value.position);
                if (hasCurrent && kv.Key == current) currentDist = d;
                if (d < bestDist) { best = kv.Key; bestDist = d; }
            }
            // stay on the current victim unless it left or someone else is clearly closer
            if (hasCurrent && currentDist < float.MaxValue && currentDist - bestDist < SwitchMargin) best = current;
            s_Victim[civ] = best;
            if (best == local) return player;
            var t = PlayerSync.TargetOf(best);
            return t != null ? t : player;
        }

        /// Host: the health Civil.Update checks to keep fighting: a guest victim's is "up" or "down" (from their
        /// player state), so the host being knocked out no longer ends a fight with a guest, and the reverse.
        static float Health(float health, object self)
        {
            var civ = self as Civil;
            if (civ == null || !CoopSession.IsHost || !s_Victim.TryGetValue(civ, out int victim) || victim == CoopSession.Net.LocalId)
                return health;
            foreach (var kv in PlayerSync.Targets())
                if (kv.Key == victim) return 100f;
            return 0f;
        }

        /// Host, Civil.Attacked (the blow's animation event): true when it went to a guest (the game's own
        /// code, which hits the host's body, is skipped).
        public static bool HostHit(Civil civ)
        {
            if (civ == null || !s_Victim.TryGetValue(civ, out int victim) || victim == CoopSession.Net.LocalId) return false;
            if (civ.myEnum != CivilEnum.Fight) return true;
            var t = PlayerSync.TargetOf(victim);
            if (t == null || Vector3.Distance(civ.transform.position, t.position) > HitReach) return true;
            var w = CoopSession.Net.Begin(Msg.PlayerHit);
            w.Put(civ.transform.position.x); w.Put(civ.transform.position.y); w.Put(civ.transform.position.z);
            CoopSession.Net.SendTo(victim);
            s_HitsSent++;
            return true;
        }

        void OnMessage(int from, Msg id, NetDataReader r)
        {
            if (id != Msg.PlayerHit || from != 0) return;
            var ray = GameManager.Instance != null ? GameManager.Instance.playerRay : null;
            if (ray == null || ray.health <= 0f || ray.falling) return;
            // the same damage as Civil.Attacked, with this player's skills
            int damage = FBPP.GetString("skillbody2") == "opened" ? 10 : FBPP.GetString("skillbody") == "opened" ? 15 : 20;
            if (AudioManager.Instance != null) AudioManager.Instance.PlaySfx(AudioManager.Instance.punchSfx, 0.5f);
            ray.Damage(damage);
            s_HitsTaken++;
        }

        public static void Report(System.Text.StringBuilder sb)
        {
            sb.Append("fight.victims=").Append(s_Victim.Count).Append('\n');
            sb.Append("fight.hitsSent=").Append(s_HitsSent).Append('\n');
            sb.Append("fight.hitsTaken=").Append(s_HitsTaken).Append('\n');
        }
    }
}
