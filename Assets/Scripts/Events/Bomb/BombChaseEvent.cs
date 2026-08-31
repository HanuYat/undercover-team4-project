using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

/// <summary>
/// 추격 폭탄 (돌발 이벤트 · 현장) — 도시에 놓인 상자(<see cref="BombCrate"/>) 중 한 곳에서 폭탄이
/// 나와 근처에 사람이 오면 쫓아오고, 제한시간이 끝나면 터진다. 해체는 없다. (GDD 6-4, #399)
///
/// 스폰형 이벤트 — 폭탄 프리팹(<see cref="BombDevice"/>)을 스폰하고 수명·정리만 맡는다. 등장·대기·
/// 추격·폭발은 스폰물이 스스로 서버 권위로 처리한다 (JailbreakEvent ↔ NpcController와 같은 관계).
/// <b>등장 지점을 이 컴포넌트가 들지 않는다</b> — 씬의 상자가 곧 후보다. 평소에도 소품으로 서 있어
/// "저기서 나올 수 있다"가 미리 보이고, 인스펙터 배열과 실제 상자가 어긋날 여지도 없다.
/// 라운드당 1개 — <see cref="IsActive"/>가 true인 동안 프레임워크가 겹쳐 발생시키지 않는다.
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
    [Tooltip(
        "폭발 후 폭탄을 남겨 둘 시간(초) — 기본 0이면 터지는 순간 사라진다. "
            + "연출을 붙잡아 두는 값이 아니다: 폭발 이펙트는 월드에 독립 스폰돼 자기 수명을 따로 "
            + "관리하고(BombExplosionView) 넉백도 폭발 순간에 끝나므로, 남겨 봤자 <b>멀쩡한 폭탄 "
            + "모델</b>만 서 있다"
    )]
    [SerializeField]
    private float m_resolvedLingerSeconds;

    private BombDevice m_bomb;
    private bool m_resolved;
    private float m_despawnAt;

    public string DisplayName => "추격 폭탄";

#if UNITY_EDITOR
    /// <summary>개발자 단축키(<see cref="BombDevHotkeys"/>)가 같은 폭탄을 놓기 위한 접근자 — 에디터 전용.
    /// 프리팹 참조를 두 벌 물리면 한쪽만 갈아 끼워도 조용히 어긋나므로 이 하나를 공유한다.</summary>
    public BombDevice DevBombPrefab => m_bombPrefab;
#endif

    public bool IsActive => m_bomb != null;

    /// <summary>
    /// 조용히 시작한다 (팀 확정 2026-08-13) — 폭탄은 <b>자기 연출로 알린다</b>. 상자에서 솟는 등장
    /// (<see cref="BombEmergeView"/>) · 머리 위 타이머(<see cref="BombTimerView"/>) · 위치 표시
    /// (<see cref="BombLocatorHud"/>)가 이미 "지금 어디서 무슨 일이 벌어지는지"를 전부 말한다.
    /// 그 위에 토스트를 얹으면 같은 사실을 두 번 말하는 셈이고, <b>본부에도 그대로 새어</b>
    /// 상자를 눈으로 찾을 이유가 없어진다 — 등장 지점을 미리 보여 주는 상자 설계(위 문서)와 어긋난다.
    /// </summary>
    public bool AnnounceOnBegin => false;

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

        // 잔류 시간이 지나면 치운다 — 기본값(0)이면 폭발 다음 틱이다.
        // <b>폭발 콜백에서 직접 치우지 않는다</b> — OnExploded 발행 중이라 뒤 구독자가 사라진 폭탄을 본다.
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
