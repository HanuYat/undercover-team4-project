using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 추격 폭탄의 이동 — NavMesh 위에서 가장 가까운 현장 플레이어를 쫓는다. (#768 분할)
///
/// <b>동기화 상태가 없다.</b> 이동 결과는 <c>NetworkTransform</c>이 실어 보내고 원격 피어는
/// 에이전트를 끄므로(<see cref="DisableAgent"/>), 이 컴포넌트는 순수 <see cref="MonoBehaviour"/>다.
/// 언제 쫓고 언제 멈추는지는 상태 주인인 <see cref="BombDevice"/>가 정하고, 여기는 "쫓으라면
/// 쫓는" 일만 한다.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class BombChaseDriver : MonoBehaviour
{
    [Tooltip("추격 속도(m/s) — 플레이어 걷기 5·달리기 8 기준. 걷기보다 빠르게 둘 것: 걸어서 벌 수 있으면 " +
             "달아나는 데 아무 대가가 없어 추격이 성립하지 않는다. 달리기보다는 확실히 느려야 한다")]
    [SerializeField]
    private float m_chaseSpeed = 5.5f;

    [Tooltip("이 반경(m) 안에 현장 인원이 들어오면 잠에서 깨어 추격과 카운트다운을 함께 시작한다")]
    [SerializeField]
    private float m_wakeRadius = 14f;

    [Tooltip("표적을 다시 고르고 목적지를 갱신하는 주기(초)")]
    [SerializeField]
    private float m_retargetInterval = 0.5f;

    [Tooltip("현재 표적보다 이만큼(m) 더 가까워야 표적을 바꾼다 — 두 사람 사이에서 갈팡질팡하지 않게")]
    [SerializeField]
    private float m_retargetHysteresis = 1.5f;

    [Tooltip("이 반경(m) 안의 현장 플레이어만 표적이 된다 — 맵 전역을 덮을 만큼 크게 둘 것")]
    [SerializeField]
    private float m_targetSearchRadius = 300f;

    private NavMeshAgent m_agent;

    private PlayerHealth m_target; // 지금 쫓는 상대 — 히스테리시스로만 바뀐다
    private float m_nextRetargetTime;

    private void Awake()
    {
        m_agent = GetComponent<NavMeshAgent>();
        m_agent.speed = m_chaseSpeed;
    }

    /// <summary>
    /// 원격 피어의 에이전트를 끈다 — 이동 권한은 서버 하나뿐이다. 원격에서 함께 돌면
    /// NetworkTransform이 보내오는 위치와 싸워 폭탄이 덜덜 떤다.
    /// </summary>
    public void DisableAgent()
    {
        m_agent.enabled = false;
    }

    /// <summary>
    /// 대기 중 깨우기 판정 — <see cref="m_wakeRadius"/> 안에 현장 인원이 있으면 <c>true</c>.
    /// 재타겟 주기로만 실제 검색한다(매 프레임 전수 검색 회피) — 추격과 같은 시계를 쓴다.
    /// </summary>
    public bool PollWakeTrigger()
    {
        if (Time.time < m_nextRetargetTime)
            return false;

        m_nextRetargetTime = Time.time + m_retargetInterval;
        return SuddenEventUtil.FindNearestFieldPlayer(transform.position, m_wakeRadius) != null;
    }

    /// <summary>무장 시점에 시계를 비운다 — 다음 <see cref="Tick"/>에서 곧바로 표적을 고른다.</summary>
    public void ResetRetargetClock()
    {
        m_nextRetargetTime = 0f;
    }

    /// <summary>표적 재선정 + 목적지 갱신. 표적도 움직이므로 같은 주기로 목적지를 다시 찍는다.</summary>
    public void Tick()
    {
        if (Time.time < m_nextRetargetTime)
            return;

        m_nextRetargetTime = Time.time + m_retargetInterval;

        PlayerHealth nearest = SuddenEventUtil.FindNearestFieldPlayer(transform.position, m_targetSearchRadius);
        if (nearest == null)
        {
            // 쫓을 사람이 없다(전원 다운) — 그 자리에서 남은 시간을 마저 센다. 이벤트는 폭발로 끝난다.
            m_target = null;
            Stop();
            return;
        }

        if (m_target == null || !m_target.IsTargetable)
        {
            m_target = nearest;
        }
        else if (nearest != m_target)
        {
            // 히스테리시스 — 근소한 차이로 표적이 바뀌면 두 사람 사이에서 방향만 바꾸다 제자리에 선다
            float current = Vector3.Distance(transform.position, m_target.transform.position);
            float candidate = Vector3.Distance(transform.position, nearest.transform.position);
            if (candidate + m_retargetHysteresis < current)
                m_target = nearest;
        }

        if (!m_agent.enabled || !m_agent.isOnNavMesh)
            return;

        m_agent.isStopped = false;
        m_agent.SetDestination(m_target.transform.position);
    }

    /// <summary>그 자리에 멈춘다 — 폭심 확정(Locked)과 폭발 시점에 불린다.</summary>
    public void Stop()
    {
        if (!m_agent.enabled || !m_agent.isOnNavMesh)
            return;

        m_agent.ResetPath();
        m_agent.isStopped = true;
    }
}
