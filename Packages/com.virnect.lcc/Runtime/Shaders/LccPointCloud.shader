// Virnect LCC Point Cloud shader.
// URP + Built-in 양쪽에서 동작. VertexID 기반으로 스플랫당 빌보드 쿼드(6 verts)를 확장.
// StructuredBuffer 로 포지션·색을 읽어, 카메라 정면 스크린 공간에서 점 크기를 유지한다.
Shader "Virnect/LccPointCloud"
{
    Properties
    {
        _PointSize ("Point Size (world units)", Range(0.001, 0.5)) = 0.03
        _Tint      ("Tint", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "IgnoreProjector"="True" "RenderPipeline"="UniversalPipeline" }
        LOD 100
        Cull Off
        ZWrite On
        ZTest LEqual

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            StructuredBuffer<float3> _Positions;
            StructuredBuffer<uint>   _Colors;  // RGBA8 packed uint

            float  _PointSize;
            float4 _Tint;

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float4 color       : COLOR;
            };

            // 빌보드 쿼드를 6 verts 로 삼각형 두 개.
            static const float2 kOffsets[6] = {
                float2(-0.5, -0.5),
                float2( 0.5, -0.5),
                float2(-0.5,  0.5),
                float2( 0.5, -0.5),
                float2( 0.5,  0.5),
                float2(-0.5,  0.5),
            };

            Varyings vert(uint vid : SV_VertexID)
            {
                uint pointIdx = vid / 6u;
                uint corner   = vid % 6u;

                float3 wpos = _Positions[pointIdx];
                uint packed = _Colors[pointIdx];
                float r = ((packed       ) & 0xFFu) / 255.0;
                float g = ((packed >>  8u) & 0xFFu) / 255.0;
                float b = ((packed >> 16u) & 0xFFu) / 255.0;
                float a = ((packed >> 24u) & 0xFFu) / 255.0;

                float4 view = mul(UNITY_MATRIX_V, float4(wpos, 1.0));
                float2 off  = kOffsets[corner] * _PointSize;
                view.xy += off;

                Varyings o;
                o.positionHCS = mul(UNITY_MATRIX_P, view);
                o.color = float4(r, g, b, a) * _Tint;
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                return i.color;
            }
            ENDHLSL
        }

        // Built-in fallback pass (URP가 없으면 이 쪽이 선택됨)
        Pass
        {
            Name "BuiltinForward"
            Tags { "LightMode"="ForwardBase" }

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            StructuredBuffer<float3> _Positions;
            StructuredBuffer<uint>   _Colors;

            float  _PointSize;
            float4 _Tint;

            struct Varyings { float4 positionHCS : SV_POSITION; float4 color : COLOR; };

            static const float2 kOffsets[6] = {
                float2(-0.5,-0.5), float2(0.5,-0.5), float2(-0.5,0.5),
                float2( 0.5,-0.5), float2(0.5, 0.5), float2(-0.5,0.5)
            };

            Varyings vert(uint vid : SV_VertexID)
            {
                uint pi = vid / 6u, ci = vid % 6u;
                float3 wpos = _Positions[pi];
                uint p = _Colors[pi];
                float4 col = float4((p&0xFFu)/255.0, ((p>>8)&0xFFu)/255.0,
                                    ((p>>16)&0xFFu)/255.0, ((p>>24)&0xFFu)/255.0);
                float4 view = mul(UNITY_MATRIX_V, float4(wpos,1.0));
                view.xy += kOffsets[ci] * _PointSize;
                Varyings o; o.positionHCS = mul(UNITY_MATRIX_P, view); o.color = col * _Tint; return o;
            }
            float4 frag(Varyings i) : SV_Target { return i.color; }
            ENDHLSL
        }
    }
}
