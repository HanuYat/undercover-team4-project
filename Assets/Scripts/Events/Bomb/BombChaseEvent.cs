using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

/// <summary>
/// 추격 폭탄 (돌발 이벤트 · 현장) — 도시에 놓인 상자(<see cref="BombCrate"/>) 중 한 곳에서 폭탄이 나와
/// 근처에 사람이 오면 쫓아오고, 제한시간이 끝나면 그 자리에서 폭발한다. 해체는 없다 — 달아나거나
/// 진압봉으로 밀어내는 수밖에 없다. (GDD 6-4, #399)
///
/// 스폰형 이벤트 — 폭탄 프리팹(<see cref="BombDevice"/>)을 스폰하고 수명·정리만 맡는다. 등장·대기·추격·
/// 폭발은 스폰물이 스스로 서버 권위로 처리하고 자기 NetworkObject로 전파한다 (ISuddenEvent 규약,
/// JailbreakEvent ↔ NpcController와 동일 관계).
///
/// <b>등장 지점을 이 컴포넌트가 들고 있지 않다</b> — 씬에 놓인 상자가 곧 후보다. 상자는 평소에도 도시
/// 소품으로 서 있어서 "저기서 나올 수 있다"를 미리 볼 수 있고, 지점을 옮기는 일이 곧 상자를 옮기는 일이라
/// 인스펙터 배열과 실제 상자가 어긋날 여지가 없다.
///
/// 라운드당 폭탄 1개 — <see cref="IsActive"/>가 폭탄이 살아 있는 동안 true라, 프레임워크가 겹쳐 발생시키지 않는다.
/// </summary>
[RequireComponent(typeof(SuddenEventManager))]
public class BombChaseEvent : MonoBehaviour, ISuddenEvent
{
    [Header("폭탄 프리팹 (BombDevice)")]
    [SerializeField]
    private BombDevice m_bombPrefab;

    [Header("등장")]
    [Tooltip("상자에서 이 거리(m) 안의 NavMesh를 찾아 그 위에 올려놓는다 — 못 찾으면 다음 상자를 시도한다")]
    [SerializeField]
    private float m_navSampleMaxDistance = 5f;

    [Header("정리")]
    [Tooltip("폭발 후 잔류 연출(넉백·VFX)을 보여줄 시간(초) — 지나면 폭탄을 치운다")]
    [SerializeField]
    private float m_resolvedLingerSeconds = 3f;

    private BombDevice m_bomb;
    private bool m_resolved;
    private float m_despawnAt;

    public string DisplayName => "추격 폭탄";

    public bool IsActive => m_bomb != null;

    public bool CanTrigger()
    {
        if (m_bombPrefab == null)
            return false;
        if (BombCrate.All.Count == 0)
            return false; // 씬에 상자가 없다 — 나올 곳이 없다
        // 쫓아갈 현장 플레이어가 있어야 한다 — 전원 다운이면 걸지 않는다
        return SuddenEventUtil.FindRandomFieldPlayer() != null;
    }

    public void ServerBegin()
    {
        if (m_bombPrefab == null)
            return;

        if (!TryPickSpawn(out Vector3 position, out Quaternion rotation))
        {
            Debug.LogWarning("BombChaseEvent: 상자 주변에서 NavMesh를 찾지 못해 발동 취소", this);
            return;
        }

        m_bomb = Instantiate(m_bombPrefab, position, rotation);
        if (SuddenEventUtil.IsNetworkSessionActive)
            m_bomb.GetComponent<NetworkObject>().Spawn();

        m_bomb.OnExploded += HandleExploded;
        m_resolved = false;

        m_bomb.ServerDeploy();
    }

    public void ServerTick()
    {
        if (m_bomb == null)
            return;

        // 폭발 후 잔류 연출 시간이 지나면 정리한다.
        if (m_resolved && Time.time >= m_despawnAt)
            Despawn();
    }

    public void ServerReset()
    {
        // 라운드 종료 등 — 폭탄을 즉시 치운다.
        Despawn();
    }

    private void HandleExploded()
    {
        if (m_resolved)
            return;

        m_resolved = true;
        m_despawnAt = Time.time + m_resolvedLingerSeconds;
        Debug.Log("[돌발이벤트] 추격 폭탄 — 폭발");
    }

    private void Despawn()
    {
        if (m_bomb == null)
            return;

        m_bomb.OnExploded -= HandleExploded;
        SuddenEventUtil.DespawnOrDestroy(m_bomb.gameObject);
        m_bomb = null;
        m_resolved = false;
    }

    /// <summary>
    /// 상자를 무작위로 고르고 그 자리의 NavMesh 지점을 얻는다 — 하나도 쓸 수 없으면 false.
    ///
    /// 폭탄은 NavMeshAgent로 움직이므로 시작 지점이 조금이라도 떠 있거나 인도 밖이면 에이전트가 아예
    /// 붙지 못해 그 자리에서 굳는다 (NpcSpawner와 같은 이유). 그래서 상자 하나가 실패해도 이벤트를
    /// 통째로 버리지 않고 다음 상자를 본다 — 상자 하나가 나중에 옮겨져 NavMesh를 벗어나도
    /// 이벤트 자체가 조용히 죽지는 않는다.
    /// </summary>
    private bool TryPickSpawn(out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.identity;

        int count = BombCrate.All.Count;
        if (count == 0)
            return false;

        int start = Random.Range(0, count);
        for (int i = 0; i < count; i++)
        {
            BombCrate crate = BombCrate.All[(start + i) % count];
            if (crate == null)
                continue;

            if (!NavMesh.SamplePosition(crate.SpawnPosition, out NavMeshHit hit, m_navSampleMaxDistance, NavMesh.AllAreas))
                continue;

            position = hit.position;
            rotation = crate.SpawnRotation;
            return true;
        }

        return false;
    }
}
