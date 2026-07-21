using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 시한폭탄 (돌발 이벤트 · 현장) — 지정된 스폰 포인트 중 한 곳에 폭탄이 설치되고, 본부 매뉴얼(<see cref="BombManual"/>)의
/// 규칙을 무전으로 받아 현장이 선을 잘라 해체한다. 제한시간 내 미해체 시 폭발한다. (GDD 6-4, #232)
///
/// 스폰형 이벤트 — 폭탄 프리팹(<see cref="BombDevice"/>)을 스폰하고 수명·정리만 맡는다. 카운트다운·해체
/// 판정·폭발은 스폰물이 스스로 서버 권위로 처리하고 자기 NetworkObject로 전파한다 (ISuddenEvent 규약,
/// JailbreakEvent ↔ NpcController와 동일 관계).
///
/// 라운드당 폭탄 1개 — <see cref="IsActive"/>가 폭탄이 살아 있는 동안 true라, 프레임워크가 겹쳐 발생시키지 않는다.
/// </summary>
[RequireComponent(typeof(SuddenEventManager))]
public class BombDefusalEvent : MonoBehaviour, ISuddenEvent
{
    [Header("폭탄 프리팹 (BombDevice)")]
    [SerializeField]
    private BombDevice m_bombPrefab;

    [Header("설치 위치 (스폰 포인트)")]
    [Tooltip("폭탄이 설치될 후보 지점들 — 발동 때마다 이 중 하나를 무작위로 골라 그 자리에 설치한다")]
    [SerializeField]
    private Transform[] m_spawnPoints;

    [Header("정리")]
    [Tooltip("해체·폭발 후 잔류 연출(넉백·VFX)을 보여줄 시간(초) — 지나면 폭탄을 치운다")]
    [SerializeField]
    private float m_resolvedLingerSeconds = 3f;

    private BombDevice m_bomb;
    private bool m_resolved;
    private float m_despawnAt;

    public string DisplayName => "시한폭탄";

    public bool IsActive => m_bomb != null;

    public bool CanTrigger()
    {
        if (m_bombPrefab == null)
            return false;
        if (!HasUsableSpawnPoint())
            return false;
        // 폭탄을 해체하러 갈 수 있는 현장 플레이어가 있어야 한다 — 전원 다운이면 걸지 않는다
        return SuddenEventUtil.FindRandomFieldPlayer() != null;
    }

    public void ServerBegin()
    {
        if (m_bombPrefab == null)
            return;

        Transform spawnPoint = PickRandomSpawnPoint();
        if (spawnPoint == null)
        {
            Debug.LogWarning("BombDefusalEvent: 사용 가능한 스폰 포인트가 없어 발동 취소", this);
            return;
        }

        m_bomb = Instantiate(m_bombPrefab, spawnPoint.position, spawnPoint.rotation);
        if (SuddenEventUtil.IsNetworkSessionActive)
            m_bomb.GetComponent<NetworkObject>().Spawn();

        m_bomb.OnResolved += HandleResolved;
        m_resolved = false;

        // 시드 하나가 퍼즐 전체(선 색·일련번호·규칙표·정답)를 정한다 — 전 클라가 이 시드로 동일 퍼즐을 재구성한다.
        int seed = Random.Range(1, int.MaxValue);
        m_bomb.ServerArm(seed);
    }

    public void ServerTick()
    {
        if (m_bomb == null)
            return;

        // 해체·폭발 후 잔류 연출 시간이 지나면 정리한다.
        if (m_resolved && Time.time >= m_despawnAt)
            Despawn();
    }

    public void ServerReset()
    {
        // 라운드 종료 등 — 폭탄을 즉시 치운다.
        Despawn();
    }

    private void HandleResolved(bool defused)
    {
        if (m_resolved)
            return;

        m_resolved = true;
        m_despawnAt = Time.time + m_resolvedLingerSeconds;
        Debug.Log(defused ? "[돌발이벤트] 시한폭탄 — 해체 성공" : "[돌발이벤트] 시한폭탄 — 폭발");
    }

    private void Despawn()
    {
        if (m_bomb == null)
            return;

        m_bomb.OnResolved -= HandleResolved;
        SuddenEventUtil.DespawnOrDestroy(m_bomb.gameObject);
        m_bomb = null;
        m_resolved = false;
    }

    // 지정된 스폰 포인트 중 실제로 쓸 수 있는(null 아닌) 것이 하나라도 있는지 — 미설정 시 발동을 거른다.
    private bool HasUsableSpawnPoint()
    {
        if (m_spawnPoints == null)
            return false;
        for (int i = 0; i < m_spawnPoints.Length; i++)
        {
            if (m_spawnPoints[i] != null)
                return true;
        }
        return false;
    }

    // 스폰 포인트 하나를 무작위로 고른다 — 무작위 시작 지점에서 목록을 한 바퀴 돌아 비어 있지 않은
    // 첫 포인트를 반환한다. 중간에 null 슬롯(인스펙터 미설정)이 있어도 이벤트를 통째로 취소하지 않는다
    // (JailbreakEvent.TryFindSpawnPosition과 같은 방식).
    private Transform PickRandomSpawnPoint()
    {
        if (m_spawnPoints == null || m_spawnPoints.Length == 0)
            return null;

        int start = Random.Range(0, m_spawnPoints.Length);
        for (int i = 0; i < m_spawnPoints.Length; i++)
        {
            Transform point = m_spawnPoints[(start + i) % m_spawnPoints.Length];
            if (point != null)
                return point;
        }
        return null;
    }

    // 씬 뷰에서 폭탄 스폰 포인트 위치를 눈으로 확인할 수 있게 기즈모를 그린다.
    private void OnDrawGizmosSelected()
    {
        if (m_spawnPoints == null)
            return;

        Gizmos.color = Color.red;
        foreach (Transform point in m_spawnPoints)
        {
            if (point != null)
                Gizmos.DrawWireSphere(point.position, 0.5f);
        }
    }
}
