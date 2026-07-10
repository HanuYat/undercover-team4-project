using Unity.Netcode;
using UnityEngine;

/// <summary>
/// NPC 외형(메시·머티리얼) 랜덤 교체 + 네트워크 동기화. (#56)
/// 서버가 스폰 시 모델 인덱스를 뽑아 NetworkVariable로 동기화하고, 각 피어가
/// 자기 쪽 SkinnedMeshRenderer에 같은 모델을 적용한다 — 외형으로 용의자를
/// 대조·식별하는 게임이라 전 클라이언트가 반드시 같은 외형을 봐야 한다.
/// 네트워크를 켜지 않은 로컬 Play 테스트에서는 로컬에서 바로 랜덤 적용한다.
///
/// 외형 특징 축(머리색/수염/액세서리)도 같은 방식으로 동기화한다. (#74)
/// 서버(AppearanceAssigner)가 SetProfile로 배정하면 인덱스만 전송되고,
/// 각 피어가 AppearanceDatabase에서 시각 리소스를 찾아 적용한다.
/// </summary>
public class NpcAppearance : NetworkBehaviour
{
    // 아직 인덱스가 정해지지 않음(프리팹 기본 외형)을 뜻하는 값
    private const int k_unassigned = -1;

    [Header("외형 후보 (캐릭터 모델 목록)")]
    [Tooltip("스폰 시 이 목록에서 랜덤으로 골라 외형(메시·머티리얼)을 교체한다. 비워두면 프리팹 기본 외형 그대로 사용. Synty 캐릭터는 같은 리그를 공유하므로 메시 교체만으로 애니메이션이 그대로 동작한다")]
    [SerializeField] private GameObject[] m_characterModels;

    [Header("외형 특징 축 (#74)")]
    [Tooltip("축별 옵션 정의. 비워두면 특징 축 적용은 건너뛴다 (모델 교체만 동작)")]
    [SerializeField] private AppearanceDatabase m_appearanceDatabase;

    [Tooltip("프롭(머리카락·수염·액세서리)을 붙일 머리 앵커. 비우면 휴머노이드 Head 본을 자동 탐색한다")]
    [SerializeField] private Transform m_headAnchor;

    [Tooltip("MaterialOverride 옵션을 적용할 머티리얼 슬롯 — Synty 캐릭터는 아틀라스 1장이라 0")]
    [SerializeField] private int m_overrideMaterialSlot = 0;

    [Tooltip("프롭 틴트에 사용할 셰이더 프로퍼티 이름 (Synty/URP 셰이더는 _BaseColor)")]
    [SerializeField] private string m_colorPropertyName = "_BaseColor";

    // 서버 권위 모델 인덱스 — 서버만 쓰고 모든 클라이언트가 읽는다
    private readonly NetworkVariable<int> m_modelIndex = new NetworkVariable<int>(k_unassigned);

    // 서버 권위 외형 특징 조합 — 축별 인덱스만 동기화된다 (#74)
    private readonly NetworkVariable<AppearanceProfile> m_syncedProfile =
        new NetworkVariable<AppearanceProfile>(AppearanceProfile.Unassigned);

    // 이 피어에 실제로 적용된 프로필 — 오프라인 폴백에서도 유지된다
    private AppearanceProfile m_appliedProfile = AppearanceProfile.Unassigned;

    // 축별로 부착한 프롭 인스턴스 — 재배정 시 교체를 위해 기억한다
    private readonly GameObject[] m_axisProps = new GameObject[AppearanceProfile.k_axisCount];

    /// <summary>현재 적용된 외형 특징 조합. 배정 전에는 Unassigned.</summary>
    public AppearanceProfile Profile => m_appliedProfile;

    public override void OnNetworkSpawn()
    {
        m_modelIndex.OnValueChanged += HandleModelIndexChanged;
        m_syncedProfile.OnValueChanged += HandleProfileChanged;

        // 인덱스 추첨은 서버 권위 — 클라이언트는 동기화된 값을 받아 적용만 한다
        if (IsServer && m_modelIndex.Value == k_unassigned && m_characterModels != null && m_characterModels.Length > 0)
            m_modelIndex.Value = Random.Range(0, m_characterModels.Length);

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
        if (!IsSpawned && m_characterModels != null && m_characterModels.Length > 0)
            ApplyModel(Random.Range(0, m_characterModels.Length));
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
                Debug.LogWarning("NpcAppearance: 외형 특징 배정은 서버 권위 — 클라이언트 호출 무시", this);
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

        // 모델 교체는 sharedMaterials를 통째로 갈아끼우므로 특징 축 시각을 다시 입힌다
        if (m_appliedProfile.IsAssigned)
            ApplyProfile(m_appliedProfile);
    }

