using Unity.Netcode;
using UnityEngine;

/// <summary>
/// NPC 외형(메시·머티리얼) 랜덤 교체 + 네트워크 동기화. (#56)
/// 서버가 스폰 시 모델 인덱스를 뽑아 NetworkVariable로 동기화하고, 각 피어가
/// 자기 쪽 SkinnedMeshRenderer에 같은 모델을 적용한다 — 외형으로 용의자를
/// 대조·식별하는 게임이라 전 클라이언트가 반드시 같은 외형을 봐야 한다.
/// 네트워크를 켜지 않은 로컬 Play 테스트에서는 로컬에서 바로 랜덤 적용한다.
///
/// 외형 특징 축(머리색/수염/액세서리 등)도 같은 방식으로 동기화한다. (#74)
/// 서버(AppearanceAssigner)가 SetProfile로 배정하면 인덱스만 전송되고,
/// 각 피어가 AppearanceDatabase에서 시각 리소스를 찾아 적용한다.
/// </summary>
public class NpcAppearance : NetworkBehaviour, IAppearanceProfileSource
{
    // 아직 인덱스가 정해지지 않음(프리팹 기본 외형)을 뜻하는 값
    private const int k_unassigned = -1;

    [Header("외형 후보 (바디 변형)")]
    [Tooltip(
        "바디 변형들이 자식으로 붙어 있는 컨테이너(보통 Model). 이 아래 SkinnedMeshRenderer를 가진 자식들을 바디 후보로 보고, 인덱스 순서대로 하나만 활성화한다. 비우면 이 오브젝트에서 탐색"
    )]
    [SerializeField]
    private Transform m_modelRoot;

    [Header("외형 특징 축 (#74)")]
    [Tooltip("축별 옵션 정의. 비워두면 특징 축 적용은 건너뛴다 (모델 교체만 동작)")]
    [SerializeField]
    private AppearanceDatabase m_appearanceDatabase;

    [Tooltip(
        "프롭(머리카락·수염·액세서리)을 붙일 머리 앵커. 비우면 휴머노이드 Head 본을 자동 탐색한다"
    )]
    [SerializeField]
    private Transform m_headAnchor;

    [Tooltip("MaterialOverride 옵션을 적용할 머티리얼 슬롯 — Synty 캐릭터는 아틀라스 1장이라 0")]
    [SerializeField]
    private int m_overrideMaterialSlot = 0;

    [Tooltip("프롭 틴트에 사용할 셰이더 프로퍼티 이름 (Synty/URP 셰이더는 _BaseColor)")]
    [SerializeField]
    private string m_colorPropertyName = "_BaseColor";

    [Tooltip("머리 프롭의 머리색 틴트에 쓰는 셰이더 프로퍼티. Synty Generic_Standard 셰이더는 머리 마스크 영역을 _Hair_Color로 칠하므로, _BaseColor로 틴트하면 머티리얼의 _Hair_Color(갈색)에 눌려 탁해진다. 이 채널로 직접 칠해야 순수한 머리색이 나온다")]
    [SerializeField]
    private string m_hairColorPropertyName = "_Hair_Color";

    [Tooltip(
        "머리 프롭에 깔 밝은 중립 베이스 머티리얼. 프롭 기본 아틀라스가 어두워 곱셈 틴트하면 밝은 머리색(금발·은발)이 탁해지므로, 머리 프롭 머티리얼을 이 중립 머티리얼로 교체한 뒤 HairColor를 틴트한다. 비우면 원본에 그대로 틴트(기존 동작)"
    )]
    [SerializeField]
    private Material m_hairBaseMaterial;

    [Tooltip("피부색 틴트에 쓰는 셰이더 프로퍼티 (Synty Generic 셰이더는 _Skin_Color)")]
    [SerializeField]
    private string m_skinColorPropertyName = "_Skin_Color";

    // 서버 권위 모델 인덱스 — 서버만 쓰고 모든 클라이언트가 읽는다
    private readonly NetworkVariable<int> m_modelIndex = new NetworkVariable<int>(k_unassigned);

