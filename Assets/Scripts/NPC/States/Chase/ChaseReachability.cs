/// <summary>
/// 표적에 갈 길이 있는가 — 부분 경로가 <b>얼마나 이어졌는지</b>만 센다. (#568)
///
/// <b>Unity를 모른다.</b> 시각을 인자로 받으므로 단위 테스트로 못박을 수 있다 —
/// "몇 초 이어져야 도달 불가로 볼 것인가"는 화면에서 검증하기 가장 어려운 값이다.
/// 짧게 잡으면 모퉁이를 도는 순간의 한두 프레임짜리 부분 경로에도 표적을 놓아 추격이 툭툭 끊기고,
/// 길게 잡으면 문 뒤의 표적에 벽을 비비며 매달린다.
///
/// 경로가 부분인지 <b>판정하지 않는다</b> — 그건 NavMeshAgent를 아는 쪽(<see cref="NpcChaseState"/>)의
/// 몫이고, 여기는 그 결과를 시간으로 누적할 뿐이다.
/// </summary>
public class ChaseReachability
{
    // 스냅해도 부분 경로가 이만큼(초) 이어지면 도달 불가로 확정한다 (#568)
    private const float k_unreachableSeconds = 1.8f;

    private readonly float m_unreachableSeconds;

    // 부분 경로가 시작된 시각 — 0 이하면 정상 경로
    private float m_partialSince;

    public ChaseReachability(float unreachableSeconds = k_unreachableSeconds)
    {
        m_unreachableSeconds = unreachableSeconds;
    }

    /// <summary>이번 경로 계산이 부분이었는지 알린다 — 정상이면 누적이 풀린다.</summary>
    public void Report(bool partial, float now)
    {
        if (!partial)
        {
            m_partialSince = 0f;
            return;
        }

        if (m_partialSince <= 0f)
            m_partialSince = now;
    }

    /// <summary>부분 경로가 확정 시간만큼 이어졌는가 — 표적이 있는 곳으로 갈 길 자체가 없다.</summary>
    public bool IsUnreachable(float now) =>
        m_partialSince > 0f && now - m_partialSince >= m_unreachableSeconds;

    /// <summary>누적을 버린다 — 표적이 바뀌거나 상태에 새로 진입할 때.</summary>
    public void Clear() => m_partialSince = 0f;
}
