// Virnect LCC colored-visualization-mesh shader.
//
// k-NN 으로 splat 색을 입힌 프록시 메쉬를 unlit 으로 렌더.
// URP 호환, 투명도 없음 (mesh 면 자체가 physics proxy 라 알파 불필요).
Shader "Virnect/LccVertexColorUnlit"
{
    Properties
    {
        _Tint ("Tint", Color) = (1,1,1,1)
        _Gamma ("Gamma Lift", Range(0.5, 2)) = 1.0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }
        LOD 100

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Tint;
                float  _Gamma;
            CBUFFER_END

            struct Attrs
            {
                float4 positionOS : POSITION;
                float4 color      : COLOR;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float4 color       : COLOR;
            };

            Varyings vert(Attrs IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.color       = IN.color;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half3 c = IN.color.rgb;
                c = pow(max(c, 1e-4), _Gamma);
                c *= _Tint.rgb;
                return half4(c, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
