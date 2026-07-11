using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 외형 특징 축별 옵션 정의 (ScriptableObject). (#74)
/// 각 옵션은 무전으로 말로 전달 가능한 표시 이름과 시각 리소스(색/머티리얼/프롭)를 가진다.
/// 몽타주 텍스트(GDD 10-3 글 방식)도 여기서 생성한다 — 본부 수배 UI(#58)의 원본.
/// </summary>
[CreateAssetMenu(fileName = "AppearanceDatabase", menuName = "Scriptable Objects/AppearanceDatabase")]
public class AppearanceDatabase : ScriptableObject
{
    /// <summary>축 하나의 옵션 — 표시 이름 + 시각 리소스.</summary>
    [Serializable]
    public class AppearanceOption
    {
        [Tooltip("몽타주·무전으로 전달하는 표시 이름 (예: 빨강, 없음)")]
        public string DisplayName;

        [Tooltip("프롭 렌더러에 틴트되는 색 (프롭 없는 옵션에서는 무시)")]
        public Color Color = Color.white;

        [Tooltip("지정하면 대상 머티리얼 슬롯을 통째로 교체한다 (프롭이 없을 때만)")]
        public Material MaterialOverride;

        [Tooltip("지정하면 머리 앵커에 부착하는 프롭 (머리카락·수염·모자·안경 등). 색은 Color로 틴트된다")]
        public GameObject PropPrefab;
    }

    /// <summary>축 하나의 정의 — 몽타주 표기용 축 이름과 옵션 목록.</summary>
    [Serializable]
    public class AxisDefinition
    {
        [Tooltip("몽타주 텍스트에 쓰는 축 이름 (예: 머리색)")]
        public string AxisName;
        public AppearanceOption[] Options;
    }

    [Header("특징 축 (AppearanceAxis 순서와 일치)")]
    [SerializeField] private AxisDefinition m_hairColor;
    [SerializeField] private AxisDefinition m_facialHair;
    [SerializeField] private AxisDefinition m_accessory;

    public AxisDefinition GetAxis(AppearanceAxis axis) => axis switch
    {
        AppearanceAxis.HairColor => m_hairColor,
        AppearanceAxis.FacialHair => m_facialHair,
        AppearanceAxis.Accessory => m_accessory,
        _ => null,
    };

    public int GetOptionCount(AppearanceAxis axis)
    {
        AxisDefinition definition = GetAxis(axis);
        return definition?.Options?.Length ?? 0;
    }

    public string GetAxisName(AppearanceAxis axis) => GetAxis(axis)?.AxisName ?? axis.ToString();

    public AppearanceOption GetOption(AppearanceAxis axis, int index)
    {
        AxisDefinition definition = GetAxis(axis);
        if (definition?.Options == null || index < 0 || index >= definition.Options.Length)
            return null;
        return definition.Options[index];
    }

    /// <summary>모든 축에서 랜덤 옵션을 뽑아 프로필을 만든다. 옵션이 없는 축은 0으로 둔다.</summary>
    public AppearanceProfile CreateRandomProfile()
    {
        AppearanceProfile profile = default;
        for (int i = 0; i < AppearanceProfile.k_axisCount; i++)
        {
            var axis = (AppearanceAxis)i;
            int count = GetOptionCount(axis);
            profile.SetIndex(axis, count > 0 ? Random.Range(0, count) : 0);
        }
        return profile;
    }

    /// <summary>
    /// 공개 축들의 특징을 글 방식 몽타주 텍스트로 만든다 (GDD 10-3).
    /// 예: "머리색: 빨강 / 수염: 콧수염" — 무전 구두 전달이 핵심 재미라 이산 값 이름만 나열한다.
    /// </summary>
    public string BuildMontageText(in AppearanceProfile profile, IReadOnlyList<AppearanceAxis> revealedAxes)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < revealedAxes.Count; i++)
        {
            AppearanceOption option = GetOption(revealedAxes[i], profile.GetIndex(revealedAxes[i]));
            if (option == null)
                continue;

            if (builder.Length > 0)
                builder.Append(" / ");
            builder.Append(GetAxisName(revealedAxes[i])).Append(": ").Append(option.DisplayName);
        }
        return builder.ToString();
    }
}
