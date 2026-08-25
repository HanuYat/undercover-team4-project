using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 추격 중 에이전트를 어떻게 몰 것인가 — 조향 덮어쓰기와 리드 조준. (#568)
///
/// <b>FSM을 모른다.</b> 받는 것은 NavMeshAgent와 설정뿐이라, 어느 상태가 쓰든 상관없다.
///
/// 조향을 덮어쓰는 이유: 시민 기본값(선회 240도/초)으로는 선회 반경이 포획 거리보다 커서
/// 플레이어가 옆으로 스텝만 밟아도 안쪽으로 못 꺾고 궤도를 돈다. 진입 시점의 <b>실제 값</b>을
/// 기억해 두고 되돌리므로 개체차(시민마다 다른 속도·회피 우선순위)를 덮어쓰지 않는다.
/// </summary>
public class ChaseSteering
{
    private readonly NpcChaseConfig m_config;

    // 진입 전 조향 값 — Restore가 그대로 되돌린다
    private float m_baseTurnSpeed;
    private float m_baseAcceleration;
    private bool m_baseAutoBraking;

    // 리드 조준용 — 직전 표본의 표적 위치와 그 시각. 표적이 바뀌면 리셋한다(엉뚱한 속도가 나온다).
    private Transform m_leadTarget;
    private Vector3 m_lastTargetPosition;
    private float m_lastTargetSampleTime;

    public ChaseSteering(NpcChaseConfig config)
    {
        m_config = config;
    }

    /// <summary>진입 시점의 조향 값을 기억한다 — <see cref="Apply"/>로 되돌릴 기준. 상태의 Enter에서 한 번.</summary>
    public void CaptureBaseline(NavMeshAgent agent)
    {
        m_baseTurnSpeed = agent.angularSpeed;
        m_baseAcceleration = agent.acceleration;
        m_baseAutoBraking = agent.autoBraking;
    }

    /// <summary>
    /// 조향을 추격용/평상시로 오간다.
    ///
    /// 오토브레이킹은 추격 중에만 끈다 — 목적지가 표적 발밑이라 켜져 있으면
    /// 제동거리(약 3m)부터 감속하다 <b>목적지를 지나쳐 돌아 나온다</b>. 반대로 수렴처럼
    /// 정해진 자리에 서는 이동에서는 감속이 있어야 하므로 그쪽은 평상시 값을 쓴다.
    /// </summary>
    public void Apply(NavMeshAgent agent, bool chasing)
    {
        agent.angularSpeed = chasing ? m_config.TurnSpeed : m_baseTurnSpeed;
        agent.acceleration = chasing ? m_config.Acceleration : m_baseAcceleration;
        agent.autoBraking = !chasing && m_baseAutoBraking;
    }

    /// <summary>
    /// 표적이 조금 뒤에 있을 자리를 돌려준다. (#568)
    ///
    /// 현재 위치를 그대로 조준하면 최대 한 주기(0.15~0.4초) 뒤처진 지점을 쫓아 <b>꼬리만 문다</b>.
    /// 앞을 보는 시간에 상한(<see cref="NpcChaseConfig.MaxLeadSeconds"/>)을 두는 이유는,
    /// 없으면 표적이 급반전할 때 <b>지나간 방향으로 크게 헛돌기</b> 때문이다 — 속도 표본이
    /// 직전 주기의 것이라 반전을 모른다.
    /// </summary>
    public Vector3 PredictAimPoint(Transform target, float distance, float agentSpeed, float now)
    {
        Vector3 current = target.position;
        float span = now - m_lastTargetSampleTime;

        // 표적이 바뀌었거나 첫 표본 — 속도를 알 수 없다
        if (m_leadTarget != target || span <= 0f)
        {
            RememberSample(target, current, now);
            return current;
        }

        Vector3 velocity = (current - m_lastTargetPosition) / span;
        velocity.y = 0f; // 계단·경사에서 위를 조준하지 않게
        RememberSample(target, current, now);

        float speed = Mathf.Max(agentSpeed, 0.1f);
        float lead = Mathf.Min(distance / speed, m_config.MaxLeadSeconds);
        return current + velocity * lead;
    }

    /// <summary>
    /// 몸 방향을 <b>실제 이동 속도</b> 쪽으로 돌린다 — NavMeshAgent 기본 회전(updateRotation) 대신
    /// 이걸 쓰는 이유는 경로 재탐색으로 우회할 때 기본 회전이 목적지(표적) 쪽을 향한 채로 남는
    /// 순간이 있어서다(pathPending 구간의 desiredVelocity는 새 경로의 코너가 아니라 새로 지정한
    /// 목적지 방향을 그대로 가리킨다). 실제로 이동한 벡터를 쓰면 우회 구간에서도 발이 향하는
    /// 쪽을 몸이 그대로 따라간다. (#829)
    ///
    /// <see cref="NpcChaseState"/>가 진입 시 <c>Agent.updateRotation</c>을 꺼 두고 이걸 대신 매 틱 부른다.
    /// </summary>
    public void TickFacing(NavMeshAgent agent, Transform transform)
    {
        Vector3 velocity = agent.velocity;
        velocity.y = 0f;

        // 멈춰 서 있으면(수렴 도착·목적지 코앞) 방향을 바꾸지 않는다 — 마지막으로 보던 쪽을 유지한다.
        if (velocity.sqrMagnitude < 0.01f)
            return;

        Quaternion target = Quaternion.LookRotation(velocity);
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation, target, agent.angularSpeed * Time.deltaTime);
    }

    /// <summary>리드 표본을 버린다 — 표적이 바뀌거나 상태에 새로 진입할 때.</summary>
    public void ClearLeadSample()
    {
        m_leadTarget = null;
        m_lastTargetSampleTime = 0f;
    }

    private void RememberSample(Transform target, Vector3 position, float now)
    {
        m_leadTarget = target;
        m_lastTargetPosition = position;
        m_lastTargetSampleTime = now;
    }
}
