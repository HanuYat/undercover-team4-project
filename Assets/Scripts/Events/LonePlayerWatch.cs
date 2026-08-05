using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// <b>혼자 다니는 현장 플레이어</b>를 지켜보는 감시기 — 반경 안에 동료가 없는 상태가 일정 시간 이어진
/// 플레이어를 표적 후보로 내놓는다. (#371)
///
/// 납치 이벤트(<see cref="AbductionEvent"/>)에서 뽑아냈다. 뽑은 이유는 <b>재는 주기가 이벤트 수명과 다르기
/// 때문</b>이다 — 스케줄러는 <see cref="ISuddenEvent.CanTrigger"/>를 수십 초 간격으로 드물게 부르므로 그 안에서
/// "20초 동안 혼자였는가"를 잴 수 없다. 그래서 이벤트가 활성이 아닐 때도 <see cref="Tick"/>이 굴러 플레이어별
/// '혼자가 된 시각'을 쌓아 두고, CanTrigger는 그 타이머만 읽는다.
///
/// MonoBehaviour가 아니라 <b>직렬화 가능한 값 묶음</b>이다(<see cref="SpawnedNpcEvent"/>와 같은 관례) — 쓰는 쪽이
/// 자기 Update에서 Tick을 굴리므로 씬에 컴포넌트를 하나 더 배선하지 않아도 되고, 서버 권한 게이트도 쓰는 쪽 것을 탄다.
///
/// <b>표적을 어떻게 쓸지는 정하지 않는다</b> — "누가 가장 오래 혼자인가"까지만 답한다.
/// </summary>
[System.Serializable]
public class LonePlayerWatch
{
    [Tooltip("이 반경(m) 안에 다른 현장 플레이어가 없으면 혼자로 본다")]
    [Min(1f)]
    [SerializeField] private float m_loneRadius = 15f;

    [Tooltip("혼자인 상태가 이 시간(초) 이상 이어져야 표적이 된다 — 잠깐 갈라진 순간에 걸리지 않게")]
    [Min(0f)]
    [SerializeField] private float m_loneSeconds = 20f;

    [Tooltip("본부 구역 (비우면 씬에서 자동 탐색) — 본부 안의 플레이어는 표적에서 제외한다")]
    [SerializeField] private HqOccupancyZone m_hqZone;

    [Tooltip("혼자 판정 갱신 주기(초) — 매 프레임 돌 필요가 없다")]
    [SerializeField] private float m_scanInterval = 0.25f;

    // 플레이어별 '혼자가 된 시각'(Time.time) — 값이 없으면 지금 혼자가 아니다.
    private readonly Dictionary<PlayerHealth, float> m_aloneSince = new Dictionary<PlayerHealth, float>();

    // 사라진 플레이어 정리용 임시 버퍼.
    private readonly List<PlayerHealth> m_staleBuffer = new List<PlayerHealth>();

    private float m_scanCooldown;

    /// <summary>비어 있는 씬 참조를 채운다 — 쓰는 쪽 Awake에서 한 번 부른다.</summary>
    public void ResolveSceneRefs()
    {
        // 본부 구역은 씬 설치물이라 부모 탐색으로 닿지 않는다 — 장소 오브젝트 관례대로 씬 탐색으로 폴백한다
        if (m_hqZone == null)
            m_hqZone = UnityEngine.Object.FindFirstObjectByType<HqOccupancyZone>();
    }

    /// <summary>타이머를 굴린다 — 쓰는 쪽 Update에서 매 프레임 부른다(갱신 주기는 안에서 지킨다).</summary>
    public void Tick(float deltaTime)
    {
        m_scanCooldown -= deltaTime;
        if (m_scanCooldown > 0f)
            return;
        m_scanCooldown = m_scanInterval;

        TickLoneTimers();
    }

