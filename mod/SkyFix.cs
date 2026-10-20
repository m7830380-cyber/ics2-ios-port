using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ICS2iOS
{
    // Enviro draws the sky with a command buffer that blits the camera target onto itself
    // (Blit(CameraTarget, CameraTarget, skyMaterial)) before the opaque pass. Direct3D tolerates that, on
    // Metal the read-while-writing is undefined and the sky keeps the previous frames ("hall of mirrors").
    // The sky shader never reads its source, so the buffer is replaced by one that blits a black texture
    // instead - same result, no feedback. Enviro removes buffers by name, so ours keeps its name.
    public class SkyFix : MonoBehaviour
    {
        const string BufferName = "Enviro Sky Rendering";
        static readonly CameraEvent[] Events = { CameraEvent.BeforeForwardOpaque, CameraEvent.BeforeGBuffer };
        readonly HashSet<CommandBuffer> m_Ours = new HashSet<CommandBuffer>();
        float m_NextCheck;

        void LateUpdate()
        {
            if (Time.unscaledTime < m_NextCheck) return;
            m_NextCheck = Time.unscaledTime + 0.5f;
            foreach (var r in FindObjectsOfType<EnviroSkyRendering>())
            {
                var cam = r.GetComponent<Camera>();
                if (cam == null || r.material == null) continue;
                foreach (var evt in Events)
                {
                    foreach (var cb in cam.GetCommandBuffers(evt))
                    {
                        if (cb.name != BufferName || m_Ours.Contains(cb)) continue;
                        cam.RemoveCommandBuffer(evt, cb);
                        var fixedCb = new CommandBuffer { name = BufferName };
                        fixedCb.Blit(Texture2D.blackTexture, BuiltinRenderTextureType.CameraTarget, r.material);
                        cam.AddCommandBuffer(evt, fixedCb);
                        m_Ours.Add(fixedCb);
                        Log.Info($"sky: replaced Enviro sky blit on camera '{cam.name}' ({evt}, path={cam.actualRenderingPath}, " +
                                 $"clear={cam.clearFlags}, hdr={cam.allowHDR}, msaa={cam.allowMSAA})");
                    }
                }
            }
        }
    }
}
