// UFO 빔 (#907) — 가산 원기둥을 픽셀마다 지면 높이로 잘라낸다.
//
// 메시 하나는 길이 하나뿐이라, 기둥 단면 일부만 지붕에 걸려도 전체가 지붕 높이에서 끊겼다.
// UfoBeamGroundField가 구운 높이맵을 읽어 자기 월드 XZ의 지면보다 아래면 버리므로,
// 걸린 쪽은 지붕에서 안 걸린 쪽은 바닥에서 끝난다. 자르는 면 근처는 알파를 눕혀
// 밑면이 지면과 겹쳐 번쩍이던 것(#890)도 함께 없앤다.
Shader "Undercover/Events/UfoBeam"
{
    Properties
    {
        // ⚠ [HDR]를 붙이지 말 것 — HDR 색은 이미 선형으로 보고 그대로 올라가서, 종전 URP/Unlit
        // (감마→선형 변환)보다 훨씬 옅고 하얗게 나온다
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

            // ⚠ 기체마다 다른 값이 들어가므로 재질을 복제해서 쓴다(UfoBeamGroundField) —
            // MaterialPropertyBlock은 SRP Batcher가 켜져 있으면 무시될 수 있다.
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
                // 높이맵이 아직 안 들어왔으면 자르지 않는다 — 검은 텍스처(높이 0)로 전부 잘리지 않게
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
