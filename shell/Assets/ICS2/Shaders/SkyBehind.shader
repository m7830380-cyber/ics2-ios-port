// Draws a full-screen texture (the Enviro sky) only where the camera saw nothing (depth at the far plane).
// Used by the iOS mod after the deferred lighting pass, instead of Enviro's own sky blit before the G-buffer,
// which Metal does not keep (old frames showed through the sky).
Shader "Hidden/ICS2/SkyBehind"
{
    Properties { _MainTex ("Sky", 2D) = "black" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 uvDepth : TEXCOORD1;
            };

            v2f vert(appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord.xy;
                o.uvDepth = v.texcoord.xy;
                #if UNITY_UV_STARTS_AT_TOP
                if (_MainTex_TexelSize.y < 0) o.uv.y = 1 - o.uv.y;
                #endif
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                float d = Linear01Depth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uvDepth));
                clip(d - 0.9999);
                return tex2D(_MainTex, i.uv);
            }
            ENDCG
        }
    }
}
