// 화면 강수 (#782) — 눈·비를 화면에 얹는다 (알파 블렌드. 애디티브는 밝은 하늘에서 묻혀 버렸다).
//
// 파티클 리그(WeatherSkyRig)를 대신한다. 어디에 뿌릴지를 배치로 풀지 않고, PrecipitationMask가
// 구운 "이 방향이 하늘에 열렸는가"로 픽셀마다 가린다 — 그래서 실내·창밖·기둥이 한 규칙으로 끝난다.
// 설계 근거: docs/superpowers/specs/2026-08-21-precipitation-shader-design.md
//
// 눈과 비는 같은 셰이더다. 갈리는 것은 값뿐이다(줄기 길이·속도·흔들림).
Shader "Undercover/Weather/PrecipitationScreen"
{
    Properties
    {
        [HDR] _Tint ("색", Color) = (0.8, 0.85, 0.95, 1)
        _Cells ("칸 수 (밀도의 기준)", Float) = 40
        _Fall ("낙하 속도", Float) = 1.2
        _Streak ("줄기 길이 (1=점, 크면 선)", Float) = 8
        _Thickness ("굵기", Range(1, 40)) = 14
        _Occupancy ("칸이 채워질 확률", Range(0.02, 1)) = 0.35
        _Tilt ("기울기 (바람)", Range(-1, 1)) = 0.15
        _Drift ("흔들림 (눈)", Range(0, 0.5)) = 0
        _Layers ("겹 수", Range(1, 4)) = 3
        _Opacity ("전체 진하기", Range(0, 1)) = 0.6
        _CenterClear ("화면 중앙 비우기", Range(0, 1)) = 0.55
        _MaskCut ("마스크 경계 기준", Range(0.1, 0.9)) = 0.55
        _MaskSoft ("마스크 경계 부드러움", Range(0.01, 0.4)) = 0.10
    }

    SubShader
    {
        // 불투명·투명이 다 그려진 뒤 화면에 얹는다. 깊이를 쓰지 않으므로 정렬에 끼지 않는다.
        Tags { "RenderType" = "Overlay" "RenderPipeline" = "UniversalPipeline" "Queue" = "Overlay" }

        Pass
        {
            Name "Precipitation"
            // ⚠ 애디티브(Blend One One)로 시작했다가 알파 블렌드로 바꿨다 (#782).
            // 밝은 하늘(아포칼립스 맵)에서는 더하기가 이미 흰 배경에 묻혀 아무것도 안 보였다 —
            // 설계 문서가 미결로 적어둔 "애디티브가 맞는가"의 답이 실측으로 '아니다'였다.
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always           // 카메라 자식 쿼드라 깊이로 걸러질 이유가 없다
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // ⚠ 전역은 CBUFFER 밖에 둔다. Properties에 넣으면 머티리얼 상수 버퍼로 들어가
            // Shader.SetGlobalTexture/Float이 덮지 못한다 (PrecipitationMask가 전역으로 넣는다).
            TEXTURE2D(_PrecipMask);
            SAMPLER(sampler_PrecipMask);
            float _PrecipAmount;

            CBUFFER_START(UnityPerMaterial)
                float4 _Tint;
                float _Cells;
                float _Fall;
                float _Streak;
                float _Thickness;
                float _Occupancy;
                float _Tilt;
                float _Drift;
                float _Layers;
                float _Opacity;
                float _CenterClear;
                float _MaskCut;
                float _MaskSoft;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv; // 쿼드가 화면을 꽉 채우므로 이 uv가 곧 화면 uv다
                return output;
            }

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            // 한 겹 — 칸마다 입자를 하나 두고 세로로 긴 방울을 그린다.
            //
            // ⚠ <b>칸을 세로로 늘린다</b>(cells.y = cells / streak). 칸을 정사각으로 두고 거리만
            // 늘리면 두 가지가 어긋난다: ① delta.y에 곱하면 세로가 <b>납작해져</b> 빗줄기가 가로
            // 대시로 나온다(늘리려면 나눠야 한다) ② 줄기가 칸보다 길면 이웃 칸이 그리지 않아
            // 끝이 잘린다. 칸 자체를 늘리면 둘이 함께 풀린다.
            float Layer(float2 uv, float cells, float fall, float seed, float time)
            {
                float streak = max(_Streak, 1.0);
                float2 cellsXY = float2(cells, cells / streak);

                float2 grid = uv * cellsXY;
                grid.x += seed * 13.7;               // 겹마다 다른 자리
                // ⚠ 부호에 주의 — 빼면 <b>위로</b> 흐른다. 무늬가 고정되려면 uv.y가 커져야 하고,
                // uv.y가 큰 쪽이 화면 위이기 때문이다. 더해야 아래로 내린다.
                grid.y += time * fall;

                float2 cell = floor(grid);
                float2 local = frac(grid);

                // 이 칸에 입자가 있는가 — 없으면 0. 밀도를 칸 단위로 끊어 규칙적인 격자무늬를 막는다.
                if (Hash21(cell + seed * 7.13) > _Occupancy)
                    return 0;

                float x = Hash21(cell + seed * 3.71 + 19.3);
                float phase = Hash21(cell + seed * 5.17 + 41.7);

                // 눈은 좌우로 흔들린다. 비는 _Drift가 0이라 이 항이 사라진다.
                x += sin(time * 2.0 + phase * 6.2831) * _Drift;

                // 송이마다 크기를 흔든다 — 전부 같은 크기면 정원이 규칙적으로 떨어져 인공적으로 보인다.
                // 0.55~1.0 배로 굵기를 나눠 큰 송이와 잔 송이가 섞이게 한다.
                float size = lerp(0.55, 1.0, Hash21(cell + seed * 11.3 + 63.1));

                // 칸이 이미 세로로 늘어나 있으므로 등방 거리로 재면 화면에서는 세로로 긴 방울이 된다.
                float2 delta = local - float2(x, 0.5);
                return saturate(1.0 - length(delta) * (_Thickness / size));
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float amount = saturate(_PrecipAmount);
                if (amount <= 0.001)
                    return 0;

                // 이 픽셀이 보는 방향이 하늘에 열렸는가 — 실내·처마·기둥이 여기서 한 번에 걸러진다
                float open = SAMPLE_TEXTURE2D(_PrecipMask, sampler_PrecipMask, input.uv).r;

                // ⚠ <b>경계를 세운다.</b> 마스크는 저해상(칸 십여 개)이라 바이리니어로 늘리면 문 구멍이
                // 주변 실내 벽까지 번지고, 깊은 실내에서 밖을 볼 때 <b>강수가 실내에 들어온 것처럼</b>
                // 보인다. 중간값을 0/1로 밀어 번짐을 걷는다.
                open = smoothstep(_MaskCut - _MaskSoft, _MaskCut + _MaskSoft, open);

                float gate = amount * open;
                if (gate <= 0.001)
                    return 0;

                // 칸이 화면 비율에 늘어나지 않게 x를 보정한다
                float aspect = _ScreenParams.x / max(_ScreenParams.y, 1.0);
                float2 uv = float2(input.uv.x * aspect, input.uv.y);

                // 기울기 — 아래로 갈수록 x를 밀어 사선으로 내린다
                uv.x += (1.0 - input.uv.y) * _Tilt;

                float time = _Time.y;
                float sum = 0;
                int layers = (int)round(_Layers);
                [unroll(4)]
                for (int i = 0; i < layers; i++)
                {
                    // 겹마다 칸을 촘촘히·빠르게 — 가까운 눈과 먼 눈이 갈려 깊이가 생긴다
                    float scale = 1.0 + i * 0.55;
                    sum += Layer(uv, _Cells * scale, _Fall * scale, i + 1, time) / scale;
                }

                // <b>화면 중앙을 비운다</b> — 전면에 고르게 덮으면 세계의 날씨가 아니라 <b>렌즈에 묻은 것</b>
                // 처럼 보이고, 크로스헤어·표적이 있는 중앙까지 가려 플레이에 방해가 된다. 가장자리를
                // 진하게 두면 시야 주변에서 날씨를 느끼면서 볼 곳은 트인다.
                float2 fromCenter = float2((input.uv.x - 0.5) * aspect, input.uv.y - 0.5);
                float edge = saturate(length(fromCenter) / 0.7);
                float centerFade = lerp(1.0 - _CenterClear, 1.0, smoothstep(0.0, 1.0, edge));

                // 알파 블렌드 — 색은 그대로, 덮는 정도로 낸다. 배경이 밝아도 어두워도 읽힌다.
                return half4(_Tint.rgb, saturate(sum) * gate * _Opacity * centerFade);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
