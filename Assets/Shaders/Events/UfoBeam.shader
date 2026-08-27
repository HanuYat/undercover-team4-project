// UFO 빔 (#907) — UfoBeamGroundField가 구운 높이맵보다 아래인 프래그먼트를 버린다.
// 걸린 쪽은 지붕에서, 안 걸린 쪽은 바닥에서 끝난다.
// 자르는 면 근처는 알파를 눕혀 밑면이 번쩍이던 것(#890)도 없앤다.
Shader "Undercover/Events/UfoBeam"
{
    Properties
    {
        // ⚠ [HDR]를 붙이지 말 것 — 감마→선형 변환을 건너뛰어 색이 옅어진다
        _BaseColor ("색", Color) = (0.3, 1, 0.75, 0.32)
        _GroundFade ("바닥 페이드 폭(m)", Range(0.01, 5)) = 0.8
        [NoScaleOffset] _HeightMap ("지면 높이맵 (코드가 넣는다)", 2D) = "black" {}
        [HideInInspector] _HeightField ("높이맵 좌표계 (코드가 넣는다)", Vector) = (0, 0, 0, 0)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }

        Pass
        {
            Name "Beam"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha One
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_HeightMap);
            SAMPLER(sampler_HeightMap);

            // ⚠ 기체마다 값이 다르다 — SRP Batcher가 MPB를 무시하므로 재질을 복제해 쓴다
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float _GroundFade;
                // xy=월드 XZ 원점(왼쪽 아래 모서리), z=1/한 변 크기, w=0이면 자르지 않는다
                float4 _HeightField;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // 높이맵이 아직 없으면 자르지 않는다 — 검은 텍스처(높이 0)로 전부 잘리지 않게
                float above = 1e+9;
                if (_HeightField.w > 0.5)
                {
                    float2 uv = (input.positionWS.xz - _HeightField.xy) * _HeightField.z;
                    float ground = SAMPLE_TEXTURE2D(_HeightMap, sampler_HeightMap, uv).r;
                    above = input.positionWS.y - ground;
                }

                clip(above);

                half4 color = _BaseColor;
                color.a *= saturate(above / _GroundFade);
                return color;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
