using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Localization;
using Random = UnityEngine.Random;

// 외형 특징 축별 옵션 정의 (ScriptableObject).
// 각 옵션은 무전으로 말로 전달 가능한 표시 이름과 시각 리소스(색/머티리얼/프롭)를 가진다.
// 몽타주 텍스트도 여기서 생성. — 본부 수배 리스트 UI의 원본.
[CreateAssetMenu(fileName = "AppearanceDatabase", menuName = "Scriptable Objects/AppearanceDatabase")]
public class AppearanceDatabase : ScriptableObject
{
    /// <summary>축 하나의 옵션 — 표시 이름 + 시각 리소스.</summary>
    [Serializable]
    public class AppearanceOption
    {
        [Tooltip("몽타주·무전으로 전달하는 표시 이름 — NpcTable의 Npc.Appearance.* (예: 빨강, 없음)")]
        public LocalizedString DisplayName;

        [Tooltip("프롭 렌더러에 틴트되는 색 (프롭 없는 옵션에서는 무시)")]
        public Color Color = Color.white;

        [Tooltip("지정하면 대상 머티리얼 슬롯을 통째로 교체한다 (프롭이 없을 때만)")]
        public Material MaterialOverride;

        [Tooltip("지정하면 머리 앵커에 부착하는 프롭 (머리카락·수염·모자·안경 등). 색은 Color로 틴트된다")]
        public GameObject PropPrefab;

        [Tooltip("SciFi 카탈로그 전용 값 — Generic 경로엔 프롭이 없어 표현 불가하므로 Generic 랜덤 배정에서 제외한다 (예: 머리 '가림', 후드/헬멧, 특수 피부색). 몽타주 텍스트·SciFi 카탈로그에는 그대로 쓰인다")]
        public bool SciFiOnly;
    }

    /// <summary>
    /// 축 하나의 정의 — 옵션 목록. 축 이름은 여기 없다.
    /// 축은 데이터가 아니라 <see cref="AppearanceAxis"/>가 정하는 목록이라, 이름은 규약 키
    /// (<c>Npc.Axis.</c> + enum 이름)로 조회한다 — 에셋에 두면 배선할 곳이 하나 더 생긴다. (#497)
    /// </summary>
    [Serializable]
    public class AxisDefinition
    {
        public AppearanceOption[] Options;
    }

    private const string k_table = "NpcTable";

    [Header("특징 축 (AppearanceAxis 순서와 일치)")]
    [SerializeField] private AxisDefinition m_hairStyle;
    [SerializeField] private AxisDefinition m_hairColor;
    [SerializeField] private AxisDefinition m_skinColor;
    [SerializeField] private AxisDefinition m_facialHair;
    [SerializeField] private AxisDefinition m_headwear;   // 기존 m_accessory에서 개명
    [SerializeField] private AxisDefinition m_eyewear;

    public AxisDefinition GetAxis(AppearanceAxis axis) => axis switch
    {
        AppearanceAxis.HairStyle => m_hairStyle,
        AppearanceAxis.HairColor => m_hairColor,
        AppearanceAxis.SkinColor => m_skinColor,
        AppearanceAxis.FacialHair => m_facialHair,
        AppearanceAxis.Headwear => m_headwear,
        AppearanceAxis.Eyewear => m_eyewear,
        _ => null
    };

    public int GetOptionCount(AppearanceAxis axis)
    {
        AxisDefinition definition = GetAxis(axis);
        return definition?.Options?.Length ?? 0;
    }

    /// <summary>축 이름을 지금 언어로 읽는다 — 규약 키 <c>Npc.Axis.&lt;AppearanceAxis&gt;</c>. (#497)</summary>
    public static string GetAxisName(AppearanceAxis axis) =>
        LocalizedStrings.Get(k_table, "Npc.Axis." + axis);

    /// <summary>옵션의 표시 이름을 지금 언어로 읽는다. 배선이 빠진 옵션은 물음표로 둔다.</summary>
    public static string GetOptionName(AppearanceOption option) =>
        option == null || option.DisplayName == null || option.DisplayName.IsEmpty
            ? "?"
            : option.DisplayName.GetLocalizedString();

    public AppearanceOption GetOption(AppearanceAxis axis, int index)
    {
        AxisDefinition definition = GetAxis(axis);
        if (definition?.Options == null || index < 0 || index >= definition.Options.Length)
            return null;
        return definition.Options[index];
    }

    /// <summary>Generic 경로로 표현 가능한(SciFiOnly가 아닌) 옵션 인덱스 목록. 전부 SciFiOnly거나 옵션이 없으면 전체 인덱스로 폴백(제한 없음).</summary>
    public List<int> GetGenericSelectableIndices(AppearanceAxis axis)
    {
        var result = new List<int>();
        AxisDefinition definition = GetAxis(axis);
        if (definition?.Options == null)
            return result;

        for (int i = 0; i < definition.Options.Length; i++)
        {
            if (definition.Options[i] != null && !definition.Options[i].SciFiOnly)
                result.Add(i);
        }

        if (result.Count == 0) // 안전 폴백: 전부 SciFiOnly면 제한하지 않는다
        {
            for (int i = 0; i < definition.Options.Length; i++)
                result.Add(i);
        }
        return result;
    }

    /// <summary>Generic 경로용 랜덤 옵션 인덱스 — SciFiOnly 값은 제외한다.</summary>
    public int GetRandomGenericIndex(AppearanceAxis axis)
    {
        List<int> indices = GetGenericSelectableIndices(axis);
        return indices.Count > 0 ? indices[Random.Range(0, indices.Count)] : 0;
    }

    /// <summary>Generic 경로용 랜덤 프로필 — 축마다 SciFiOnly가 아닌 옵션에서만 뽑는다. 옵션이 없는 축은 0.</summary>
    public AppearanceProfile CreateRandomProfile()
    {
        AppearanceProfile profile = default;
        for (int i = 0; i < AppearanceProfile.k_axisCount; i++)
        {
            var axis = (AppearanceAxis)i;
            profile.SetIndex(axis, GetRandomGenericIndex(axis));
        }
        return profile;
    }

    /// <summary>
    /// 공개 축들의 특징을 글 방식 몽타주 텍스트로 만든다 (GDD 10-3).
    /// 예: "머리색: 빨강 / 수염: 콧수염" — 무전 구두 전달이 핵심 재미라 이산 값 이름만 나열한다.
    ///
    /// <b>문장은 만든 쪽의 언어로 나온다 — 그래서 표시하는 피어에서 조립한다.</b>
    /// 수배 항목은 완성 문장이 아니라 프로필 인덱스 + 공개 축(<see cref="RevealedAxisSet"/>)만 실어 보내고,
    /// 본부 화면이 이 메서드로 각자 자기 언어로 조립한다 (#497 — 호스트·클라 언어가 갈려도 각자 언어로 보인다).
    /// </summary>
    public string BuildMontageText(in AppearanceProfile profile, RevealedAxisSet revealedAxes)
    {
        var builder = new StringBuilder();
        foreach (AppearanceAxis axis in revealedAxes)
        {
            AppearanceOption option = GetOption(axis, profile.GetIndex(axis));
            if (option == null)
                continue;

            if (builder.Length > 0)
                builder.Append(" / ");
            builder.Append(GetAxisName(axis)).Append(": ").Append(GetOptionName(option));
        }
        return builder.ToString();
    }
}
