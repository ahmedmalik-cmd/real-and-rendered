// Perspective-correct projection-mapping blit.
//
// For each output pixel on the projector display, applies a 3×3 homography
// to the display's normalized position to get the corresponding source UV
// in the camera's RenderTexture. The homography is computed C#-side via
// DLT from the 4 user-defined corners and sent as three Vector4 uniforms.
//
// Pixels whose sampled source UV falls outside [0,1] are discarded — the
// framer camera's black shows through there, so the projector beam emits
// no light onto surroundings.
//
// Optional edge feather smooths the alpha falloff in a configurable band
// around the source-UV edges, useful for hiding the seam between two
// overlapping projectors.

Shader "Custom/QuadWarp"
{
    Properties
    {
        _MainTex      ("Source RT",          2D)       = "white" {}
        _HRow0        ("Homography row 0",   Vector)   = (1,0,0,0)
        _HRow1        ("Homography row 1",   Vector)   = (0,1,0,0)
        _HRow2        ("Homography row 2",   Vector)   = (0,0,1,0)
        _FeatherWidth ("Edge feather (UV)",  Range(0, 0.25)) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        ZWrite Off  ZTest Always  Cull Off  Blend Off

        Pass
        {
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _HRow0, _HRow1, _HRow2;
            float  _FeatherWidth;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f     { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv     = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Apply 3×3 homography to (display u, display v, 1) to get
                // (source x · w, source y · w, w) in homogeneous coords.
                float3 p = float3(i.uv, 1.0);
                float w  = dot(_HRow2.xyz, p);
                if (abs(w) < 1.0e-6) discard;
                float2 srcUV = float2(dot(_HRow0.xyz, p), dot(_HRow1.xyz, p)) / w;

                // Anything outside the source-RT bounds means this display
                // pixel is outside the warped quad — leave it black.
                if (srcUV.x < 0.0 || srcUV.x > 1.0 ||
                    srcUV.y < 0.0 || srcUV.y > 1.0)
                    discard;

                fixed4 c = tex2D(_MainTex, srcUV);

                // Smooth edge falloff (multiplicative; framer black supplies the rest)
                if (_FeatherWidth > 1.0e-4)
                {
                    float fw   = _FeatherWidth;
                    float fade = smoothstep(0.0, fw, srcUV.x)
                               * smoothstep(0.0, fw, srcUV.y)
                               * smoothstep(0.0, fw, 1.0 - srcUV.x)
                               * smoothstep(0.0, fw, 1.0 - srcUV.y);
                    c.rgb *= fade;
                }

                return fixed4(c.rgb, 1.0);
            }
            ENDCG
        }
    }
}
