using UnityEngine;

/// <summary>
/// 차가 나오는 자리 하나 — 씬에 놓는 마커다 (#634). <b>놓인 자리가 출발점이고 놓인 방향(파랑 축)이
/// 진행 방향</b>이라는 규칙은 예전(#304)과 같고, 그 규칙을 차 대신 이 마커로 옮겼을 뿐이라
/// 맵 제작 방식은 그대로다.
///
/// <b>회수 조건이 마커 하나로 판정된다.</b> 출발점 + 방향 + <see cref="RunDistance"/>가 곧 주행
/// 구간이고, 그 끝이 곧 맵 밖이다 — 도로 직선 구간보다 길게 잡으면 차가 건물을 통과하므로
/// 기즈모(초록 선)가 실제로 도로 위에 얹혀 있는지 눈으로 확인하고 잡을 것.
///
/// <b>속도는 차가 아니라 레인이 쥔다.</b> 한 레인의 차가 모두 같은 속도여야 시간 간격이 곧 거리
/// 간격이 되고, 그래야 아래 하한이 "앞차와 붙지 않는다"를 실제로 보장한다.
/// </summary>
public class TrafficLane : MonoBehaviour
{
    [Header("주행")]
    [Tooltip("이 레인의 차가 달리는 거리(m) — 출발점에서 전방으로 이만큼 간 뒤 회수된다. 도로 직선 구간을 넘기지 말 것")]
    [Min(5f)]
    [SerializeField] private float m_runDistance = 120f;

    [Tooltip("이 레인의 주행 속도(m/s) — 플레이어 전력질주(8)보다 충분히 빨라야 '피한다'가 성립한다. 레인 안에서는 모든 차가 같은 속도다")]
    [Min(1f)]
    [SerializeField] private float m_speed = 22f;

    [Header("배출 간격")]
    [Tooltip(
        "이 레인이 가로지르는 도로 폭(m) — 사람이 건너는 데 걸리는 시간의 근거다. "
            + "배출 간격의 하한이 여기서 나온다: 폭 ÷ 이동 속도 + 여유"
    )]
    [Min(1f)]
    [SerializeField] private float m_crossWidth = 8f;

    [Tooltip("하한 <b>위에</b> 얹는 랜덤 폭(초) — 0이면 정확히 하한 간격으로 규칙적으로 나온다. 랜덤은 하한 아래로는 절대 내려가지 않는다")]
    [Min(0f)]
    [SerializeField] private float m_randomExtraSeconds = 4f;

    [Header("차종")]
    [Tooltip("TrafficManager의 차량 프리팹 목록에서 쓸 인덱스 — -1이면 매번 랜덤으로 고른다")]
    [SerializeField] private int m_vehicleIndex = -1;

    /// <summary>출발점 — 놓인 자리 그대로다.</summary>
    public Vector3 StartPoint => transform.position;

    /// <summary>진행 방향 — 놓인 방향을 수평으로 눕힌 것. 마커가 위를 보고 있으면 <see cref="Vector3.zero"/>.</summary>
    public Vector3 Direction
    {
        get
        {
            Vector3 forward = transform.forward;
            forward.y = 0f;
            return forward.sqrMagnitude < 0.001f ? Vector3.zero : forward.normalized;
        }
    }

    /// <summary>주행 거리(m).</summary>
    public float RunDistance => m_runDistance;

    /// <summary>주행 속도(m/s).</summary>
    public float Speed => m_speed;

    /// <summary>쓸 차량 프리팹 인덱스 — 음수면 랜덤.</summary>
    public int VehicleIndex => m_vehicleIndex;

    /// <summary>
    /// 배출 간격의 <b>하한</b>(초) — 이 값 아래로는 어떤 랜덤도 내려가지 않는다.
    ///
    /// 즉사를 유지한 대가로 공정성을 지는 두 축 중 하나다(다른 하나는 헤드라이트·엔진음).
    /// 앞차가 지나간 뒤 <b>도로를 건널 창이 반드시 한 번은 열린다</b>는 것이 이 하한의 약속이고,
    /// 그래서 값이 "건너는 데 걸리는 시간 + 여유"다. 레인 안의 차가 모두 같은 속도라
    /// (<see cref="Speed"/>) 시간 간격은 그대로 거리 간격이 된다 — 붙어 오는 두 대가 생기지 않는다.
    /// </summary>
    /// <param name="crossSpeed">건너는 사람의 이동 속도(m/s) — 걷기 기준으로 잡아야 걷는 사람도 산다.</param>
    /// <param name="marginSeconds">그 위에 더하는 여유(초) — 차를 보고 판단하는 시간이다.</param>
    public float MinGapSeconds(float crossSpeed, float marginSeconds) =>
        m_crossWidth / Mathf.Max(0.1f, crossSpeed) + Mathf.Max(0f, marginSeconds);

    /// <summary>다음 배출까지의 간격(초) — 하한 위에서만 랜덤을 굴린다.</summary>
    public float NextIntervalSeconds(float crossSpeed, float marginSeconds) =>
        MinGapSeconds(crossSpeed, marginSeconds) + Random.Range(0f, m_randomExtraSeconds);

    // 씬 뷰에서 주행 구간과 진행 방향을 눈으로 확인할 수 있게 그린다 —
    // 이 초록 선이 도로 위에 얹혀 있지 않으면 차가 건물을 통과한다.
    private void OnDrawGizmos()
    {
        Vector3 direction = Direction;
        if (direction == Vector3.zero)
        {
            Gizmos.color = Color.red; // 마커가 위/아래를 보고 있다 — 차가 나오지 않는다
            Gizmos.DrawWireSphere(StartPoint, 2f);
            return;
        }

        Vector3 start = StartPoint;
        Vector3 end = start + direction * m_runDistance;

        Gizmos.color = Color.green;
        Gizmos.DrawLine(start, end);
        Gizmos.DrawWireSphere(start, 1f);

        // 진행 방향 화살촉 — 어느 쪽으로 달리는지가 배치의 전부다
        Vector3 right = Vector3.Cross(Vector3.up, direction);
        Gizmos.DrawLine(end, end - direction * 3f + right * 1.5f);
        Gizmos.DrawLine(end, end - direction * 3f - right * 1.5f);

        // 건너는 폭 — 하한 계산의 근거다
        Gizmos.color = new Color(0f, 1f, 0f, 0.35f);
        Gizmos.DrawLine(start - right * (m_crossWidth * 0.5f), start + right * (m_crossWidth * 0.5f));
    }
}
