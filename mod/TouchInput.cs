using System.Collections.Generic;
using ICS2Shim;
using UnityEngine;

namespace ICS2iOS
{
    // Turns raw touches into the virtual mouse / keys / axes the patched game reads (ICS2Shim.VInput).
    //
    // Every new finger is offered, in order, to: the overlay (FPS counter / log viewer), the iOS settings
    // panel, the on-screen buttons (TouchHud). What is left depends on the mode:
    //   Walk   (cursor locked in the game): left 40% of the screen = floating movement stick,
    //          the rest = drag to look ("Mouse X"/"Mouse Y").
    //   Cursor (menus, inventory, the in-game PC, main menu): the first finger is the mouse with the left
    //          button held - tap = click, drag = drag. A second finger dragging up/down is the scroll wheel.
    public static class TouchInput
    {
        enum Role { None, Overlay, Panel, Button, Stick, Look, Mouse, Scroll }

        sealed class Finger
        {
            public Role Role;
            public HudButton Button;
            public Vector2 Start, Last;
        }

        static readonly Dictionary<int, Finger> s_Fingers = new Dictionary<int, Finger>();
        static readonly HashSet<int> s_Seen = new HashSet<int>();
        static readonly List<int> s_Gone = new List<int>();
        static GameState.Mode s_LastMode = GameState.Mode.Menu;
        static bool s_MouseDown;

        // movement stick (screen pixels, origin bottom-left)
        public static bool StickActive { get; private set; }
        public static Vector2 StickOrigin { get; private set; }
        public static Vector2 StickKnob { get; private set; }
        public static float StickRadius => TouchHud.Unit * 0.9f;

        public static void Tick()
        {
            if (!VInput.Virtual) return;
            GameState.Update();
            var mode = GameState.Current;
            if (mode != s_LastMode)
            {
                // the game switched between walking and cursor (sat at a PC, opened a menu, ...):
                // drop whatever the fingers were doing so nothing stays pressed
                Log.Info($"input mode {s_LastMode} -> {mode}");
                ResetFingers(mode != GameState.Mode.Walk);
                s_LastMode = mode;
            }
            TouchHud.Layout();
            Controller.Tick(mode);

            if (mode == GameState.Mode.Walk)
                VInput.VMousePosition = new Vector3(Screen.width * 0.5f, Screen.height * 0.5f);

            TouchSim.Update();
            int n = TouchSim.Count;
            s_Seen.Clear();
            for (int i = 0; i < n; i++)
            {
                Touch t = TouchSim.Get(i);
                s_Seen.Add(t.fingerId);
                if (t.phase == TouchPhase.Began || !s_Fingers.ContainsKey(t.fingerId))
                    Begin(t, mode);
                var f = s_Fingers[t.fingerId];
                bool ended = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;
                Move(t, f, ended);
                if (ended) End(t.fingerId, f);
            }
            // a finger can vanish without an Ended phase (app switch, system gesture)
            s_Gone.Clear();
            foreach (var kv in s_Fingers) if (!s_Seen.Contains(kv.Key)) s_Gone.Add(kv.Key);
            foreach (int id in s_Gone) End(id, s_Fingers[id]);
        }

        static Vector2 Gui(Vector2 p) => new Vector2(p.x, Screen.height - p.y);

        static void Begin(Touch t, GameState.Mode mode)
        {
            var f = new Finger { Start = t.position, Last = t.position };
            s_Fingers[t.fingerId] = f;
            if (DebugOverlay.TryBeginTouch(t)) { f.Role = Role.Overlay; return; }
            if (SettingsPanel.Open) { f.Role = Role.Panel; SettingsPanel.HandleTouch(t.position, TouchPhase.Began); return; }
            var b = TouchHud.HitTest(Gui(t.position));
            if (b != null && b.Finger < 0)
            {
                f.Role = Role.Button;
                f.Button = b;
                b.Finger = t.fingerId;
                b.Press();
                return;
            }
            if (mode == GameState.Mode.Walk)
            {
                if (t.position.x < Screen.width * 0.4f && !StickActive)
                {
                    f.Role = Role.Stick;
                    StickActive = true;
                    StickOrigin = StickKnob = t.position;
                }
                else f.Role = Role.Look;
                return;
            }
            // cursor mode
            bool hasMouse = false;
            foreach (var o in s_Fingers.Values) if (o != f && o.Role == Role.Mouse) hasMouse = true;
            if (!hasMouse) { f.Role = Role.Mouse; s_MouseDown = false; }
            else
            {
                f.Role = Role.Scroll;
                VInput.SetMouseButton(0, false);   // two fingers: scroll instead of dragging
            }
        }

