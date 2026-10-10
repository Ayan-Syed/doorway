// The route on the floor in AR: a band in the route colour with white chevrons flowing along it toward the
// destination, and a soft glow round its edges. The texture's red channel is the band's opacity (solid at the
// edges), green the chevrons, blue the glow (peaking on the band's edges, fading out past them). v runs along the
// route; the chevrons scroll at _ScrollSpeed repeats per second. Unlit, transparent: the glow, then the band over
// it, then the chevrons.
Shader "Navigation/Route Ribbon"
{
    Properties
    {
        _MainTex ("Mask (R band, G chevrons, B glow)", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _ChevronColor ("Chevron color", Color) = (1,1,1,1)
        _GlowColor ("Glow color", Color) = (1,1,1,0)
        _ScrollSpeed ("Scroll speed (repeats per second)", Float) = 1
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            fixed4 _ChevronColor;
            fixed4 _GlowColor;
            float _ScrollSpeed;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                // Moving the pattern toward +v (forward along the route). frac keeps uv small; the texture repeats.
                o.uv.y -= frac(_Time.y * _ScrollSpeed);
                o.color = v.color;
                return o;
            }

            // top over bottom, both straight (not premultiplied) alpha.
            fixed4 Over(fixed4 top, fixed4 bottom)
            {
                fixed a = top.a + bottom.a * (1 - top.a);
                fixed3 rgb = (top.rgb * top.a + bottom.rgb * bottom.a * (1 - top.a)) / max(a, 1e-4);
                return fixed4(rgb, a);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed3 mask = tex2D(_MainTex, i.uv).rgb;
                fixed4 c = fixed4(_GlowColor.rgb, mask.b * _GlowColor.a);
                c = Over(fixed4(_Color.rgb, mask.r * _Color.a), c);
                c = Over(fixed4(_ChevronColor.rgb, mask.g), c);
                c.a *= i.color.a;   // vertex alpha = RouteRibbon's fades
                return c;
            }
            ENDCG
        }
    }
}
