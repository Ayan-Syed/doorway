// The selected place on the floor map: a tinted fill and a crisp outline, drawn from a soft mask (the place's
// pixels on the place map, blurred). The edge is where the mask crosses one half; fwidth keeps the outline the
// same width on screen at any zoom. A UI shader: works under Mask and RectMask2D.
Shader "Navigation/Map Highlight"
{
    Properties
    {
        [PerRendererData] _MainTex ("Mask (alpha)", 2D) = "black" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _FillAlpha ("Fill opacity", Range(0,1)) = 0.2
        _StrokeWidth ("Outline half-width (screen pixels)", Float) = 2.2

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
                float4 local : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float _FillAlpha;
            float _StrokeWidth;
            float4 _ClipRect;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.local = v.vertex;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color * _Color;   // the RawImage's colour (destination) times the tint
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float m = tex2D(_MainTex, i.uv).a;
                float w = max(fwidth(m), 1e-4);
                // Distance from the edge in screen pixels, where the mask ramps.
                float d = (m - 0.5) / w;
                float inside = saturate(d + 0.5);
                float stroke = 1.0 - saturate(abs(d) - _StrokeWidth + 0.5);
                float a = max(inside * _FillAlpha, stroke) * i.color.a;
                fixed4 c = fixed4(i.color.rgb, a);
                #ifdef UNITY_UI_CLIP_RECT
                c.a *= UnityGet2DClipping(i.local.xy, _ClipRect);
                #endif
                return c;
            }
            ENDCG
        }
    }
}
