using System;
using System.Collections.Generic;
using ICS2Shim;
using UnityEngine;

namespace ICS2iOS
{
    // On-screen buttons. Every button presses a real game key / mouse button through ICS2Shim.VInput, so
    // the game reacts exactly as it does on PC. Layout (landscape, inside the safe area):
    //   top-left    : menu row (pause, inventory, map, skills, stats) and the hide/show toggle
    //   bottom-left : sprint toggle above the floating movement stick zone
    //   bottom-right: action cluster (hit/place, use E, secondary F, drop/cancel, jump, crouch, rotate)
    // In cursor mode (menus, the in-game PC) only Back (Esc) and, when seated, Leave (Q) are shown.
    public sealed class HudButton
    {
        public string Label;
        public KeyCode Key;
        public int Mouse = -1;
        public bool Toggle;
        public Action OnPress;
        public float Size = 1f;             // in HUD units
        public Rect Rect;                   // GUI coordinates (origin top-left)
        public bool Visible;
        public bool On;                     // held (or toggled on)
        public int Finger = -1;
        public bool Accent;

        public void Press()
        {
            if (OnPress != null) { OnPress(); return; }
            if (Toggle) On = !On; else On = true;
            Apply();
        }

        public void Release()
        {
            Finger = -1;
            if (Toggle || OnPress != null) return;
            On = false;
            Apply();
        }

        public void ForceOff()
        {
            Finger = -1;
            if (!On) return;
            On = false;
            if (OnPress == null) Apply();
        }

        void Apply()
        {
            if (Mouse >= 0) VInput.SetMouseButton(Mouse, On);
            else if (Key != KeyCode.None) VInput.SetKey(Key, On);
        }
    }

    public static class TouchHud
    {
        public static readonly HudButton Pause = new HudButton { Label = "II", Key = KeyCode.Escape, Size = 0.75f };
        public static readonly HudButton Inventory = new HudButton { Label = "Bag", Key = KeyCode.I, Size = 0.75f };
        public static readonly HudButton Map = new HudButton { Label = "Map", Key = KeyCode.M, Size = 0.75f };
        public static readonly HudButton Skills = new HudButton { Label = "Skills", Key = KeyCode.X, Size = 0.75f };
        public static readonly HudButton Stats = new HudButton { Label = "Stats", Key = KeyCode.LeftAlt, Size = 0.75f };
        public static readonly HudButton Hide = new HudButton { Label = "Hide", Size = 0.75f };

        public static readonly HudButton Sprint = new HudButton { Label = "Run", Key = KeyCode.LeftShift, Toggle = true };

        public static readonly HudButton Hit = new HudButton { Label = "Hit", Mouse = 0, Size = 1.35f };
        public static readonly HudButton Use = new HudButton { Label = "E\nUse", Key = KeyCode.E, Size = 1.15f, Accent = true };
        public static readonly HudButton Alt = new HudButton { Label = "F", Key = KeyCode.F, Size = 1f, Accent = true };
        public static readonly HudButton Drop = new HudButton { Label = "Drop", Mouse = 1 };
        public static readonly HudButton Jump = new HudButton { Label = "Jump", Key = KeyCode.Space };
        public static readonly HudButton Crouch = new HudButton { Label = "Crouch", Key = KeyCode.LeftControl, Size = 0.85f };
        public static readonly HudButton RotL = new HudButton { Label = "<", Key = KeyCode.Q, Size = 0.9f };
        public static readonly HudButton RotR = new HudButton { Label = ">", Key = KeyCode.R, Size = 0.9f };

        public static readonly HudButton Back = new HudButton { Label = "Back", Key = KeyCode.Escape, Size = 0.8f };
        public static readonly HudButton Leave = new HudButton { Label = "Leave\n(Q)", Key = KeyCode.Q, Size = 0.9f };
        public static readonly HudButton Zoom = new HudButton { Label = "Zoom\n(F)", Key = KeyCode.F, Size = 0.9f };
        public static readonly HudButton Power = new HudButton { Label = "Power", Key = KeyCode.Space, Size = 0.9f };
        public static readonly HudButton Gear = new HudButton { Label = "iOS", Size = 0.75f };
        public static readonly HudButton Coop = new HudButton { Label = "Co-op", Size = 0.75f };

        public static readonly HudButton[] All =
        {
            Pause, Inventory, Map, Skills, Stats, Hide, Sprint, Hit, Use, Alt, Drop, Jump, Crouch, RotL, RotR,
            Back, Leave, Zoom, Power, Gear, Coop,
        };

        public static float Unit { get; private set; }
        public static Rect Safe { get; private set; }     // GUI coordinates

        static TouchHud()
        {
            Hide.OnPress = () => { IosSettings.HudHidden = !IosSettings.HudHidden; IosSettings.Save(); };
            Gear.OnPress = () => { SettingsPanel.Open = !SettingsPanel.Open; if (SettingsPanel.Open) CoopPanel.Open = false; };
            Coop.OnPress = CoopPanel.Toggle;
        }