    /// <summary>지속 조건까지 채운 후보 중 <b>가장 오래 혼자인</b> 플레이어 — 없으면 null.</summary>
    public Transform FindTarget()
    {
        Transform best = null;
        float oldest = float.MaxValue;

        foreach (KeyValuePair<PlayerHealth, float> entry in m_aloneSince)
        {
            if (entry.Key == null)
                continue;
            if (Time.time - entry.Value < m_loneSeconds)
                continue;

            if (entry.Value < oldest)
            {
                oldest = entry.Value;
                best = entry.Key.transform;
            }
        }

        return best;
    }

    /// <summary>쌓인 타이머를 모두 버린다 — 표적을 소비했거나 강제 정리할 때. 다음 Tick부터 처음부터 센다.</summary>
    public void Reset()
    {
        m_aloneSince.Clear();
        m_scanCooldown = 0f;
    }

    // 현장 플레이어 각자가 '혼자'인지 갱신한다. 혼자가 아니게 되면 타이머를 버린다(다시 혼자가 되면 처음부터).
    //
    // 씬 스캔은 <b>주기당 1회</b>다. 모은 배열을 반경 판정에 그대로 넘긴다 — 예전에는 여기서 한 번,
    // 반경 판정이 플레이어마다 SuddenEventUtil.CollectFieldPlayers로 또 한 번 훑어 N명이면 주기당
    // N+1회였다(6인이면 초당 28회 + 매번 배열 할당).
    private void TickLoneTimers()
    {
        PlayerHealth[] players = UnityEngine.Object.FindObjectsByType<PlayerHealth>(FindObjectsSortMode.None);

        for (int i = 0; i < players.Length; i++)
        {
            PlayerHealth player = players[i];

            if (IsLoneCandidate(player, players))
            {
                if (!m_aloneSince.ContainsKey(player))
                    m_aloneSince[player] = Time.time;
            }
            else
            {
                m_aloneSince.Remove(player);
            }
        }

        // 접속을 끊거나 파괴된 플레이어의 항목을 걷어낸다 — 남겨두면 목록이 자라고 죽은 키를 표적으로 고른다
        m_staleBuffer.Clear();
        foreach (PlayerHealth tracked in m_aloneSince.Keys)
            if (tracked == null)
                m_staleBuffer.Add(tracked);

        for (int i = 0; i < m_staleBuffer.Count; i++)
            m_aloneSince.Remove(m_staleBuffer[i]);
    }

    // 지금 이 순간 혼자인가 — 후보 자격의 순간 조건. 지속 시간은 타이머가 본다.
    // scanned는 이번 주기에 이미 모아 둔 전체 플레이어 목록이다(위 TickLoneTimers 주석 참고).
    private bool IsLoneCandidate(PlayerHealth player, PlayerHealth[] scanned)
    {
        // 다운·기능 정지된 플레이어는 제외한다 — 이미 무력한 대상은 쓰는 쪽에서도 다룰 것이 없다
        // (SuddenEventUtil의 현장 플레이어 판정과 같은 기준)
        if (player == null || !player.IsTargetable)
            return false;

        // 본부 안은 세지 않는다 — 관제가 혼자 남는 것은 정상 상황이고, 그걸 표적으로 삼으면 역할 분담이 깨진다 (팀 확정)
        if (m_hqZone != null && m_hqZone.Contains(player))
            return false;

        // 반경 안에 다른 현장 플레이어가 하나라도 있으면 혼자가 아니다.
        // 판정 기준(IsTargetable + 반경)은 SuddenEventUtil.CollectFieldPlayers와 같지만 스캔을 다시 돌지 않는다.
        Vector3 origin = player.transform.position;
        float radiusSqr = m_loneRadius * m_loneRadius;

        for (int i = 0; i < scanned.Length; i++)
        {
            PlayerHealth other = scanned[i];
            if (other == null || other == player || !other.IsTargetable)
                continue;

            if ((other.transform.position - origin).sqrMagnitude <= radiusSqr)
                return false;
        }

        return true;
    }
}
