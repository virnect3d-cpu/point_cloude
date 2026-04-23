// Virnect LCC colored-visualization-mesh shader (v2 photoreal).
//
// vertex color32 는 sRGB 바이트로 GPU 에 들어와 Linear workspace 에선 디감마가 필요.
// Properties 로 exposure / saturation / contrast 를 노출해 후처리 파이프라인 없이도
// 톤매핑 비슷한 룩 조절 가능.
Shader "Virnect/LccVertexColorUnlit"
{
    Properties
    {
        _Tint       ("Tint", Color)               = (1,1,1,1)
        _Exposure   ("Exposure",  Range(-2, 2))   = 0.0
        _Saturation ("Saturation", Range(0, 2))   = 1.10
        _Contrast   ("Contrast",  Range(0.5, 2))  = 1.05
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
                float  _Exposure;
                float  _Saturation;
                float  _Contrast;
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

            // Approximate sRGB → linear (같은 커브의 pow(2.2) 근사)
            half3 SrgbToLinear(half3 c)
            {
                return c * (c * (c * 0.305306011 + 0.682171111) + 0.012522878);
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half3 c = IN.color.rgb;

                #ifndef UNITY_COLORSPACE_GAMMA
                    // Linear workspace 에선 COLOR 시맨틱 sRGB 바이트 디감마
                    c = SrgbToLinear(c);
                #endif

                // Exposure
                c *= exp2(_Exposure);

                // Saturation (luma preserving)
                half luma = dot(c, half3(0.299, 0.587, 0.114));
                c = lerp(half3(luma, luma, luma), c, _Saturation);

                // Contrast around 0.5
                c = (c - 0.5) * _Contrast + 0.5;
                c = max(c, 0);

                c *= _Tint.rgb;
                return half4(c, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