        /// Decides which buttons are shown and where, for the current game state.
        public static void Layout()
        {
            Rect s = Screen.safeArea;
            float H = Screen.height;
            Safe = new Rect(s.x, H - s.yMax, s.width, s.height);
            Unit = Mathf.Min(Screen.height, Screen.width) * 0.115f * IosSettings.HudScale;
            float u = Unit, pad = u * 0.18f;

            foreach (var b in All) b.Visible = false;
            var mode = GameState.Current;
            bool hidden = IosSettings.HudHidden || Controller.InUse;

            if (mode == GameState.Mode.Walk)
            {
                Hide.Visible = true;
                Hide.Label = hidden ? "Show" : "Hide";
                if (!hidden)
                {
                    Pause.Visible = Inventory.Visible = Map.Visible = Skills.Visible = Stats.Visible = true;
                    Sprint.Visible = true;
                    Hit.Visible = true;
                    Hit.Label = GameState.Building ? "Place" : "Hit";
                    Use.Visible = GameState.PromptE;
                    Alt.Visible = GameState.PromptF;
                    Drop.Visible = GameState.Holding || GameState.Building;
                    Drop.Label = GameState.Building ? "Cancel" : "Drop";
                    Jump.Visible = Crouch.Visible = !GameState.Building;
                    RotL.Visible = RotR.Visible = GameState.Building;
                }
                // top-left menu row
                float x = Safe.xMin + pad, y = Safe.yMin + pad;
                foreach (var b in new[] { Pause, Inventory, Map, Skills, Stats, Hide })
                {
                    if (!b.Visible) continue;
                    float w = u * b.Size * 1.25f, h = u * b.Size * 0.7f;
                    b.Rect = new Rect(x, y, w, h);
                    x += w + pad * 0.6f;
                }
                // sprint: above the movement zone
                Sprint.Rect = Square(Safe.xMin + pad + u * 0.2f, Safe.yMax - pad - u * 3.1f, u * Sprint.Size);
                // action cluster, anchored at the bottom-right corner
                float r = Safe.xMax - pad, bot = Safe.yMax - pad;
                Hit.Rect = Square(r - u * Hit.Size, bot - u * Hit.Size, u * Hit.Size);
                Jump.Rect = Square(Hit.Rect.x - pad - u * Jump.Size, bot - u * Jump.Size, u * Jump.Size);
                RotR.Rect = Square(Hit.Rect.x - pad - u * RotR.Size, bot - u * RotR.Size, u * RotR.Size);
                RotL.Rect = Square(RotR.Rect.x - pad - u * RotL.Size, bot - u * RotL.Size, u * RotL.Size);
                Crouch.Rect = Square(Jump.Rect.x - pad - u * Crouch.Size, bot - u * Crouch.Size, u * Crouch.Size);
                Use.Rect = Square(r - u * Use.Size, Hit.Rect.y - pad - u * Use.Size, u * Use.Size);
                Alt.Rect = Square(Use.Rect.x - pad - u * Alt.Size, Use.Rect.y + (Use.Rect.height - u * Alt.Size) * 0.5f, u * Alt.Size);
                Drop.Rect = Square(r - u * Drop.Size, Use.Rect.y - pad - u * Drop.Size, u * Drop.Size);
            }
            else
            {
                bool inGame = mode == GameState.Mode.Cursor;
                // Back = Esc (closes the open panel / pause menu). Hidden on the main menu, which has its own buttons.
                Back.Visible = inGame;
                Leave.Visible = inGame && !GameState.Paused && (GameState.AtComputer || GameState.Seated);
                Zoom.Visible = Power.Visible = inGame && !GameState.Paused && GameState.AtComputer;
                Gear.Visible = Coop.Visible = mode == GameState.Mode.Menu || GameState.Paused;
                Back.Rect = new Rect(Safe.xMin + pad, Safe.yMin + pad, u * 1.2f * Back.Size, u * 0.65f * Back.Size);
                Gear.Rect = new Rect(Safe.xMin + pad, Safe.yMax - pad - u * 0.6f, u * 1.0f, u * 0.6f);
                Coop.Rect = new Rect(Gear.Rect.xMax + pad, Gear.Rect.y, u * 1.3f, u * 0.6f);
                float y = Back.Rect.yMax + pad;
                foreach (var b in new[] { Leave, Zoom, Power })
                {
                    if (!b.Visible) continue;
                    b.Rect = new Rect(Safe.xMin + pad, y, u * 1.2f * b.Size, u * 0.75f * b.Size);
                    y += b.Rect.height + pad * 0.6f;
                }
            }
        }

        static Rect Square(float x, float y, float s) => new Rect(x, y, s, s);

        /// The visible button under a GUI-space point (slightly enlarged hit areas).
        public static HudButton HitTest(Vector2 gui)
        {
            float slop = Unit * 0.08f;
            foreach (var b in All)
            {
                if (!b.Visible) continue;
                var r = b.Rect;
                if (new Rect(r.x - slop, r.y - slop, r.width + 2 * slop, r.height + 2 * slop).Contains(gui)) return b;
            }
            return null;
        }

        public static void ReleaseAll(bool includingToggles)
        {
            foreach (var b in All)
            {
                if (b.Toggle && !includingToggles) { b.Finger = -1; continue; }
                b.ForceOff();
            }
        }
    }
}
