using Unity.Netcode;
using UnityEngine;

/// <summary>
/// NPC 외형(메시·머티리얼) 랜덤 교체 + 네트워크 동기화. (#56)
/// 서버가 스폰 시 모델 인덱스를 뽑아 NetworkVariable로 동기화하고, 각 피어가
/// 자기 쪽 SkinnedMeshRenderer에 같은 모델을 적용한다 — 외형으로 용의자를
/// 대조·식별하는 게임이라 전 클라이언트가 반드시 같은 외형을 봐야 한다.
/// 네트워크를 켜지 않은 로컬 Play 테스트에서는 로컬에서 바로 랜덤 적용한다.
/// </summary>
public class NpcAppearance : NetworkBehaviour
{
    // 아직 인덱스가 정해지지 않음(프리팹 기본 외형)을 뜻하는 값
    private const int k_unassigned = -1;

    [Header("외형 후보 (캐릭터 모델 목록)")]
    [Tooltip("스폰 시 이 목록에서 랜덤으로 골라 외형(메시·머티리얼)을 교체한다. 비워두면 프리팹 기본 외형 그대로 사용. Synty 캐릭터는 같은 리그를 공유하므로 메시 교체만으로 애니메이션이 그대로 동작한다")]
    [SerializeField] private GameObject[] m_characterModels;

    // 서버 권위 모델 인덱스 — 서버만 쓰고 모든 클라이언트가 읽는다
    private readonly NetworkVariable<int> m_modelIndex = new NetworkVariable<int>(k_unassigned);

    public override void OnNetworkSpawn()
    {
        m_modelIndex.OnValueChanged += HandleModelIndexChanged;

        // 인덱스 추첨은 서버 권위 — 클라이언트는 동기화된 값을 받아 적용만 한다
        if (IsServer && m_modelIndex.Value == k_unassigned && m_characterModels != null && m_characterModels.Length > 0)
            m_modelIndex.Value = Random.Range(0, m_characterModels.Length);

        // 클라이언트는 스폰 페이로드에 이미 값이 실려 오므로 이 시점에 바로 적용된다
        ApplyModel(m_modelIndex.Value);
    }

    public override void OnNetworkDespawn()
    {
        m_modelIndex.OnValueChanged -= HandleModelIndexChanged;
    }

    private void Start()
    {
        // 오프라인 폴백 — 네트워크 세션 없이 Play한 로컬 테스트에서는 바로 랜덤 적용
        if (!IsSpawned && m_characterModels != null && m_characterModels.Length > 0)
            ApplyModel(Random.Range(0, m_characterModels.Length));
    }

    private void HandleModelIndexChanged(int previous, int current)
    {
        ApplyModel(current);
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
}
