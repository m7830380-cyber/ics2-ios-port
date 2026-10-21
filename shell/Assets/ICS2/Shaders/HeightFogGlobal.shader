// The game's BOXOPHOBIC Atmospheric Height Fog (the camera-centred fog sphere), compiled natively for Metal.
// Same maths as the game's shader (rebuilt from its compiled code); the only change is the sky test: the
// original treats a pixel as sky only when the depth is exactly the far plane, which on iPhone left the
// whole sky fogged grey ("full distance fog at the far plane"). Here anything at the far plane is sky and
// gets the game's horizon sky fog instead.
Shader "Hidden/ICS2/HeightFogGlobal"
{
    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Overlay" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Front
            ZWrite Off
            ZTest Always
            Stencil { Ref 222 Comp NotEqual Pass Replace }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            float4 AHF_FogColorStart, AHF_FogColorEnd, AHF_DirectionalColor;
            float AHF_FogDistanceStart, AHF_FogDistanceEnd, AHF_FogDistanceFalloff, AHF_FogColorDuo;
            float3 AHF_FogAxisOption;
            float AHF_FogHeightStart, AHF_FogHeightEnd, AHF_FogHeightFalloff, AHF_FogLayersMode;
            float AHF_NoiseScale, AHF_NoiseDistanceEnd, AHF_NoiseIntensity;
            float3 AHF_NoiseSpeed;
            float AHF_SkyboxFogHeight, AHF_SkyboxFogFalloff, AHF_SkyboxFogFill, AHF_SkyboxFogIntensity;
            float AHF_FogIntensity;
            float3 AHF_DirectionalDir;
            float AHF_DirectionalIntensity, AHF_DirectionalFalloff;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 screen : TEXCOORD1;
                float directional : TEXCOORD2;
            };

            v2f vert(float4 vertex : POSITION)
            {
                v2f o;
                float3 world = mul(unity_ObjectToWorld, vertex).xyz;
                o.pos = UnityObjectToClipPos(vertex);
                o.screen = ComputeScreenPos(o.pos);
                float d = dot(normalize(world - _WorldSpaceCameraPos), AHF_DirectionalDir) * 0.5 + 0.5;
                o.directional = pow(abs(d * AHF_DirectionalIntensity), AHF_DirectionalFalloff);
                return o;
            }

            float4 Mod289(float4 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
            float4 Permute(float4 x) { return Mod289((x * 34.0 + 1.0) * x); }

            // the game's value noise (Amplify "Simple Noise 3D")
            float Noise3D(float3 p)
            {
                float3 i = floor(p), f = frac(p);
                float4 h = Permute(float4(i.z, i.z, i.z + 1.0, i.z + 1.0) + Permute(float4(i.y, i.y + 1.0, i.y, i.y + 1.0)));
                h = i.x + h;
                float4 c0 = frac(Permute(h) * (1.0 / 41.0));
                float4 c1 = frac(Permute(h + 1.0) * (1.0 / 41.0));
                float3 s = f * f * (3.0 - 2.0 * f);
                float4 x = lerp(c0, c1, s.x);
                float2 z = lerp(x.xz, x.yw, s.y);
                return lerp(z.x, z.y, s.z);
            }

            half4 frag(v2f i) : SV_Target
            {
                float2 uv = i.screen.xy / i.screen.w;
                float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
                // the game's test is "raw depth == far plane" exactly
                float sky = Linear01Depth(raw) > 0.999 ? 1.0 : 0.0;

                #if defined(UNITY_REVERSED_Z)
                float z = 1.0 - raw;
                #else
                float z = raw;
                #endif
                float4 view = mul(unity_CameraInvProjection, float4(float3(uv, z) * 2.0 - 1.0, 1.0));
                view.xyz /= view.w;
                view.z = -view.z;
                float3 world = mul(unity_CameraToWorld, float4(view.xyz, 1.0)).xyz;

                float3 toPixel = world - _WorldSpaceCameraPos;
                float dist = length(toPixel);
                float3 dir = toPixel / max(dist, 1e-5);

                float n = Noise3D(world.zxy / AHF_NoiseScale - AHF_NoiseSpeed.zxy * (_Time.y * 2.0)) * 0.5 - 0.5;
                float noise = 1.0 + saturate((dist - AHF_NoiseDistanceEnd) / -AHF_NoiseDistanceEnd) * AHF_NoiseIntensity * n;

                float h = saturate((dot(world, AHF_FogAxisOption) - AHF_FogHeightEnd) / (AHF_FogHeightStart - AHF_FogHeightEnd));
                h = pow(h, AHF_FogHeightFalloff);
                float d = saturate((dist - AHF_FogDistanceStart) / (AHF_FogDistanceEnd - AHF_FogDistanceStart));
                d = pow(d, AHF_FogDistanceFalloff);
                float additive = min(h + d, 1.0) - d * h;
                float fog = (h * d + AHF_FogLayersMode * additive) * noise;

                float s = saturate((abs(dot(dir, AHF_FogAxisOption)) - AHF_SkyboxFogHeight) / -AHF_SkyboxFogHeight);
                s = pow(max(s, 1e-4), AHF_SkyboxFogFalloff);
                s = lerp(s, 1.0, AHF_SkyboxFogFill);
                fog = lerp(fog, s * AHF_SkyboxFogIntensity, sky);

                float3 col = lerp(AHF_FogColorStart.rgb, AHF_FogColorEnd.rgb, saturate(d - 0.5) * AHF_FogColorDuo);
                col = lerp(col, AHF_DirectionalColor.rgb, i.directional);
                return half4(GammaToLinearSpace(col), fog * AHF_FogIntensity);
            }
            ENDCG
        }
    }
}
