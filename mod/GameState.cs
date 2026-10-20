using System;
using UnityEngine;

namespace ICS2iOS
{
    // What the player is doing right now, read from the game's own objects (see input_map.md):
    // PlayerRay.freezed is the game's "cursor is free" flag (CursorManager locks the mouse when it is false).
    public static class GameState
    {
        public enum Mode { Menu, Walk, Cursor }

        static PlayerRay s_Player;
        static float s_NextSearch;

        public static PlayerRay Player
        {
            get
            {
                if (s_Player == null && Time.unscaledTime >= s_NextSearch)
                {
                    s_NextSearch = Time.unscaledTime + 0.5f;
                    s_Player = UnityEngine.Object.FindObjectOfType<PlayerRay>();
                }
                return s_Player;
            }
        }

        static VHS.InputHandler s_Input;
        static VHS.CameraController s_Camera;
        static float s_NextLookSearch;
        static bool s_LoggedLook;

        /// The "Mouse X"/"Mouse Y" value that turns the player's camera by <paramref name="degrees"/> this frame.
        /// The game multiplies the axis by InputHandler.sensitivity, CameraController.sensitivity and
        /// Time.deltaTime, so those are divided out here: touch look then turns by a fixed angle per point
        /// dragged, whatever the frame rate or the game's own sensitivity values.
        public static float LookAxis(float degrees, bool vertical = false)
        {
            if ((s_Input == null || s_Camera == null) && Time.unscaledTime >= s_NextLookSearch)
            {
                s_NextLookSearch = Time.unscaledTime + 0.5f;
                s_Input = UnityEngine.Object.FindObjectOfType<VHS.InputHandler>();
                s_Camera = UnityEngine.Object.FindObjectOfType<VHS.CameraController>();
            }
            float gain = (s_Input != null ? s_Input.sensitivity : 1.25f) * (s_Camera != null ? (vertical ? s_Camera.sensitivity.y : s_Camera.sensitivity.x) : 100f);
            if (!s_LoggedLook && s_Input != null && s_Camera != null)
            {
                s_LoggedLook = true;
                Log.Info($"look gain: input sensitivity {s_Input.sensitivity}, camera sensitivity {s_Camera.sensitivity}");
            }
            float dt = Mathf.Max(Time.deltaTime, 1f / 240f);
            return degrees / (Mathf.Max(0.01f, Mathf.Abs(gain)) * dt);
        }

        public static Mode Current { get; private set; } = Mode.Menu;
        public static bool Paused { get; private set; }
        public static bool AtComputer { get; private set; }
        public static bool Seated { get; private set; }
        public static bool Building { get; private set; }
        public static bool Holding { get; private set; }
        public static bool PromptE { get; private set; }
        public static bool PromptF { get; private set; }

        static int s_Errors;

        public static void Update()
        {
            try
            {
                var p = Player;
                if (p == null || !p.isActiveAndEnabled)
                {
                    Current = Mode.Menu;
                    Paused = AtComputer = Seated = Building = Holding = PromptE = PromptF = false;
                    return;
                }
                var gm = GameManager.Instance;
                Paused = gm != null && gm.gamePaused;
                Current = p.freezed || Paused ? Mode.Cursor : Mode.Walk;
                AtComputer = p.currentPlayerComputer != null;
                Seated = (p.stripClubSitCameraParent != null && p.stripClubSitCameraParent.activeInHierarchy) ||
                         (p.blackjackSitCameraParent != null && p.blackjackSitCameraParent.activeInHierarchy);
                Building = p.buildable;
                Holding = !string.IsNullOrEmpty(p.takedItemName);

                // The game shows its key hints ("E" / "F") in two bottom-of-screen boxes; they are the most
                // reliable signal for when a key does something.
                bool e = false, f = false;
                var bim = BottomInfoManager.Instance;
                if (bim != null)
                {
                    Scan(bim.bottomInfo, bim.bottomInfoTxt, ref e, ref f);
                    Scan(bim.midInfo, bim.midInfoTxt, ref e, ref f);
                }
                if (p.crosshairOutline != null && p.crosshairOutline.activeInHierarchy) e = true;
                PromptE = e;
                PromptF = f;
            }
            catch (Exception ex)
            {
                if (s_Errors++ < 10) Log.Error("game state: " + ex);
            }
        }

        static void Scan(Transform box, UnityEngine.UI.Text txt, ref bool e, ref bool f)
        {
            if (box == null || txt == null || box.localScale.x < 0.2f || !box.gameObject.activeInHierarchy) return;
            string s = txt.text;
            if (string.IsNullOrEmpty(s)) return;
            if (s.Contains("\"E\"")) e = true;
            if (s.Contains("\"F\"")) f = true;
        }
    }
}