    // 서버 권위 외형 특징 조합 — 축별 인덱스만 동기화된다 (#74)
    private readonly NetworkVariable<AppearanceProfile> m_syncedProfile =
        new NetworkVariable<AppearanceProfile>(AppearanceProfile.Unassigned);

    // 이 피어에 실제로 적용된 프로필 — 오프라인 폴백에서도 유지된다
    private AppearanceProfile m_appliedProfile = AppearanceProfile.Unassigned;

    // 축별로 부착한 프롭 인스턴스 — 재배정 시 교체를 위해 기억한다
    private readonly GameObject[] m_axisProps = new GameObject[AppearanceProfile.k_axisCount];

    // 바디 변형 렌더러 캐시 — 배열 인덱스 순서가 곧 네트워크 동기화 기준
    private SkinnedMeshRenderer[] m_bodyVariants;

    // 현재 활성 바디 — 피부색·바디 틴트 대상
    private SkinnedMeshRenderer m_activeBody;

    /// <summary>바디 변형 후보. 컨테이너 아래 SkinnedMeshRenderer들을 계층 순서대로 캐시한다.</summary>
    private SkinnedMeshRenderer[] BodyVariants
    {
        get
        {
            if (m_bodyVariants == null)
            {
                Transform root = m_modelRoot != null ? m_modelRoot : transform;
                m_bodyVariants = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            }
            return m_bodyVariants;
        }
    }

    /// <summary>현재 적용된 외형 특징 조합. 배정 전에는 Unassigned.</summary>
    public AppearanceProfile Profile => m_appliedProfile;

    public override void OnNetworkSpawn()
    {
        m_modelIndex.OnValueChanged += HandleModelIndexChanged;
        m_syncedProfile.OnValueChanged += HandleProfileChanged;

        // 인덱스 추첨은 서버 권위 — 클라이언트는 동기화된 값을 받아 적용만 한다
        if (IsServer && m_modelIndex.Value == k_unassigned && BodyVariants.Length > 0)
            m_modelIndex.Value = Random.Range(0, BodyVariants.Length);

        // 클라이언트는 스폰 페이로드에 이미 값이 실려 오므로 이 시점에 바로 적용된다
        ApplyModel(m_modelIndex.Value);

        // 늦게 접속한 클라이언트는 프로필도 스폰 페이로드에 실려 온다
        if (m_syncedProfile.Value.IsAssigned)
            ApplyProfile(m_syncedProfile.Value);
    }

    public override void OnNetworkDespawn()
    {
        m_modelIndex.OnValueChanged -= HandleModelIndexChanged;
        m_syncedProfile.OnValueChanged -= HandleProfileChanged;
    }

    private void Start()
    {
        // 오프라인 폴백 — 네트워크 세션 없이 Play한 로컬 테스트에서는 바로 랜덤 적용
        if (!IsSpawned && BodyVariants.Length > 0)
            ApplyModel(Random.Range(0, BodyVariants.Length));
    }

    /// <summary>
    /// 외형 특징 조합을 배정한다. 네트워크 세션에서는 서버 전용 —
    /// NetworkVariable로 전 클라이언트에 인덱스가 동기화된다. (#74)
    /// 오프라인(로컬 Play)에서는 바로 적용한다.
    /// </summary>
    public void SetProfile(AppearanceProfile profile)
    {
        if (IsSpawned)
        {
            if (!IsServer)
            {
                Debug.LogWarning(
                    "NpcAppearance: 외형 특징 배정은 서버 권위 — 클라이언트 호출 무시",
                    this
                );
                return;
            }
            m_syncedProfile.Value = profile; // OnValueChanged로 서버 포함 전 피어에 적용된다
        }
        else
        {
            ApplyProfile(profile);
        }
    }

    private void HandleModelIndexChanged(int previous, int current)
    {
        ApplyModel(current);

        // 바디가 바뀌면 이전 바디에만 입힌 피부색·틴트가 사라지므로 특징 축을 다시 입힌다
        if (m_appliedProfile.IsAssigned)
            ApplyProfile(m_appliedProfile);
    }

    private void HandleProfileChanged(AppearanceProfile previous, AppearanceProfile current)
    {
        ApplyProfile(current);
    }