        static void Move(Touch t, Finger f, bool ended)
        {
            Vector2 p = t.position;
            switch (f.Role)
            {
                case Role.Stick:
                {
                    Vector2 d = p - StickOrigin;
                    float r = StickRadius;
                    if (d.magnitude > r)
                    {
                        // floating stick: the base follows the thumb
                        StickOrigin = p - d.normalized * r;
                        d = p - StickOrigin;
                    }
                    StickKnob = p;
                    Vector2 v = d / r;
                    VInput.SetAxis("Horizontal", v.x);
                    VInput.SetAxis("Vertical", v.y);
                    break;
                }
                case Role.Look:
                {
                    Vector2 d = p - f.Last;
                    // pixels -> points, then scaled so the game's own sensitivity (and its Time.deltaTime
                    // factor) still apply: the result is a rotation per point that does not depend on the fps
                    float points = Mathf.Max(1f, Screen.dpi / 163f);
                    float dt = Mathf.Max(Time.unscaledDeltaTime, 1f / 240f);
                    float k = 0.2f * IosSettings.LookSensitivity / (points * dt * 60f);
                    VInput.AddFrameAxis("Mouse X", d.x * k);
                    VInput.AddFrameAxis("Mouse Y", d.y * k * (IosSettings.InvertLook ? -1f : 1f));
                    break;
                }
                case Role.Mouse:
                    VInput.VMousePosition = p;
                    if (ended)
                    {
                        // a touch that began and ended between two frames still gets a full click
                        if (!s_MouseDown) VInput.TapMouseButton(0);
                    }
                    else if (!AnyRole(Role.Scroll))
                    {
                        VInput.SetMouseButton(0, true);
                        s_MouseDown = true;
                    }
                    break;
                case Role.Panel:
                    if (t.phase != TouchPhase.Began) SettingsPanel.HandleTouch(p, ended ? TouchPhase.Ended : TouchPhase.Moved);
                    break;
                case Role.Scroll:
                {
                    float dy = p.y - f.Last.y;
                    VInput.VScroll += new Vector2(0, dy / Mathf.Max(20f, Screen.dpi * 0.12f));
                    break;
                }
            }
            f.Last = p;
        }

        static void End(int id, Finger f)
        {
            s_Fingers.Remove(id);
            switch (f.Role)
            {
                case Role.Button:
                    if (f.Button != null && f.Button.Finger == id) f.Button.Release();
                    break;
                case Role.Stick:
                    StickActive = false;
                    VInput.SetAxis("Horizontal", 0);
                    VInput.SetAxis("Vertical", 0);
                    break;
                case Role.Mouse:
                    VInput.SetMouseButton(0, false);
                    break;
            }
        }

        static bool AnyRole(Role r)
        {
            foreach (var f in s_Fingers.Values) if (f.Role == r) return true;
            return false;
        }

        static void ResetFingers(bool includingToggles)
        {
            // fingers still on the screen are ignored until lifted
            foreach (var f in s_Fingers.Values) { f.Role = Role.None; f.Button = null; }
            StickActive = false;
            VInput.SetAxis("Horizontal", 0);
            VInput.SetAxis("Vertical", 0);
            VInput.SetMouseButton(0, false);
            VInput.SetMouseButton(1, false);
            TouchHud.ReleaseAll(includingToggles);
        }

        /// Lifts every virtual button (e.g. when the app goes to the background).
        public static void ReleaseAll()
        {
            ResetFingers(true);
            s_Fingers.Clear();
            VInput.SetMouseButton(2, false);
        }
    }
}
