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

        [Tooltip(
            "머리 앵커에 부착하는 프롭 (머리카락·수염·모자·안경 등). 색은 Color로 틴트된다.\n"
                + "여럿 넣으면 NPC마다 그중 하나를 쓴다 — 화면 다양성은 늘리되 몽타주는 한 값으로 남는다 (#619).\n"
                + "⚠ 여기 함께 넣는 메시는 반드시 몽타주에서 서로 구분되지 않아야 한다. 그림은 0번으로 한 장만 굽기 때문에,\n"
                + "구분되는 메시를 섞으면 그림이 실물과 어긋나 §1이 깨진다."
        )]
        public GameObject[] PropPrefabs;

        /// <summary>
        /// 몽타주가 대표로 쓰는 프롭 — 그림을 굽고 '이 값에 프롭이 있는가'를 판정하는 기준.
        /// 여러 메시를 물려도 그림은 한 장이라 대표가 하나여야 한다 (#619).
        /// </summary>
        public GameObject MontageProp =>
            PropPrefabs != null && PropPrefabs.Length > 0 ? PropPrefabs[0] : null;

        /// <summary>
        /// 이 NPC가 쓸 메시 하나. <paramref name="seed"/>가 같으면 어느 피어에서도 같은 것이 나온다 —
        /// 변형은 <see cref="AppearanceProfile"/>에 없어 네트워크로 오지 않으므로, 이미 동기화된 값
        /// (NetworkObjectId)에서 결정론적으로 뽑아야 전 클라이언트가 같은 외형을 본다 (#56).
        /// </summary>
        public GameObject PickProp(ulong seed)
        {
            if (PropPrefabs == null || PropPrefabs.Length == 0)
                return null;
            if (PropPrefabs.Length == 1)
                return PropPrefabs[0];

            // 곱셈 해시 — 인접한 NetworkObjectId가 같은 변형으로 몰리지 않게 흩는다
            ulong mixed = seed * 2654435761UL + 1013904223UL;
            return PropPrefabs[(int)(mixed % (ulong)PropPrefabs.Length)];
        }

        [Tooltip("SciFi 카탈로그 전용 값 — Generic 경로엔 프롭이 없어 표현 불가하므로 Generic 랜덤 배정에서 제외한다 (예: 머리 '가림', 후드/헬멧, 특수 피부색). 몽타주 텍스트·SciFi 카탈로그에는 그대로 쓰인다")]
        public bool SciFiOnly;

        [Tooltip("몽타주 포트레이트에서 이 값을 그리는 레이어 그림 (#607). 프롭이 있는 값은 Tools/몽타주 레이어 굽기로 자동 생성된다. 색 축(머리색·피부색)은 그림 없이 다른 레이어를 Color로 칠하므로 비운다. '없음/대머리'도 비운다 — 안 그리는 것이 곧 그 값이다")]
        public Sprite MontageLayer;

        [Tooltip("몽타주에서 이 값을 통째로 뺀다 — 그림도 안 꽂고 이 축이 공개 축 후보에서도 빠진다. 화면에는 그대로 착용한다.\n켜면 글로도 못 말하게 되는 것이 대가다. 그래서 정면 그림이 희미한 것만으로는 켜지 않는다 — 구분은 글이 하고, 희미한 정면 투영은 사실이라 그림이 거짓말을 하는 게 아니다. 머리 축에서 이 이유로 아무 값도 켜지 않았다 (#619, docs §13-7).\n켤 자리는 화면에서 무엇이 보이는지를 그림·글 어느 쪽으로도 옳게 말할 수 없는 값이다")]
        public bool ExcludeFromMontage;
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

    [Header("몽타주 포트레이트 (#607) — 축에 속하지 않는 공용 레이어")]
    [Tooltip("맨 아래에 깔리는 두상 실루엣. 피부색이 공개 축이면 이 그림이 그 색으로 칠해진다 — 그래서 명암·질감 없는 순백이어야 색이 제대로 나온다")]
    [SerializeField] private Sprite m_montageBase;

    [Tooltip("살 실루엣 위에 얹는 이목구비(눈·눈썹·입). 피부색과 무관하므로 칠하지 않는다 — 살 레이어를 통짜로 칠할 수 있는 것이 이걸 분리한 이유다")]
    [SerializeField] private Sprite m_montageFace;

    [Tooltip("머리 스타일은 미공개인데 머리색만 공개일 때 칠할 '형태 미상' 머리. 스타일을 말하지 않으면서 색을 얹을 자리를 만든다")]
    [SerializeField] private Sprite m_montageUnknownHair;

    /// <summary>포트레이트 바닥 레이어 — 피부색을 칠하는 대상.</summary>
    public Sprite MontageBase => m_montageBase;

    /// <summary>살 위에 얹는 이목구비 레이어 — 틴트하지 않는다.</summary>
    public Sprite MontageFace => m_montageFace;

    /// <summary>머리 스타일 미공개용 머리 레이어 — 머리색만 공개된 몽타주에서 색을 얹는 자리.</summary>
    public Sprite MontageUnknownHair => m_montageUnknownHair;

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

    /// <summary>공개되지 않은 축의 값 자리에 넣는 말 — "미상".</summary>
    public static string UnknownValueName => LocalizedStrings.Get(k_table, "Npc.Appearance.Unknown");

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

    /// <summary>
    /// 이 조합의 머리가 화면에 보이는가 — 머리 스타일 옵션에 프롭이 있는지로 판정한다. (#556)
    /// '없음(대머리)'·'가림'은 프롭이 없어 머리색이 화면에 나타날 자리가 없다.
    /// SciFi 카탈로그 경로도 같은 기준이다 — 프롭을 쓰지 않지만 머리 스타일 값은 같은 옵션 목록을 가리킨다.
    /// </summary>
    public bool HasVisibleHair(in AppearanceProfile profile) =>
        GetOption(AppearanceAxis.HairStyle, profile.HairStyleIndex)?.MontageProp != null;

    /// <summary>
    /// 이 값을 몽타주 포트레이트로 그릴 수 있는가 (#607) — 공개 축 후보를 거르는 기준이다 (#556과 같은 취지).
    /// SciFi 전용 값(가림·후드·풀헬멧·특수 안경)은 Generic 프롭이 없어 레이어를 자동 생성할 수 없다 —
    /// 그림이 없는 값이 공개되면 본부 화면이 빈 채로 남는다. 나중에 그림을 채우면 저절로 후보로 돌아온다.
    /// 색 축은 다른 레이어를 칠할 뿐이라 언제나 가능하고, '없음/대머리'는 안 그리는 것이 곧 그 값이다.
    /// </summary>
    public bool CanDepict(AppearanceAxis axis, int index)
    {
        if (axis == AppearanceAxis.HairColor || axis == AppearanceAxis.SkinColor)
            return true;

        AppearanceOption option = GetOption(axis, index);
        if (option == null)
            return false;

        // 정면에서 '없음'과 구분되지 않는 값 — 그리면 본부가 대머리와 같은 그림을 받는다 (#619)
        if (option.ExcludeFromMontage)
            return false;

        if (option.MontageLayer != null)
            return true;

        // 머리 스타일은 프롭이 없다는 것이 곧 '머리가 화면에 안 보인다'(대머리·가림)이고,
        // 그건 안 그리는 것으로 정확히 표현된다 — 무엇이 덮었는지는 모자 축이 말할 몫이다.
        if (axis == AppearanceAxis.HairStyle)
            return option.MontageProp == null;

        // 나머지 축에서 프롭 없는 SciFi 전용 값(후드·헬멧·바이저·발광렌즈)은 '그릴 것이 있는데
        // 그림이 없는' 경우다 — 안 그리면 '없음'으로 읽혀 화면과 어긋난다.
        return option.MontageProp == null && !option.SciFiOnly;
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
        for (int i = 0; i < AppearanceProfile.k_axisCount; i++)
        {
            var axis = (AppearanceAxis)i;
            if (builder.Length > 0)
                builder.Append(" / ");

            AppearanceOption option = revealedAxes.Contains(axis) ? GetOption(axis, profile.GetIndex(axis)) : null;
            builder.Append(GetAxisName(axis)).Append(": ").Append(option != null ? GetOptionName(option) : UnknownValueName);
        }
        return builder.ToString();
    }
}
