using ICS2Shim;
using UnityEngine;

namespace ICS2iOS
{
    // Draws the touch HUD (buttons, movement stick, controller pointer) with IMGUI on top of the game.
    public class HudView : MonoBehaviour
    {
        static Texture2D s_Disc, s_Round;
        GUIStyle m_Label, m_Rounded;

        static Texture2D Disc(int size, float radiusFrac, bool ring)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            float c = (size - 1) * 0.5f, r = size * 0.5f * radiusFrac;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                float a = Mathf.Clamp01(r - d);
                if (ring) a *= Mathf.Clamp01(d - (r - size * 0.06f));
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        void Init()
        {
            s_Disc = Disc(128, 1f, false);
            s_Round = Disc(64, 1f, false);
            m_Label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = false };
            m_Rounded = new GUIStyle { border = new RectOffset(31, 31, 31, 31), normal = { background = s_Round } };
        }

        void OnApplicationPause(bool paused)
        {
            if (paused) TouchInput.ReleaseAll();
        }

        void OnGUI()
        {
            if (!VInput.Virtual || Event.current.type != EventType.Repaint) return;
            if (s_Disc == null) Init();
            GUI.depth = -100;
            float a = IosSettings.HudOpacity;
            float u = TouchHud.Unit;

            if (TouchInput.StickActive)
            {
                Vector2 o = new Vector2(TouchInput.StickOrigin.x, Screen.height - TouchInput.StickOrigin.y);
                Vector2 k = new Vector2(TouchInput.StickKnob.x, Screen.height - TouchInput.StickKnob.y);
                float r = TouchInput.StickRadius;
                Vector2 d = Vector2.ClampMagnitude(k - o, r);
                GUI.color = new Color(1, 1, 1, a * 0.5f);
                GUI.DrawTexture(new Rect(o.x - r, o.y - r, 2 * r, 2 * r), s_Disc);
                GUI.color = new Color(1, 1, 1, Mathf.Min(1f, a * 1.6f));
                float kr = r * 0.45f;
                GUI.DrawTexture(new Rect(o.x + d.x - kr, o.y + d.y - kr, 2 * kr, 2 * kr), s_Disc);
            }

            foreach (var b in TouchHud.All)
            {
                if (!b.Visible) continue;
                var r = b.Rect;
                Color bg = b.On ? new Color(1f, 0.85f, 0.3f) : b.Accent ? new Color(0.35f, 0.75f, 1f) : new Color(0.1f, 0.1f, 0.12f);
                GUI.color = new Color(bg.r, bg.g, bg.b, b.On ? Mathf.Min(1f, a + 0.3f) : a);
                bool square = Mathf.Abs(r.width - r.height) < 1f;
                if (square) GUI.DrawTexture(r, s_Disc);
                else GUI.Box(r, GUIContent.none, m_Rounded);
                m_Label.fontSize = Mathf.RoundToInt(Mathf.Min(r.height * (b.Label.Contains("\n") ? 0.26f : 0.34f), u * 0.3f));
                GUI.color = new Color(0, 0, 0, Mathf.Min(1f, a + 0.2f));
                GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), b.Label, m_Label);
                GUI.color = new Color(1, 1, 1, Mathf.Min(1f, a + 0.45f));
                GUI.Label(r, b.Label, m_Label);
            }

            if (Controller.ShowCursor && GameState.Current != GameState.Mode.Walk)
            {
                Vector3 m = VInput.VMousePosition;
                float s = u * 0.22f;
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(m.x - s / 2, Screen.height - m.y - s / 2, s, s), s_Disc);
            }
            GUI.color = Color.white;
        }
    }
}
