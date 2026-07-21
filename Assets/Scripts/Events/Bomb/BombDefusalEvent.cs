using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 시한폭탄 (돌발 이벤트 · 현장) — 필드 임의 지점에 폭탄이 설치되고, 본부 매뉴얼(<see cref="BombManual"/>)의
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

    [Header("설치 위치 (현장 플레이어 주변 링)")]
    [Tooltip("기준 플레이어에게서 이 거리(m) 이상 떨어뜨려 설치한다 — 발밑에 바로 뜨지 않게")]
    [SerializeField]
    private float m_placeDistanceMin = 8f;

    [Tooltip("기준 플레이어에게서 이 거리(m) 이내에 설치한다 — 달려갈 수 있는 범위")]
    [SerializeField]
    private float m_placeDistanceMax = 20f;

    [Tooltip("설치 후보 지점에서 이 거리(m) 안에 NavMesh가 없으면 그 지점은 버린다")]
    [SerializeField]
    private float m_navSampleMaxDistance = 4f;

    [Tooltip("유효한 설치 지점을 찾는 최대 시도 횟수")]
    [SerializeField]
    private int m_maxSpawnAttempts = 12;

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
        // 설치 기준점이 될 행동 가능한 현장 플레이어가 있어야 한다 — 전원 다운이면 걸지 않는다
        return SuddenEventUtil.FindRandomFieldPlayer() != null;
    }

    public void ServerBegin()
    {
        if (m_bombPrefab == null)
            return;

        Transform anchor = SuddenEventUtil.FindRandomFieldPlayer();
        if (anchor == null)
            return;

        if (!SuddenEventUtil.TryFindSpawnPositionNear(
                anchor.position,
                m_placeDistanceMin,
                m_placeDistanceMax,
                m_navSampleMaxDistance,
                m_maxSpawnAttempts,
                out Vector3 position))
        {
            Debug.LogWarning("BombDefusalEvent: NavMesh 위 설치 지점을 찾지 못해 발동 취소", this);
            return;
        }

        m_bomb = Instantiate(m_bombPrefab, position, Quaternion.identity);
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
}
