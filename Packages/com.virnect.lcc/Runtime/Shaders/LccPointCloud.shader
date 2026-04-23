// Virnect LCC Point Cloud — 버텍스 색을 그대로 출력하는 단순 Unlit 셰이더.
// MeshTopology.Points 메쉬에 붙여 쓰며, URP/Built-in 양쪽에서 동작.
Shader "Virnect/LccPointCloud"
{
    Properties
    {
        _Tint ("Tint", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }
        LOD 100
        Cull Off ZWrite On ZTest LEqual

        Pass
        {
            Name "URPForward"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _Tint;

            struct Attributes { float4 positionOS : POSITION; float4 color : COLOR; };
            struct Varyings   { float4 positionHCS : SV_POSITION; float4 color : COLOR; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionHCS = UnityObjectToClipPos(v.positionOS);
                o.color = v.color * _Tint;
                return o;
            }
            float4 frag(Varyings i) : SV_Target { return i.color; }
            ENDHLSL
        }
    }

    // Built-in 파이프라인용 SubShader (URP 없을 때 선택)
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 100
        Cull Off ZWrite On ZTest LEqual

        Pass
        {
            Tags { "LightMode"="ForwardBase" }

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _Tint;
            struct Attributes { float4 positionOS : POSITION; float4 color : COLOR; };
            struct Varyings   { float4 positionHCS : SV_POSITION; float4 color : COLOR; };
            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionHCS = UnityObjectToClipPos(v.positionOS);
                o.color = v.color * _Tint;
                return o;
            }
            float4 frag(Varyings i) : SV_Target { return i.color; }
            ENDHLSL
        }
    }

    FallBack "Unlit/Color"
}
