using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 필드에 다수의 NPC를 스폰하는 스포너. (이슈 #37)
/// 여러 스폰 포인트를 돌아가며 지정된 수만큼 NPC를 생성하고,
/// 스폰 위치는 NavMesh 위 지점으로 보정해 배회가 항상 동작하게 한다.
/// </summary>
// TODO: 네트워크 전환(#56) 시 서버에서만 스폰하고 NetworkObject.Spawn으로 동기화
public class NpcSpawner : MonoBehaviour
{
    [Header("NPC 프리팹")]
    [SerializeField] private NpcController m_npcPrefab;

    [Header("총 스폰 수")]
    [SerializeField] private int m_spawnCount = 15;

    [Header("스폰 포인트")]
    [Tooltip("비워두면 이 오브젝트의 자식 Transform들을 스폰 포인트로 사용한다")]
    [SerializeField] private Transform[] m_spawnPoints;

    [Header("스폰 분산 반경")]
    [Tooltip("각 스폰 포인트를 중심으로 이 반경(m) 안에 랜덤하게 흩어 배치한다")]
    [SerializeField] private float m_spawnRadius = 5f;

    [Header("NavMesh 보정 최대 거리")]
    [Tooltip("랜덤 위치에서 이 거리(m) 안에 NavMesh가 없으면 그 위치는 버리고 다시 뽑는다")]
    [SerializeField] private float m_sampleMaxDistance = 4f;

    [Header("외형 다양화 (캐릭터 모델 목록)")]
    [Tooltip("스폰 시 이 목록에서 랜덤으로 골라 외형(메시·머티리얼)을 교체한다. 비워두면 프리팹 기본 외형 그대로 사용. Synty 캐릭터는 같은 리그를 공유하므로 메시 교체만으로 애니메이션이 그대로 동작한다")]
    [SerializeField] private GameObject[] m_characterModels;

    private readonly List<NpcController> m_spawnedNpcs = new List<NpcController>();

    /// <summary>스폰된 NPC 목록. (#38 범인 랜덤 배정 등 후속 시스템에서 사용)</summary>
    public IReadOnlyList<NpcController> SpawnedNpcs => m_spawnedNpcs;

    private void Awake()
    {
        // 인스펙터에 스폰 포인트를 따로 지정하지 않았으면 자식들을 그대로 사용한다
        if (m_spawnPoints == null || m_spawnPoints.Length == 0)
        {
            m_spawnPoints = new Transform[transform.childCount];
            for (int i = 0; i < transform.childCount; i++)
                m_spawnPoints[i] = transform.GetChild(i);
        }
    }

    private void Start()
    {
        SpawnAll();
    }

    private void SpawnAll()
    {
        if (m_npcPrefab == null || m_spawnPoints.Length == 0)
        {
            Debug.LogWarning("NpcSpawner: NPC 프리팹 또는 스폰 포인트가 설정되지 않음", this);
            return;
        }

        int spawned = 0;
        int attempts = 0;
        int maxAttempts = m_spawnCount * 10; // NavMesh 보정 실패가 반복돼도 무한 루프에 빠지지 않도록 상한을 둔다

        while (spawned < m_spawnCount && attempts < maxAttempts)
        {
            attempts++;

            // 스폰 포인트를 순환하며 사용해 특정 지점에만 몰리는 것을 막는다
            Transform point = m_spawnPoints[spawned % m_spawnPoints.Length];
            Vector2 offset = Random.insideUnitCircle * m_spawnRadius;
            Vector3 candidate = point.position + new Vector3(offset.x, 0f, offset.y);

            // NavMesh 위 지점으로 보정 — NavMesh 밖에 스폰되면 NavMeshAgent가 동작하지 않아 배회가 멈춘다
            if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, m_sampleMaxDistance, NavMesh.AllAreas))
                continue;

            Quaternion rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            NpcController npc = Instantiate(m_npcPrefab, hit.position, rotation, transform);
            ApplyRandomAppearance(npc);
            m_spawnedNpcs.Add(npc);
            spawned++;
        }

        if (spawned < m_spawnCount)
            Debug.LogWarning($"NpcSpawner: {m_spawnCount}마리 중 {spawned}마리만 스폰됨 — 스폰 포인트가 NavMesh 근처에 있는지 확인 필요", this);
    }

    /// <summary>
    /// 모델 목록에서 랜덤으로 하나를 골라 NPC의 외형(메시·머티리얼)을 교체한다.
    /// 모델 오브젝트 전체를 갈아끼우지 않고 SkinnedMeshRenderer의 메시만 바꾸므로
    /// Animator·NpcAnimationDriver가 잡고 있는 참조가 깨지지 않는다.
    /// </summary>
    private void ApplyRandomAppearance(NpcController npc)
    {
        if (m_characterModels == null || m_characterModels.Length == 0)
            return;

        GameObject source = m_characterModels[Random.Range(0, m_characterModels.Length)];
        if (source == null)
            return;

        SkinnedMeshRenderer sourceRenderer = source.GetComponentInChildren<SkinnedMeshRenderer>();
        SkinnedMeshRenderer targetRenderer = npc.GetComponentInChildren<SkinnedMeshRenderer>();
        if (sourceRenderer == null || targetRenderer == null)
        {
            Debug.LogWarning($"NpcSpawner: SkinnedMeshRenderer를 찾지 못해 외형 교체 생략 ({source.name})", this);
            return;
        }

        targetRenderer.sharedMesh = sourceRenderer.sharedMesh;
        targetRenderer.sharedMaterials = sourceRenderer.sharedMaterials;
    }

    // 씬 뷰에서 스폰 포인트 위치와 분산 반경을 눈으로 확인할 수 있게 기즈모를 그린다
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;

        if (m_spawnPoints != null && m_spawnPoints.Length > 0)
        {
            foreach (Transform point in m_spawnPoints)
            {
                if (point != null)
                    Gizmos.DrawWireSphere(point.position, m_spawnRadius);
            }
        }
        else
        {
            // 스폰 포인트 미지정 시 실제로 사용될 자식들을 미리 보여준다
            foreach (Transform child in transform)
                Gizmos.DrawWireSphere(child.position, m_spawnRadius);
        }
    }
}
