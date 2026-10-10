// The route on the floor in AR: a band in the route colour with white chevrons flowing along it toward the
// destination. The texture's red channel is the band's opacity (soft at the edges), green the chevrons. v runs
// along the route; the chevrons scroll at _ScrollSpeed repeats per second. Unlit, transparent.
Shader "Navigation/Route Ribbon"
{
    Properties
    {
        _MainTex ("Mask (R band, G chevrons)", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _ChevronColor ("Chevron color", Color) = (1,1,1,1)
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

            fixed4 frag (v2f i) : SV_Target
            {
                fixed2 mask = tex2D(_MainTex, i.uv).rg;
                fixed3 rgb = lerp(_Color.rgb, _ChevronColor.rgb, mask.g);
                fixed a = max(mask.r * _Color.a, mask.g) * i.color.a;   // vertex alpha = RouteRibbon's fades
                return fixed4(rgb, a);
            }
            ENDCG
        }
    }
}
