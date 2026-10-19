using System;
using ICS2Shim;
using UnityEngine;

namespace ICS2iOS
{
    // MFi / Xbox / PlayStation controllers (Unity's legacy iOS mapping). Buttons are KeyCode.JoystickButtonN:
    //   pause 0, d-pad up/right/down/left 4/5/6/7, L1 8, R1 9, L2 10, R2 11, Y 12, B 13, A 14, X 15.
    // Sticks are read through the "ICS2 Joy ..." axes the data converter adds to the InputManager.
    //
    // Walking: left stick move, right stick look, A jump, B crouch, X use (E), Y secondary (F), R2 hit/place,
    //          L2 drop/cancel, L1 sprint (hold), R1 inventory, d-pad up map / down stats / left+right rotate,
    //          menu = pause.
    // Cursor:  left stick moves the pointer, A click, B back (Esc), X leave (Q), Y zoom (F), right stick scrolls.
    public static class Controller
    {
        public static bool InUse { get; private set; }
        static bool s_AxesOk = true, s_Logged;
        static bool s_StickWasActive, s_CursorStarted;
        static readonly bool[] s_Prev = new bool[20];

        struct Map { public int Button; public KeyCode Walk; public KeyCode Cursor; }
        static readonly Map[] s_Maps =
        {
            new Map { Button = 0, Walk = KeyCode.Escape, Cursor = KeyCode.Escape },
            new Map { Button = 14, Walk = KeyCode.Space, Cursor = KeyCode.Mouse0 },
            new Map { Button = 13, Walk = KeyCode.LeftControl, Cursor = KeyCode.Escape },
            new Map { Button = 15, Walk = KeyCode.E, Cursor = KeyCode.Q },
            new Map { Button = 12, Walk = KeyCode.F, Cursor = KeyCode.F },
            new Map { Button = 11, Walk = KeyCode.Mouse0, Cursor = KeyCode.Mouse0 },
            new Map { Button = 10, Walk = KeyCode.Mouse1, Cursor = KeyCode.Mouse1 },
            new Map { Button = 8, Walk = KeyCode.LeftShift, Cursor = KeyCode.None },
            new Map { Button = 9, Walk = KeyCode.I, Cursor = KeyCode.None },
            new Map { Button = 4, Walk = KeyCode.M, Cursor = KeyCode.None },
            new Map { Button = 6, Walk = KeyCode.LeftAlt, Cursor = KeyCode.None },
            new Map { Button = 7, Walk = KeyCode.Q, Cursor = KeyCode.None },
            new Map { Button = 5, Walk = KeyCode.R, Cursor = KeyCode.None },
        };
        static readonly KeyCode[] s_Held = new KeyCode[20];

        static float Axis(string name)
        {
            if (!s_AxesOk) return 0f;
            try { return Input.GetAxisRaw(name); }
            catch (ArgumentException)
            {
                s_AxesOk = false;
                Log.Warn("controller sticks unavailable (InputManager has no ICS2 joystick axes); buttons still work");
                return 0f;
            }
        }

        static Vector2 Stick(string x, string y, float dead)
        {
            // legacy joystick Y is positive downwards
            var v = new Vector2(Axis(x), -Axis(y));
            float m = v.magnitude;
            if (m < dead) return Vector2.zero;
            return v / m * Mathf.Clamp01((m - dead) / (1f - dead));
        }

        public static void Tick(GameState.Mode mode)
        {
            bool any = false;
            for (int i = 0; i < s_Maps.Length; i++)
            {
                var m = s_Maps[i];
                bool down = Input.GetKey(KeyCode.JoystickButton0 + m.Button);
                any |= down;
                KeyCode want = mode == GameState.Mode.Walk ? m.Walk : m.Cursor;
                if (down && !s_Prev[i]) { s_Held[i] = want; Set(want, true); }
                else if (!down && s_Prev[i]) { Set(s_Held[i], false); s_Held[i] = KeyCode.None; }
                s_Prev[i] = down;
            }

            Vector2 l = Stick("ICS2 Joy LX", "ICS2 Joy LY", 0.2f), r = Stick("ICS2 Joy RX", "ICS2 Joy RY", 0.15f);
            bool sticks = l != Vector2.zero || r != Vector2.zero;
            any |= sticks;
            if (any && !s_Logged)
            {
                s_Logged = true;
                var names = Input.GetJoystickNames();
                Log.Info("game controller in use: " + (names.Length > 0 ? string.Join(", ", names) : "?"));
            }
            if (any) InUse = true;
            if (TouchSim.Count > 0) InUse = false;

            float dt = Time.unscaledDeltaTime;
            if (mode == GameState.Mode.Walk)
            {
                s_CursorStarted = false;
                if (l != Vector2.zero || s_StickWasActive)
                {
                    VInput.SetAxis("Horizontal", l.x);
                    VInput.SetAxis("Vertical", l.y);
                }
                s_StickWasActive = l != Vector2.zero;
                if (r != Vector2.zero)
                {
                    // ~180 degrees/s at full tilt with the default sensitivity (same scale as touch look)
                    float k = 9f * IosSettings.LookSensitivity;
                    VInput.AddFrameAxis("Mouse X", r.x * r.magnitude * k);
                    VInput.AddFrameAxis("Mouse Y", r.y * r.magnitude * k * (IosSettings.InvertLook ? -1f : 1f));
                }
            }
            else
            {
                if (s_StickWasActive) { VInput.SetAxis("Horizontal", 0); VInput.SetAxis("Vertical", 0); s_StickWasActive = false; }
                if (l != Vector2.zero)
                {
                    if (!s_CursorStarted) { s_CursorStarted = true; VInput.VMousePosition = new Vector3(Screen.width / 2f, Screen.height / 2f); }
                    float speed = Screen.height * 1.1f;
                    Vector3 p = VInput.VMousePosition + (Vector3)(l * l.magnitude * speed * dt);
                    VInput.VMousePosition = new Vector3(Mathf.Clamp(p.x, 0, Screen.width - 1), Mathf.Clamp(p.y, 0, Screen.height - 1));
                }
                if (r.y != 0) VInput.VScroll += new Vector2(0, r.y * dt * 12f);
            }
        }

        public static bool ShowCursor => InUse && s_CursorStarted;

        static void Set(KeyCode k, bool held)
        {
            if (k == KeyCode.None) return;
            if (k >= KeyCode.Mouse0 && k <= KeyCode.Mouse2) VInput.SetMouseButton(k - KeyCode.Mouse0, held);
            else VInput.SetKey(k, held);
        }
    }
}
