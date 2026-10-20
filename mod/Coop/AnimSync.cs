using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;

namespace ICS2iOS
{
    // The visible state of one Animator: its bool / int / float parameters and, per layer, the state it is in
    // (or moving to). Triggers are not sent: a trigger only moves the animator into another state, and that
    // state is sent. Without the states, a guest's copy that missed a trigger ("sit", "paymoney", "backsit")
    // stayed in the wrong state and slid around without walking.
    public static class AnimSync
    {
        public sealed class Layout
        {
            public AnimatorControllerParameter[] Params;
            public int Layers;
            public static readonly Layout Empty = new Layout { Params = new AnimatorControllerParameter[0], Layers = 0 };
        }

        public sealed class State
        {
            public int Bools;
            public short[] Ints;
            public float[] Floats;
            public int[] Hashes;
            public float[] Times;      // normalized time in the state (fraction of a loop)
            public bool Valid;
        }

        public static Layout LayoutOf(Animator a)
        {
            if (a == null || a.runtimeAnimatorController == null) return Layout.Empty;
            var list = new List<AnimatorControllerParameter>();
            foreach (var p in a.parameters)
                if (p.type != AnimatorControllerParameterType.Trigger && list.Count < 32) list.Add(p);
            return new Layout { Params = list.ToArray(), Layers = Mathf.Min(a.layerCount, 4) };
        }

        public static State Capture(Animator a, Layout l)
        {
            var s = new State();
            int ni = 0, nf = 0;
            foreach (var p in l.Params)
            {
                if (p.type == AnimatorControllerParameterType.Int) ni++;
                else if (p.type == AnimatorControllerParameterType.Float) nf++;
            }
            s.Ints = new short[ni];
            s.Floats = new float[nf];
            s.Hashes = new int[l.Layers];
            s.Times = new float[l.Layers];
            if (a == null || !a.isActiveAndEnabled || a.runtimeAnimatorController == null) return s;
            s.Valid = true;
            ni = nf = 0;
            for (int k = 0; k < l.Params.Length; k++)
            {
                var p = l.Params[k];
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Bool: if (a.GetBool(p.nameHash)) s.Bools |= 1 << k; break;
                    case AnimatorControllerParameterType.Int: s.Ints[ni++] = (short)a.GetInteger(p.nameHash); break;
                    case AnimatorControllerParameterType.Float: s.Floats[nf++] = a.GetFloat(p.nameHash); break;
                }
            }
            for (int layer = 0; layer < l.Layers; layer++)
            {
                var info = a.IsInTransition(layer) ? a.GetNextAnimatorStateInfo(layer) : a.GetCurrentAnimatorStateInfo(layer);
                s.Hashes[layer] = info.fullPathHash;
                s.Times[layer] = info.loop ? Mathf.Repeat(info.normalizedTime, 1f) : Mathf.Clamp01(info.normalizedTime);
            }
            return s;
        }

        /// Differences worth sending (parameters or states; the time inside a state is not one).
        public static bool Changed(State a, State b)
        {
            if (a == null || a.Valid != b.Valid || a.Bools != b.Bools) return true;
            for (int k = 0; k < a.Ints.Length && k < b.Ints.Length; k++) if (a.Ints[k] != b.Ints[k]) return true;
            for (int k = 0; k < a.Floats.Length && k < b.Floats.Length; k++) if (Mathf.Abs(a.Floats[k] - b.Floats[k]) > 0.05f) return true;
            for (int k = 0; k < a.Hashes.Length && k < b.Hashes.Length; k++) if (a.Hashes[k] != b.Hashes[k]) return true;
            return false;
        }

        /// Parameters only (scene props: their states follow from the parameters on each device).
        public static bool ParamsChanged(State a, State b)
        {
            if (a == null || a.Valid != b.Valid) return b.Valid;
            if (a.Bools != b.Bools) return true;
            for (int k = 0; k < a.Ints.Length && k < b.Ints.Length; k++) if (a.Ints[k] != b.Ints[k]) return true;
            for (int k = 0; k < a.Floats.Length && k < b.Floats.Length; k++) if (Mathf.Abs(a.Floats[k] - b.Floats[k]) > 0.05f) return true;
            return false;
        }

        public static void Write(NetDataWriter w, State s)
        {
            w.Put(s.Valid);
            if (!s.Valid) return;
            w.Put(s.Bools);
            w.Put((byte)s.Ints.Length); foreach (var v in s.Ints) w.Put(v);
            w.Put((byte)s.Floats.Length); foreach (var v in s.Floats) w.Put(v);
            w.Put((byte)s.Hashes.Length);
            for (int k = 0; k < s.Hashes.Length; k++) { w.Put(s.Hashes[k]); w.Put((byte)Mathf.RoundToInt(s.Times[k] * 255f)); }
        }

        public static State Read(NetDataReader r)
        {
            var s = new State { Valid = r.GetBool() };
            if (!s.Valid) { s.Ints = new short[0]; s.Floats = new float[0]; s.Hashes = new int[0]; s.Times = new float[0]; return s; }
            s.Bools = r.GetInt();
            s.Ints = new short[r.GetByte()]; for (int k = 0; k < s.Ints.Length; k++) s.Ints[k] = r.GetShort();
            s.Floats = new float[r.GetByte()]; for (int k = 0; k < s.Floats.Length; k++) s.Floats[k] = r.GetFloat();
            int n = r.GetByte();
            s.Hashes = new int[n]; s.Times = new float[n];
            for (int k = 0; k < n; k++) { s.Hashes[k] = r.GetInt(); s.Times[k] = r.GetByte() / 255f; }
            return s;
        }

        /// Sets the parameters, then moves every layer that is not in (or heading to) the sender's state there.
        public static void Apply(Animator a, Layout l, State s)
        {
            if (a == null || s == null || !s.Valid || a.runtimeAnimatorController == null) return;
            if (!a.enabled) a.enabled = true;
            if (!a.isActiveAndEnabled) return;
            ApplyParams(a, l, s);
            EnforceStates(a, s);
        }

        public static void ApplyParams(Animator a, Layout l, State s)
        {
            if (a == null || s == null || !s.Valid || !a.isActiveAndEnabled) return;
            int ni = 0, nf = 0;
            for (int k = 0; k < l.Params.Length; k++)
            {
                var p = l.Params[k];
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Bool: a.SetBool(p.nameHash, (s.Bools & (1 << k)) != 0); break;
                    case AnimatorControllerParameterType.Int: if (ni < s.Ints.Length) a.SetInteger(p.nameHash, s.Ints[ni++]); break;
                    case AnimatorControllerParameterType.Float: if (nf < s.Floats.Length) a.SetFloat(p.nameHash, s.Floats[nf++]); break;
                }
            }
        }

        /// Only the states (cheap): used between messages so a guest animator that wandered off by itself
        /// (exit-time transitions) comes back.
        public static void EnforceStates(Animator a, State s)
        {
            if (a == null || s == null || !s.Valid || !a.isActiveAndEnabled) return;
            for (int layer = 0; layer < s.Hashes.Length && layer < a.layerCount; layer++)
            {
                int want = s.Hashes[layer];
                if (want == 0) continue;
                bool trans = a.IsInTransition(layer);
                var info = trans ? a.GetNextAnimatorStateInfo(layer) : a.GetCurrentAnimatorStateInfo(layer);
                if (info.fullPathHash == want) continue;
                if (!a.HasState(layer, want)) continue;
                a.CrossFade(want, 0.12f, layer, s.Times[layer]);
            }
        }
    }
}
