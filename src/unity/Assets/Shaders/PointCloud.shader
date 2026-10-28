Shader "Custom/PointCloud"
{
    Properties
    {
        _PointSize  ("Point Size (px)",          Float) = 3.0
        _ColorMode  ("Color Mode (0=RGB 1=Heat)",  Int) = 0
        _HeatMin    ("Heat Near (m)",            Float) = 0.5
        _HeatMax    ("Heat Far  (m)",            Float) = 5.0
        _Shape      ("Shape (0=circle 1=square)",  Int) = 1
        _Gamma      ("Gamma (> 1 = darker highlights)", Float) = 1.35
        _Contrast   ("Contrast",                 Float) = 1.05
        _Brightness ("Brightness",               Float) = -0.05
        _HighlightRolloff ("Highlight Rolloff (0=off, 1=hard)", Float) = 0.75
        _RolloffThreshold ("Rolloff Threshold",  Float) = 0.78
        _ThereminHue      ("Theremin Hue Shift (0..1)", Float) = 0
        _ThereminShimmer  ("Theremin Shimmer (0..1)",   Float) = 0
        _FloorBrightness  ("Floor Brightness (dark-room lift)", Float) = 0.18
        _TribeIntensity   ("TRIBE Intensity (0..1)",    Float) = 0
        _TribePulse       ("TRIBE Pulse (0..1)",        Float) = 0
        _TribeTrend       ("TRIBE Trend (-1..1)",       Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }

        Pass
        {
            ZWrite On
            ZTest  LEqual
            Cull   Off

            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   4.5

            #include "UnityCG.cginc"

            struct PointData
            {
                float3 position;
                uint   color;     // packed RGBA8: R=[0..7] G=[8..15] B=[16..23]
            };

            StructuredBuffer<PointData> _Points;
            float _PointSize;
            int   _ColorMode;
            float _HeatMin, _HeatMax;
            int   _Shape;
            float _Gamma, _Contrast, _Brightness;
            float _HighlightRolloff, _RolloffThreshold;
            float _ThereminHue, _ThereminShimmer;
            float _FloorBrightness;
            float _TribeIntensity, _TribePulse, _TribeTrend;

            float3 RGBtoHSV(float3 c)
            {
                float4 K = float4(0.0, -1.0/3.0, 2.0/3.0, -1.0);
                float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
                float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
                float d = q.x - min(q.w, q.y);
                float e = 1.0e-10;
                return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
            }

            float3 HSVtoRGB(float3 c)
            {
                float4 K = float4(1.0, 2.0/3.0, 1.0/3.0, 3.0);
                float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
                return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
            }

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 col : COLOR;
                float2 uv  : TEXCOORD0;
            };

            static const float2 QUAD[6] =
            {
                float2(-1,-1), float2( 1,-1), float2(-1, 1),
                float2(-1, 1), float2( 1,-1), float2( 1, 1)
            };

            float3 HeatColor(float t)
            {
                t = saturate(t);
                float3 c = float3(0,0,1);
                c = lerp(c, float3(0,1,1), saturate(t*4));
                c = lerp(c, float3(0,1,0), saturate(t*4-1));
                c = lerp(c, float3(1,1,0), saturate(t*4-2));
                c = lerp(c, float3(1,0,0), saturate(t*4-3));
                return c;
            }

            v2f vert(uint vid : SV_VertexID)
            {
                uint pidx  = vid / 6u;
                uint qidx  = vid % 6u;
                PointData pd = _Points[pidx];
                float2 corner = QUAD[qidx];

                float4 clipPos = mul(UNITY_MATRIX_VP, float4(pd.position, 1.0));
                clipPos.xy += corner * _PointSize / _ScreenParams.xy * clipPos.w * 2.0;

                float4 col;
                if (_ColorMode == 0)
                {
                    col.r = ((pd.color >>  0) & 0xFFu) / 255.0;
                    col.g = ((pd.color >>  8) & 0xFFu) / 255.0;
                    col.b = ((pd.color >> 16) & 0xFFu) / 255.0;
                    col.a = 1.0;

                    // Gamma → contrast → brightness
                    col.rgb = pow(max(col.rgb, 0.0001), _Gamma);
                    col.rgb = saturate(col.rgb * _Contrast + _Brightness);

                    // Highlight roll-off: pulls saturated whites toward a warm neutral grey.
                    // Fixes Kinect RGB blow-outs (overhead lights bouncing off white walls).
                    float lum  = max(col.r, max(col.g, col.b));
                    float over = saturate((lum - _RolloffThreshold) / max(1.0 - _RolloffThreshold, 0.001));
                    col.rgb    = lerp(col.rgb, float3(0.55, 0.52, 0.48), over * _HighlightRolloff);

                    // Theremini synesthesia layer (opt-in via non-zero uniforms).
                    // Pitch  → hue rotation in HSV space (broad colour weather, no per-pixel flicker).
                    // Volume → saturation boost + iridescent pulse modulated by spatial position
                    //          so the shimmer reads as "the room shimmering" rather than a flat overlay.
                    if (_ThereminHue > 0.0001 || _ThereminShimmer > 0.0001)
                    {
                        float3 hsv = RGBtoHSV(col.rgb);
                        hsv.x = frac(hsv.x + _ThereminHue);
                        hsv.y = saturate(hsv.y * (1.0 + _ThereminShimmer * 0.6) + _ThereminShimmer * 0.25);
                        col.rgb = HSVtoRGB(hsv);
                        float pulse = sin(_Time.y * 2.5 + dot(pd.position, float3(1.7, 2.3, 1.1))) * 0.5 + 0.5;
                        col.rgb *= (1.0 + pulse * _ThereminShimmer * 0.28);
                        col.rgb = saturate(col.rgb);
                    }

                    // TRIBE brain-encoding modulation (independent from Theremini).
                    // Intensity → overall vividness; Pulse → spatial wave from activation peaks;
                    // Trend → warm/cool colour shift. Zero uniforms = no effect.
                    if (_TribeIntensity > 0.0001 || _TribePulse > 0.0001 || abs(_TribeTrend) > 0.0001)
                    {
                        col.rgb *= (1.0 + _TribeIntensity * 0.35);
                        if (_TribePulse > 0.0001)
                        {
                            float w = sin(_Time.y * 3.5 + dot(pd.position.xz, float2(2.3, 1.7))) * 0.5 + 0.5;
                            col.rgb *= (1.0 + w * _TribePulse * 0.45);
                        }
                        if (abs(_TribeTrend) > 0.0001)
                        {
                            float3 warm = float3(1.10, 0.95, 0.80);
                            float3 cool = float3(0.80, 0.95, 1.15);
                            float3 tint = _TribeTrend > 0.0 ? warm : cool;
                            col.rgb = lerp(col.rgb, col.rgb * tint, abs(_TribeTrend));
                        }
                        col.rgb = saturate(col.rgb);
                    }

                    // Brightness floor — keeps the cloud visible in a dark room.
                    // Remaps HSV Value [0,1] → [_FloorBrightness, 1], so the
                    // darkest points glow at a baseline instead of vanishing.
                    // Touches only V, so hue + saturation are fully preserved —
                    // unlike the brightness slider, this lifts darks WITHOUT
                    // washing the colour out.
                    if (_FloorBrightness > 0.001)
                    {
                        float3 hsvF = RGBtoHSV(col.rgb);
                        hsvF.z = _FloorBrightness + hsvF.z * (1.0 - _FloorBrightness);
                        col.rgb = HSVtoRGB(hsvF);
                    }
                }
                else
                {
                    float dist = length(pd.position);
                    float t    = (dist - _HeatMin) / max(_HeatMax - _HeatMin, 0.001);
                    col = float4(HeatColor(t), 1.0);
                }

                v2f o;
                o.pos = clipPos;
                o.col = col;
                o.uv  = corner;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float d = dot(i.uv, i.uv);
                if (_Shape == 0 && d > 1.0) discard;
                // Spherical shading: full brightness at centre, fades ~28% at edge.
                // Gives each point a subtle surface-depth cue without alpha sorting.
                float rim = 1.0 - smoothstep(0.16, 1.0, d) * 0.28;
                return fixed4(i.col.rgb * rim, 1.0);
            }
            ENDCG
        }
    }
}
