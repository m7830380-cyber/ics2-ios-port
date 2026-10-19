using System.Collections.Generic;
using ICS2Shim;
using UnityEngine;

namespace ICS2iOS
{
    // Turns raw touches into the virtual mouse the patched game reads (ICS2Shim.VInput).
    //
    // Cursor mode (menus, the in-game computer, shop pages): the first finger is the mouse with the left
    // button held - tap = click, drag = drag. A second finger dragging up/down is the scroll wheel.
    // Touches that start on the overlay (FPS counter / log viewer) belong to it.
    public static class TouchInput
    {
        static int s_Primary = -1, s_Secondary = -1;
        static Vector2 s_LastSecondary;
        static bool s_PrimaryDown;
        static readonly HashSet<int> s_OverlayFingers = new HashSet<int>();

        public static void Tick()
        {
            if (!VInput.Virtual) return;
            TouchSim.Update();
            int n = TouchSim.Count;
            bool primarySeen = false, secondarySeen = false;
            for (int i = 0; i < n; i++)
            {
                Touch t = TouchSim.Get(i);
                if (t.phase == TouchPhase.Began)
                {
                    if (DebugOverlay.TryBeginTouch(t)) { s_OverlayFingers.Add(t.fingerId); continue; }
                    if (s_Primary < 0) { s_Primary = t.fingerId; s_PrimaryDown = false; }
                    else if (s_Secondary < 0) { s_Secondary = t.fingerId; s_LastSecondary = t.position; }
                }
                if (s_OverlayFingers.Contains(t.fingerId))
                {
                    if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled) s_OverlayFingers.Remove(t.fingerId);
                    continue;
                }
                bool ended = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;
                if (t.fingerId == s_Primary)
                {
                    primarySeen = true;
                    VInput.VMousePosition = t.position;
                    if (ended)
                    {
                        // a touch that began and ended between two frames still gets a full click
                        if (!s_PrimaryDown) VInput.TapMouseButton(0);
                        VInput.SetMouseButton(0, false);
                        s_Primary = -1;
                    }
                    else if (s_Secondary < 0)
                    {
                        VInput.SetMouseButton(0, true);
                        s_PrimaryDown = true;
                    }
                }
                else if (t.fingerId == s_Secondary)
                {
                    secondarySeen = true;
                    // two fingers: release the click and scroll instead (1 wheel notch per ~40 points)
                    VInput.SetMouseButton(0, false);
                    float dy = t.position.y - s_LastSecondary.y;
                    s_LastSecondary = t.position;
                    VInput.VScroll += new Vector2(0, dy / Mathf.Max(20f, Screen.dpi * 0.12f));
                    if (ended) s_Secondary = -1;
                }
            }
            // a finger can vanish without an Ended phase (app switch, system gesture)
            if (s_Primary >= 0 && !primarySeen) { VInput.SetMouseButton(0, false); s_Primary = -1; }
            if (s_Secondary >= 0 && !secondarySeen) s_Secondary = -1;
            if (n == 0) s_OverlayFingers.Clear();
        }

        /// Lifts every virtual button (e.g. when the app goes to the background).
        public static void ReleaseAll()
        {
            VInput.SetMouseButton(0, false);
            VInput.SetMouseButton(1, false);
            VInput.SetMouseButton(2, false);
            s_Primary = s_Secondary = -1;
            s_OverlayFingers.Clear();
        }
    }
}
