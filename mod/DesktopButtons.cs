using ICS2Shim;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ICS2iOS
{
    // Windows (keyboard and mouse): the "Co-op" and "Settings" buttons that the touch HUD shows on the phone,
    // in the main menu and the pause menu. F8 opens the co-op panel from those menus too. The panels close
    // when the game leaves them (Esc back into the cafe), so nothing stays open under a locked cursor.
    public class DesktopButtons : MonoBehaviour
    {
        GUIStyle m_Style;
        EventSystem m_Blocked;
        SettingMenu m_CheckedMenu;
        float m_NextMenuCheck;

        static bool InMenus => GameState.Current == GameState.Mode.Menu || GameState.Paused;

        void Update()
        {
            if (VInput.Virtual) return;
            if (Time.unscaledTime >= m_NextMenuCheck) { m_NextMenuCheck = Time.unscaledTime + 1f; FixResolution(); }
            GameState.Update();
            if (!InMenus) { CoopPanel.Open = false; SettingsPanel.Open = false; }
            else if (Input.GetKeyDown(KeyCode.F8)) CoopPanel.Toggle();
            BlockGameMenu(CoopPanel.Open || SettingsPanel.Open);

            // the settings panel takes touches; the mouse acts as one finger
            if (SettingsPanel.Open)
            {
                var pos = (Vector2)Input.mousePosition;
                if (Input.GetMouseButtonDown(0)) SettingsPanel.HandleTouch(pos, TouchPhase.Began);
                else if (Input.GetMouseButton(0)) SettingsPanel.HandleTouch(pos, TouchPhase.Moved);
                else if (Input.GetMouseButtonUp(0)) SettingsPanel.HandleTouch(pos, TouchPhase.Ended);
            }
        }

        /// While a panel is open, clicks must not also reach the game's menu underneath (e.g. "Main menu").
        void BlockGameMenu(bool block)
        {
            if (block && m_Blocked == null && EventSystem.current != null && EventSystem.current.enabled)
            {
                m_Blocked = EventSystem.current;
                m_Blocked.enabled = false;
            }
            else if (!block && m_Blocked != null)
            {
                m_Blocked.enabled = true;
                m_Blocked = null;
            }
        }

        /// The game's Options "save" stores its resolution number, which starts at 0 (the smallest mode the monitor
        /// lists, e.g. 640x480) unless the player picked one. Starting with a stored 0 then shrinks the window every
        /// time. A stored 0 that is far below the monitor's size (or a number the monitor does not have) is replaced
        /// by the monitor's own; a small mode the player picked on purpose is kept. With none stored, the menu's
        /// number is set to the monitor's so that saving the options keeps it. Written straight to the save: a
        /// hidden Options menu has no list yet (it fills it when it opens, from the save).
        void FixResolution()
        {
            var menu = FindObjectOfType<SettingMenu>(true);
            if (menu == null || menu == m_CheckedMenu) return;
            m_CheckedMenu = menu;
            var list = Screen.resolutions;
            if (list == null || list.Length == 0) return;
            int sw = Display.main.systemWidth, sh = Display.main.systemHeight, best = list.Length - 1;
            for (int i = 0; i < list.Length; i++)
                if (list[i].width == sw && list[i].height == sh) best = i;   // the last one: the highest refresh rate
            if (!FBPP.HasKey("optionresolutionindex")) { menu.SavedresolutionIndex = best; return; }
            int stored = FBPP.GetInt("optionresolutionindex");
            bool bad = stored < 0 || stored >= list.Length || stored == 0 && list[0].width < 1024 && sw >= 1280;
            if (!bad) return;
            Log.Info($"resolution: stored #{stored} is too small for the {sw}x{sh} monitor; using {list[best].width}x{list[best].height}");
            FBPP.SetInt("optionresolutionindex", best);
            FBPP.Save();
            menu.SavedresolutionIndex = best;
            Screen.SetResolution(list[best].width, list[best].height, Screen.fullScreenMode);
            var dd = menu.resolutionDropdown;
            if (dd != null && best < dd.options.Count) { dd.value = best; dd.RefreshShownValue(); }
        }

        void OnGUI()
        {
            if (VInput.Virtual || !InMenus || CoopPanel.Open || SettingsPanel.Open) return;
            float h = Screen.height, pad = h * 0.02f, bh = h * 0.065f, bw = h * 0.22f;
            if (m_Style == null) m_Style = new GUIStyle(GUI.skin.button);
            m_Style.fontSize = Mathf.RoundToInt(h * 0.028f);
            m_Style.fontStyle = FontStyle.Bold;
            GUI.depth = -100;
            float top = h * 0.07f, right = Screen.width - pad;
            var settings = new Rect(right - bw, top, bw, bh);
            var coop = new Rect(settings.x - pad - bw, top, bw, bh);
            if (GUI.Button(coop, "Co-op", m_Style)) CoopPanel.Toggle();
            if (GUI.Button(settings, "Settings", m_Style)) { SettingsPanel.Open = true; CoopPanel.Open = false; }
        }
    }
}
