// Virnect LCC Splat billboard shader.
// 버텍스당 (worldPos, corner uv, scale_avg, opacity, color) 가 들어오면,
// 뷰 공간에서 카메라 정면으로 scale 반지름만큼 확장해 쿼드를 그린다.
// 중심 가중 falloff 로 가우시안 느낌을 흉내냄.
Shader "Virnect/LccSplat"
{
    Properties
    {
        _Tint         ("Tint", Color) = (1,1,1,1)
        _ScaleMul     ("Scale Multiplier", Range(0.2, 5)) = 1.5
        _OpacityBoost ("Opacity Boost", Range(0, 1)) = 0.0
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest LEqual
        Cull Off
        LOD 100

        Pass
        {
            Name "URPForward"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _Tint;
            float  _ScaleMul;
            float  _OpacityBoost;

            struct Attributes
            {
                float4 positionOS : POSITION;    // world pos (same for all 4 verts)
                float4 color      : COLOR;
                float2 corner     : TEXCOORD0;   // -1..1
                float2 scaleOp    : TEXCOORD1;   // (scale_avg, opacity)
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float4 color       : COLOR;
                float2 uv          : TEXCOORD0;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                float4 worldPos = mul(unity_ObjectToWorld, float4(v.positionOS.xyz, 1.0));
                float4 viewPos  = mul(UNITY_MATRIX_V, worldPos);
                float radius = max(v.scaleOp.x * _ScaleMul, 0.003);
                viewPos.xy += v.corner * radius;
                o.positionHCS = mul(UNITY_MATRIX_P, viewPos);
                o.color = v.color * _Tint;
                o.color.a = saturate(v.scaleOp.y + _OpacityBoost);
                o.uv = v.corner;
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                // Gaussian falloff : |uv|^2 · -σ → exp
                float r2 = dot(i.uv, i.uv);
                if (r2 > 1.0) discard;
                float falloff = exp(-3.0 * r2);
                float4 c = i.color;
                c.a *= falloff;
                if (c.a < 0.003) discard;
                return c;
            }
            ENDHLSL
        }
    }

    // Built-in 파이프라인 fallback
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest LEqual
        Cull Off
        LOD 100

        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _Tint; float _ScaleMul; float _OpacityBoost;

            struct Attributes { float4 positionOS:POSITION; float4 color:COLOR; float2 corner:TEXCOORD0; float2 scaleOp:TEXCOORD1; };
            struct Varyings   { float4 positionHCS:SV_POSITION; float4 color:COLOR; float2 uv:TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                float4 worldPos = mul(unity_ObjectToWorld, float4(v.positionOS.xyz, 1.0));
                float4 viewPos  = mul(UNITY_MATRIX_V, worldPos);
                float radius = max(v.scaleOp.x * _ScaleMul, 0.003);
                viewPos.xy += v.corner * radius;
                o.positionHCS = mul(UNITY_MATRIX_P, viewPos);
                o.color = v.color * _Tint;
                o.color.a = saturate(v.scaleOp.y + _OpacityBoost);
                o.uv = v.corner;
                return o;
            }
            float4 frag(Varyings i) : SV_Target
            {
                float r2 = dot(i.uv, i.uv);
                if (r2 > 1.0) discard;
                i.color.a *= exp(-3.0 * r2);
                if (i.color.a < 0.003) discard;
                return i.color;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
