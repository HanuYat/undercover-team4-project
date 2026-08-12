using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 누구를 쫓을 것인가 — 후보 탐색·자격 판정·재추격 쿨다운. (#568)
///
/// <b>이동을 모른다.</b> 에이전트를 건드리지 않고 "이 사람을 쫓아도 되는가"만 답한다.
///
/// 자격(<see cref="IsHeld"/>)과 거리(<see cref="IsChaseable"/>)를 갈라 둔 이유는
/// <b>"왜 놓았는가"가 로그 없이도 읽히게</b> 하기 위해서다 — 무력화돼서인지, 격퇴 쿨다운인지,
/// 그냥 멀어져서인지가 호출부에서 구분된다.
/// </summary>
public class ChaseTargeting
{

    private readonly NpcChaseConfig m_config;
    private readonly NpcRepathScheduler m_repath;
    private readonly List<Transform> m_candidateBuffer = new List<Transform>();

    // 격퇴당한 플레이어별 재추격 금지 종료 시각. 부품 인스턴스가 NPC마다 1개라 NPC별 기록이 된다.
    private readonly Dictionary<Transform, float> m_cooldowns = new Dictionary<Transform, float>();

    public ChaseTargeting(NpcChaseConfig config, NpcRepathScheduler repath)
    {
        m_config = config;
        m_repath = repath;
    }

    /// <summary>스캔 주기와 쿨다운 장부를 비운다 — 상태 진입 시.
    /// 이력을 다음 추격까지 끌고 가면 이유 없이 특정 플레이어가 후보에서 빠지고,
    /// 접속을 끊은 플레이어의 키도 계속 쌓인다. (#568)</summary>
    public void Reset()
    {
        m_cooldowns.Clear();
    }

    /// <summary>이 플레이어를 당분간 노리지 않는다 — 격퇴, 그리고 도달 불가로 놓은 경우.</summary>
    public void PutOnCooldown(Transform target, float now)
    {
        if (target != null)
            m_cooldowns[target] = now + m_config.RetargetCooldown;
    }

    /// <summary>거리를 빼고 <b>붙들 자격만</b> 본다 — 존재·행동 가능·격퇴 쿨다운 아님.</summary>
    public bool IsHeld(Transform target, float now)
    {
        if (target == null)
            return false;
        if (IsOnCooldown(target, now))
            return false;

        PlayerHealth health = target.GetComponent<PlayerHealth>();
        return health != null && health.IsTargetable;
    }

    /// <summary>계속 쫓아도 되는가 — 자격에 <b>포기 거리</b>를 얹는다.
    /// 포기 거리는 후보를 고르는 범위(<see cref="NpcChaseConfig.Range"/>)보다 넓어야 한다 —
    /// 좁으면 방금 고른 표적이 즉시 자격을 잃어 물었다 놨다를 반복한다.</summary>
    public bool IsChaseable(Vector3 from, Transform target, float now) =>
        IsHeld(target, now)
        && ChaseMath.FlatDistance(from, target.position) <= m_config.ReleaseDistance;

    /// <summary>
    /// 지금 표적보다 <see cref="NpcChaseConfig.SwitchAdvantage"/>만큼 더 가까운 후보 — 없으면 null.
    ///
    /// 절대 거리로 놓았다 다시 고르는 방식과 달리 <b>왕복이 생기지 않는다</b>:
    /// 바꾼 직후에는 새 표적이 더 가까우므로 되돌아갈 조건이 성립하지 않는다.
    /// </summary>
    public Transform FindCloser(Vector3 from, Transform current, float currentDistance, float now)
    {
        Transform nearest = PickNearest(from, now);
        if (nearest == null || nearest == current)
            return null;

        float distance = ChaseMath.FlatDistance(from, nearest.position);
        return currentDistance - distance >= m_config.SwitchAdvantage ? nearest : null;
    }

    /// <summary>
    /// 범위 안의 행동 가능한 플레이어 중 <b>가장 가까운</b> 사람 — 쿨다운 대상 제외. 없으면 null.
    /// 스캔 주기로 스로틀하므로 주기 전에 부르면 후보가 있어도 null이 나온다.
    ///
    /// 무작위가 아니라 최근접인 이유 (#568): 눈앞의 사람을 두고 멀리 있는 사람에게 뛰어가는
    /// 그림이 나왔다. 갈아타기는 새로 추첨하는 것이 아니라 "놓쳤으니 가까운 쪽으로"가 자연스럽다.
    /// </summary>
    public Transform PickNearest(Vector3 from, float now)
    {
        if (!m_repath.Due(NpcRepathChannel.TargetScan))
            return null;

        SuddenEventUtil.CollectFieldPlayers(from, m_config.Range, m_candidateBuffer);
        for (int i = m_candidateBuffer.Count - 1; i >= 0; i--)
        {
            if (IsOnCooldown(m_candidateBuffer[i], now))
                m_candidateBuffer.RemoveAt(i);
        }

        Transform nearest = null;
        float nearestDistance = float.MaxValue;
        for (int i = 0; i < m_candidateBuffer.Count; i++)
        {
            Transform candidate = m_candidateBuffer[i];
            float distance = ChaseMath.FlatDistance(from, candidate.position);
            if (distance >= nearestDistance)
                continue;

            nearestDistance = distance;
            nearest = candidate;
        }

        return nearest;
    }

    private bool IsOnCooldown(Transform target, float now) =>
        m_cooldowns.TryGetValue(target, out float until) && now < until;
}