    private void HandleProfileChanged(AppearanceProfile previous, AppearanceProfile current)
    {
        ApplyProfile(current);
    }

    /// <summary>
    /// 모델 목록의 index번 모델로 외형을 교체한다. 모델 오브젝트 전체를 갈아끼우지 않고
    /// SkinnedMeshRenderer의 메시·머티리얼만 바꾸므로 Animator·NpcAnimationDriver가
    /// 잡고 있는 참조가 깨지지 않는다. (기존 NpcSpawner.ApplyRandomAppearance에서 이관)
    /// </summary>
    private void ApplyModel(int index)
    {
        if (m_characterModels == null || index < 0 || index >= m_characterModels.Length)
            return;

        GameObject source = m_characterModels[index];
        if (source == null)
            return;

        SkinnedMeshRenderer sourceRenderer = source.GetComponentInChildren<SkinnedMeshRenderer>();
        SkinnedMeshRenderer targetRenderer = GetComponentInChildren<SkinnedMeshRenderer>();
        if (sourceRenderer == null || targetRenderer == null)
        {
            Debug.LogWarning($"NpcAppearance: SkinnedMeshRenderer를 찾지 못해 외형 교체 생략 ({source.name})", this);
            return;
        }

        targetRenderer.sharedMesh = sourceRenderer.sharedMesh;
        targetRenderer.sharedMaterials = sourceRenderer.sharedMaterials;
    }

    /// <summary>축별 옵션의 시각 리소스를 데이터베이스에서 찾아 이 NPC에 입힌다. (#74)</summary>
    private void ApplyProfile(AppearanceProfile profile)
    {
        m_appliedProfile = profile;

        if (!profile.IsAssigned || m_appearanceDatabase == null)
            return;

        for (int i = 0; i < AppearanceProfile.k_axisCount; i++)
        {
            var axis = (AppearanceAxis)i;
            ApplyAxisVisual(axis, profile.GetIndex(axis));
        }
    }

    /// <summary>
    /// 축 하나의 옵션을 적용한다. 프롭 부착(옵션 색으로 틴트) → 머티리얼 슬롯 교체 순.
    /// Synty 캐릭터는 아틀라스 1장이라 부위별 색 변경이 불가능해 모든 축을 프롭으로 표현한다
    /// — 전신 틴트는 피부까지 물들어 제외 (팀 결정). 프롭·머티리얼 없는 옵션(예: '없음')은 시각 없음이 정상.
    /// </summary>
    private void ApplyAxisVisual(AppearanceAxis axis, int optionIndex)
    {
        AppearanceDatabase.AppearanceOption option = m_appearanceDatabase.GetOption(axis, optionIndex);
        if (option == null)
        {
            Debug.LogWarning($"NpcAppearance: {axis} 축에 옵션 {optionIndex}가 없어 적용 생략", this);
            return;
        }

        // 재배정·모델 교체 대비 — 이 축의 기존 프롭을 제거하고 새로 입힌다
        int axisSlot = (int)axis;
        if (m_axisProps[axisSlot] != null)
            Destroy(m_axisProps[axisSlot]);

        if (option.PropPrefab != null)
        {
            Transform anchor = ResolveHeadAnchor();
            if (anchor == null)
            {
                Debug.LogWarning($"NpcAppearance: 머리 앵커를 찾지 못해 {axis} 프롭 부착 생략", this);
                return;
            }

            GameObject prop = Instantiate(option.PropPrefab, anchor, false);
            TintRenderers(prop, option.Color);
            m_axisProps[axisSlot] = prop;
            return;
        }

        if (option.MaterialOverride != null)
        {
            SkinnedMeshRenderer body = GetComponentInChildren<SkinnedMeshRenderer>();
            if (body == null)
                return;

            Material[] materials = body.sharedMaterials;
            int slot = Mathf.Clamp(m_overrideMaterialSlot, 0, materials.Length - 1);
            materials[slot] = option.MaterialOverride;
            body.sharedMaterials = materials;
        }
    }

    /// <summary>프롭의 모든 렌더러에 옵션 색을 틴트한다 — 공유 머티리얼은 건드리지 않는다.</summary>
    private void TintRenderers(GameObject prop, Color color)
    {
        var block = new MaterialPropertyBlock();
        foreach (Renderer renderer in prop.GetComponentsInChildren<Renderer>())
        {
            renderer.GetPropertyBlock(block);
            block.SetColor(m_colorPropertyName, color);
            renderer.SetPropertyBlock(block);
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