    /// <summary>
    /// 바디 변형 목록에서 index번 하나만 활성화하고 나머지는 끈다.
    /// 모든 바디가 같은 Root 스켈레톤에 이미 바인딩돼 있어(프리팹 토글 방식)
    /// mesh 교체 없이 SetActive만으로 외형이 바뀌고 본 배열이 유지된다.
    /// </summary>
    private void ApplyModel(int index)
    {
        SkinnedMeshRenderer[] variants = BodyVariants;
        if (variants == null || index < 0 || index >= variants.Length)
            return;

        for (int i = 0; i < variants.Length; i++)
        {
            if (variants[i] != null)
                variants[i].gameObject.SetActive(i == index);
        }
        m_activeBody = variants[index];
    }

    /// <summary>축별 옵션의 시각 리소스를 데이터베이스에서 찾아 이 NPC에 입힌다. (#74)</summary>
    private void ApplyProfile(AppearanceProfile profile)
    {
        m_appliedProfile = profile;
        if (!profile.IsAssigned || m_appearanceDatabase == null)
            return;

        // 1) 프롭 축
        ApplyPropAxis(AppearanceAxis.HairStyle, profile);
        ApplyPropAxis(AppearanceAxis.FacialHair, profile);
        ApplyPropAxis(AppearanceAxis.Headwear, profile);
        ApplyPropAxis(AppearanceAxis.Eyewear, profile);

        // 2) 머리색 — Synty 셰이더의 머리 채널(_Hair_Color)로 칠해야 갈색 마스크에 눌리지 않는다
        TintPropAxis(AppearanceAxis.HairColor, AppearanceAxis.HairStyle, profile, m_hairColorPropertyName);

        // 3) 피부색
        ApplySkinColor(AppearanceAxis.SkinColor, profile);
    }

    /// <summary> 프롭 축 하나를 적용한다.
    /// — 기존 프롭 제거 후 새 프롭 부착(옵션 색으로 틴트).
    /// 옵션에 프롭이 없으면('없음/대머리') 미부착이 정상.</summary>
    private void ApplyPropAxis(AppearanceAxis axis, in AppearanceProfile profile)
    {
        AppearanceDatabase.AppearanceOption option = m_appearanceDatabase.GetOption(
            axis,
            profile.GetIndex(axis)
        );
        int slot = (int)axis;

        if (m_axisProps[slot] != null)
            Destroy(m_axisProps[slot]);
        m_axisProps[slot] = null;

        // 한 값이 메시를 여럿 가질 수 있다 (#619). 어느 것을 쓸지는 프로필에 없어 네트워크로 오지 않으므로,
        // 이미 동기화된 NetworkObjectId에서 결정론적으로 뽑는다 — 안 그러면 피어마다 다른 머리가 보인다.
        // 축을 섞는 것은 한 NPC의 모든 축이 같은 자리 변형으로 몰리지 않게 하기 위한 것이다.
        ulong seed = (IsSpawned ? NetworkObjectId : (ulong)GetInstanceID()) * 31UL + (ulong)axis;
        GameObject prefab = option?.PickProp(seed);
        if (prefab == null)
            return;

        Transform anchor = ResolveHeadAnchor();
        if (anchor == null)
        {
            Debug.LogWarning($"[NpcAppearance] 머리 앵커를 찾지 못해 {axis} 프롭 부착 생략", this);
            return;
        }

        GameObject prop = Instantiate(prefab, anchor, false);
        // 머리 프롭은 밝은 중립 베이스로 갈아끼워야 HairColor 곱셈 틴트가 선명하다 (#221)
        if (axis == AppearanceAxis.HairStyle && m_hairBaseMaterial != null)
            ApplyBaseMaterial(prop, m_hairBaseMaterial);
        TintRenderers(prop, option.Color);
        m_axisProps[slot] = prop;
    }

