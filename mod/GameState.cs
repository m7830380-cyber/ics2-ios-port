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
