using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Vuplex.WebView;

namespace ICS2iOS
{
    // The in-game PC's "browser" app is Vuplex 3D WebView (Windows Chromium build). Its only use is
    // cheesecakeweb: it opens google.com and has Back / Forward buttons; no game feature depends on it.
    // On iOS this backend is registered instead: the browser window shows a plain page with a
    // "not available" note, and every call is a harmless no-op (MockWebView), so nothing throws.
    public sealed class IosWebPlugin : IWebPlugin
    {
        public static void Register()
        {
            WebPluginFactory.RegisterWindowsPlugin(new IosWebPlugin());
            Log.Info("web view: iOS fallback backend registered (in-game browser shows 'not available')");
        }

        public WebPluginType Type => WebPluginType.iOS;
        public void ClearAllData() { }

        public void CreateTexture(float width, float height, Action<Texture2D> callback)
        {
            int w = Mathf.Clamp(Mathf.RoundToInt(width), 8, 2048), h = Mathf.Clamp(Mathf.RoundToInt(height), 8, 2048);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            FallbackWebView.Paint(tex);
            callback(tex);
        }

        public void CreateMaterial(Action<Material> callback)
        {
            var baseMat = Resources.Load<Material>("DefaultViewportMaterial");
            Material m = baseMat != null ? new Material(baseMat) : new Material(Shader.Find("UI/Default"));
            callback(m);
        }

        public void CreateVideoMaterial(Action<Material> callback) => callback(null);
        public IWebView CreateWebView() => new GameObject("IosFallbackWebView").AddComponent<FallbackWebView>();
        public void EnableRemoteDebugging() { }
        public void SetAutoplayEnabled(bool enabled) { }
        public void SetIgnoreCertificateErrors(bool ignore) { }
        public void SetStorageEnabled(bool enabled) { }
        public void SetUserAgent(bool mobile) { }
        public void SetUserAgent(string userAgent) { }
    }

    public class FallbackWebView : MockWebView
    {
        public static void Paint(Texture2D tex)
        {
            if (tex == null) return;
            var px = new Color32[tex.width * tex.height];
            var bg = new Color32(242, 242, 245, 255);
            for (int i = 0; i < px.Length; i++) px[i] = bg;
            tex.SetPixels32(px);
            tex.Apply(false);
        }

        public override void LoadUrl(string url, Dictionary<string, string> additionalHttpHeaders)
        {
            base.LoadUrl(url, additionalHttpHeaders);
            Paint(Texture);
        }

        public override void LoadHtml(string html)
        {
            base.LoadHtml(html);
            Paint(Texture);
        }
    }

    // Puts a short note over every web view in the scene.
    public static class WebViewNote
    {
        const string Text = "The web browser is not available in the iOS version.";

        public static void OnSceneLoaded()
        {
            foreach (var p in Resources.FindObjectsOfTypeAll<CanvasWebViewPrefab>())
            {
                if (p == null || !p.gameObject.scene.IsValid() || p.transform.Find("ICS2iOS-Note") != null) continue;
                var go = new GameObject("ICS2iOS-Note", typeof(RectTransform));
                go.transform.SetParent(p.transform, false);
                var rt = (RectTransform)go.transform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = rt.offsetMax = Vector2.zero;
                var t = go.AddComponent<Text>();
                t.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                t.text = Text;
                t.color = new Color(0.25f, 0.25f, 0.3f);
                t.alignment = TextAnchor.MiddleCenter;
                t.resizeTextForBestFit = true;
                t.resizeTextMinSize = 10;
                t.resizeTextMaxSize = 40;
                t.raycastTarget = false;
            }
        }
    }
}