    /// <summary>프롭의 모든 렌더러 머티리얼을 중립 베이스로 교체한다.
    /// 곱셈 틴트가 프롭 원본(어두운 아틀라스)에 눌리지 않게 밝은 베이스를 깔 때 쓴다.
    /// 공유 머티리얼을 그대로 대입 — 색은 이후 MaterialPropertyBlock으로 렌더러별 적용(인스턴스 누수 없음).</summary>
    private static void ApplyBaseMaterial(GameObject prop, Material baseMaterial)
    {
        foreach (Renderer renderer in prop.GetComponentsInChildren<Renderer>())
        {
            Material[] mats = renderer.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
                mats[i] = baseMaterial;
            renderer.sharedMaterials = mats;
        }
    }

    /// <summary>프롭 부착 지점. 인스펙터 지정 → 휴머노이드 Head 본 → 이름 검색 순으로 찾는다.</summary>
    private Transform ResolveHeadAnchor()
    {
        if (m_headAnchor != null)
            return m_headAnchor;

        Animator animator = GetComponentInChildren<Animator>();
        if (animator != null && animator.isHuman)
            m_headAnchor = animator.GetBoneTransform(HumanBodyBones.Head);

        if (m_headAnchor == null)
            m_headAnchor = FindChildByName(transform, "Head");

        return m_headAnchor;
    }

    /// <summary>색 전용 축을 다른 축의 프롭에 틴트. (예: 머리색 -> 머리스타일 프롭)
    /// 대상 프롭이 없으면 건너 뛴다.</summary>
    private void TintPropAxis(
        AppearanceAxis colorAxis,
        AppearanceAxis targetPropAxis,
        in AppearanceProfile profile,
        string propertyName
    )
    {
        GameObject prop = m_axisProps[(int)targetPropAxis];
        if (prop == null)
            return;

        AppearanceDatabase.AppearanceOption option = m_appearanceDatabase.GetOption(
            colorAxis,
            profile.GetIndex(colorAxis)
        );
        if (option != null)
            TintRenderers(prop, option.Color, propertyName);
    }

    /// <summary>프롭의 모든 렌더러에 옵션 색을 틴트한다 — 공유 머티리얼은 건드리지 않는다.</summary>
    private void TintRenderers(GameObject prop, Color color) =>
        TintRenderers(prop, color, m_colorPropertyName);

    /// <summary>지정한 셰이더 프로퍼티로 프롭 렌더러를 틴트한다 (렌더러별 MaterialPropertyBlock — 공유 머티리얼 불변).</summary>
    private void TintRenderers(GameObject prop, Color color, string propertyName)
    {
        var block = new MaterialPropertyBlock();
        foreach (Renderer renderer in prop.GetComponentsInChildren<Renderer>())
        {
            renderer.GetPropertyBlock(block);
            block.SetColor(propertyName, color);
            renderer.SetPropertyBlock(block);
        }
    }

    /// <summary>피부색을 바디 렌더러에 적용한다.
    /// '메탈' 등 머티리얼 교체가 필요한 옵션은 MaterialOverride로,
    /// 일반 피부톤은 마스크된 _Skin_Color로.
    /// 둘 다 렌더러별 적용이라 공유 머티리얼은 불변.</summary>
    private void ApplySkinColor(AppearanceAxis axis, in AppearanceProfile profile)
    {
        AppearanceDatabase.AppearanceOption option = m_appearanceDatabase.GetOption(
            axis,
            profile.GetIndex(axis)
        );
        if (option == null)
            return;

        SkinnedMeshRenderer body =
            m_activeBody != null ? m_activeBody : GetComponentInChildren<SkinnedMeshRenderer>();
        if (body == null)
            return;

        if (option.MaterialOverride != null)
        {
            Material[] materials = body.sharedMaterials;
            int slot = Mathf.Clamp(m_overrideMaterialSlot, 0, materials.Length - 1);
            materials[slot] = option.MaterialOverride;
            body.sharedMaterials = materials;
            return;
        }

        var block = new MaterialPropertyBlock();
        body.GetPropertyBlock(block);
        block.SetColor(m_skinColorPropertyName, option.Color);
        body.SetPropertyBlock(block);
    }

    private static Transform FindChildByName(Transform root, string name)
    {
        foreach (Transform child in root.GetComponentsInChildren<Transform>())
        {
            if (child.name.Contains(name))
                return child;
        }
        return null;
    }
}
