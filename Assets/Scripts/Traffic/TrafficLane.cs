using Unity.AI.Navigation;
using UnityEngine;

/// <summary>
/// 차가 나오는 자리 하나 — 씬에 놓는 마커다 (#634). <b>놓인 방향(파랑 축)이 진행 방향</b>이라는
/// 규칙은 예전(#304)과 같고, 그 규칙을 차 대신 이 마커로 옮겼을 뿐이라 맵 제작 방식은 그대로다.
///
/// <b>마커가 놓인 자리는 이제 출발점이 아니라 도로 구간의 시작점이다</b> (#673). 차는 그보다
/// <see cref="m_approachDistance"/>만큼 뒤 — 맵 밖 — 에서 태어나 달려 들어오고, 도로 구간을 다 쓴 뒤
/// <see cref="m_exitDistance"/>만큼 더 가서 맵 밖에서 회수된다. 마커 자체는 도로 위에 그대로 두면 된다.
///
/// <b>이 앞뒤 여유가 곧 엔진음의 감쇠 구간이다.</b> 예전에는 출발점이 가청 범위 안이라 차가 스폰되는
/// 순간 옆에서 큰 소리가 났다 — 다가오고 멀어지는 느낌이 아예 없었다. 여유를 엔진음 maxDistance보다
/// 크게 잡으면 코드 수정 없이 "멀리서 들리기 시작 → 커짐 → 지나감 → 작아짐"이 성립한다.
///
/// <b>회수 조건이 마커 하나로 판정된다.</b> 출발점 + 방향 + <see cref="RunDistance"/>가 곧 주행
/// 구간이다 — 도로 직선 구간보다 길게 잡으면 차가 건물을 통과하므로 기즈모(초록 선)가 실제로 도로
/// 위에 얹혀 있는지 눈으로 확인하고 잡을 것. 맵 밖 구간(회색 선)은 도로를 벗어나 있는 것이 정상이다.
///
/// <b>속도는 차가 아니라 레인이 쥔다.</b> 한 레인의 차가 모두 같은 속도여야 시간 간격이 곧 거리
/// 간격이 되고, 그래야 아래 하한이 "앞차와 붙지 않는다"를 실제로 보장한다.
/// </summary>
public class TrafficLane : MonoBehaviour
{
    [Header("주행")]
    [Tooltip("이 레인이 덮는 도로 구간의 길이(m) — 마커 자리에서 전방으로 이만큼이다. 도로 직선 구간을 넘기지 말 것.\n실제 주행 거리는 여기에 아래 맵 밖 여유 앞뒤가 더해진다")]
    [Min(5f)]
    [SerializeField] private float m_runDistance = 120f;

    [Header("맵 밖 여유 (#673)")]
    [Tooltip(
        "도로 구간 <b>앞</b>에 붙는 맵 밖 주행 거리(m) — 차는 여기서 태어나 달려 들어온다.\n"
            + "⚠ 엔진음 maxDistance(AudioLibrary의 VehicleEngine, 현재 90)보다 커야 스폰 순간이 안 들린다. "
            + "작으면 예전처럼 차가 갑자기 옆에서 소리를 내며 나타난다"
    )]
    [Min(0f)]
    [SerializeField] private float m_approachDistance = 100f;

    [Tooltip("도로 구간 <b>뒤</b>에 붙는 맵 밖 주행 거리(m) — 차는 여기까지 가서 회수된다. 앞 여유와 같은 이유로 엔진음 maxDistance보다 커야 소리가 끝까지 잦아든다")]
    [Min(0f)]
    [SerializeField] private float m_exitDistance = 100f;

    [Tooltip("이 레인의 주행 속도(m/s) — 플레이어 전력질주(8)보다 충분히 빨라야 '피한다'가 성립한다. 레인 안에서는 모든 차가 같은 속도다")]
    [Min(1f)]
    [SerializeField] private float m_speed = 22f;

    [Header("배출 간격 하한")]
    [Tooltip(
        "이 레인이 가로지르는 도로 폭(m) — 사람이 건너는 데 걸리는 시간의 근거다. "
            + "배출 간격의 하한이 여기서 나온다: 폭 ÷ 이동 속도 + 여유.\n"
            + "⚠ 이 값은 <b>폴백</b>이다 — 평소에는 도로의 NavMesh Road 볼륨에서 실제 폭을 읽어 쓴다 (#673)"
    )]
    [Min(1f)]
    [SerializeField] private float m_crossWidth = 8f;

    [Tooltip("도로 폭을 읽어 올 Road 영역 볼륨 — 비워 두면 마커를 품고 있는 볼륨을 씬에서 찾는다. 자동 탐색이 엉뚱한 것을 잡을 때만 직접 지정한다")]
    [SerializeField] private NavMeshModifierVolume m_roadVolume;

    [Header("차종")]
    [Tooltip("TrafficManager의 차량 프리팹 목록에서 쓸 인덱스 — -1이면 매번 랜덤으로 고른다")]
    [SerializeField] private int m_vehicleIndex = -1;

    /// <summary>도로 구간의 시작점 — 마커가 놓인 자리 그대로다. 차가 태어나는 자리는 <see cref="StartPoint"/>다.</summary>
    public Vector3 RoadPoint => transform.position;

    /// <summary>차가 태어나는 자리 — 도로 구간보다 <see cref="m_approachDistance"/>만큼 뒤(맵 밖)다 (#673).</summary>
    public Vector3 StartPoint => RoadPoint - Direction * m_approachDistance;

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

    /// <summary>주행 거리(m) — 맵 밖 진입 + 도로 구간 + 맵 밖 이탈을 합한 실제 거리다 (#673).</summary>
    public float RunDistance => m_approachDistance + m_runDistance + m_exitDistance;

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
        CrossWidth / Mathf.Max(0.1f, crossSpeed) + Mathf.Max(0f, marginSeconds);

    /// <summary>
    /// 이 레인이 가로지르는 도로 폭(m).
    ///
    /// <b>손으로 넣지 않고 도로에서 읽는다</b> (#673). 이 프로젝트에서 "도로"를 정하는 것은 NavMesh의
    /// Road 영역 볼륨이고(시민을 도로에서 빼는 <see cref="NpcNavAreas.ExcludeRoad"/>가 보는 것도 같은
    /// 영역이다), 건너는 시간의 근거도 결국 그 폭이다. 레인마다 숫자를 베껴 넣으면 도로를 넓힌 날
    /// 조용히 어긋나므로 근거 쪽을 직접 읽는다.
    ///
    /// 볼륨을 못 찾으면(맵에 Road 영역이 안 칠해진 구성) 인스펙터의 <see cref="m_crossWidth"/>로
    /// 폴백한다 — <see cref="NpcNavAreas.ExcludeRoad"/>가 같은 상황에서 원래 마스크로 물러나는 것과 같다.
    /// </summary>
    public float CrossWidth => m_resolvedCrossWidth > 0f ? m_resolvedCrossWidth : m_crossWidth;

    private float m_resolvedCrossWidth; // 0 = 아직 못 읽었다 (폴백을 쓴다)

    /// <summary>
    /// Road 볼륨에서 이 레인의 도로 폭을 읽어 둔다 — <see cref="TrafficManager"/>가 Awake에서 한 번 부른다.
    /// 볼륨 목록을 매니저가 모아 넘기는 이유는 레인마다 씬을 훑지 않기 위해서다.
    /// </summary>
    public void ResolveCrossWidth(NavMeshModifierVolume[] volumes)
    {
        NavMeshModifierVolume volume = m_roadVolume != null ? m_roadVolume : FindRoadVolume(volumes);
        if (volume == null)
            return; // 폴백 유지 — Road 영역이 없는 맵에서는 인스펙터 값이 유일한 근거다

        float width = LateralExtentOf(volume);
        if (width > 0f)
            m_resolvedCrossWidth = width;
    }

    // 마커를 품고 있는 Road 영역 볼륨 — 도로가 겹치는 교차로에서는 먼저 찾은 것을 쓴다(폭이 같다).
    // <b>높이는 보지 않는다.</b> 마커를 도로면보다 조금 띄워 놓는 것은 흔한 배치라, y까지 따지면
    // 그런 레인이 조용히 폴백으로 떨어진다 — 어차피 도로를 가리는 것은 평면상의 위치다.
    private NavMeshModifierVolume FindRoadVolume(NavMeshModifierVolume[] volumes)
    {
        if (volumes == null || NpcNavAreas.RoadMask == 0)
            return null;

        for (int i = 0; i < volumes.Length; i++)
        {
            NavMeshModifierVolume volume = volumes[i];
            if (volume == null || (1 << volume.area & NpcNavAreas.RoadMask) == 0)
                continue;

            Vector3 local = volume.transform.InverseTransformPoint(RoadPoint) - volume.center;
            Vector3 half = volume.size * 0.5f;
            if (Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.z) <= half.z)
                return volume;
        }

        return null;
    }

    // 진행 방향과 <b>직각</b>인 축으로 잰 볼륨의 폭 — 그것이 건너는 사람이 지나야 하는 거리다.
    // 볼륨이 회전해 있어도 맞도록 상자의 반너비를 그 축에 투영해 더한다(OBB의 지지 폭).
    private float LateralExtentOf(NavMeshModifierVolume volume)
    {
        Vector3 direction = Direction;
        if (direction == Vector3.zero)
            return 0f;

        Vector3 lateral = Vector3.Cross(Vector3.up, direction);
        Transform box = volume.transform;
        Vector3 half = Vector3.Scale(volume.size, box.lossyScale) * 0.5f;

        return 2f
            * (Mathf.Abs(Vector3.Dot(lateral, box.right * half.x))
                + Mathf.Abs(Vector3.Dot(lateral, box.up * half.y))
                + Mathf.Abs(Vector3.Dot(lateral, box.forward * half.z)));
    }

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

        Vector3 roadStart = RoadPoint;
        Vector3 roadEnd = roadStart + direction * m_runDistance;
        Vector3 spawn = StartPoint;
        Vector3 end = spawn + direction * RunDistance;
        Vector3 right = Vector3.Cross(Vector3.up, direction);

        // 도로 위에 얹혀 있어야 하는 구간 — 이게 건물을 통과하면 차도 통과한다
        Gizmos.color = Color.green;
        Gizmos.DrawLine(roadStart, roadEnd);
        Gizmos.DrawWireSphere(roadStart, 1f);

        // 맵 밖 구간 — 스폰·회수 자리다. 도로를 벗어나 있는 것이 정상이고, 여기가 엔진음 감쇠 구간이다 (#673)
        Gizmos.color = new Color(0.6f, 0.6f, 0.6f, 0.8f);
        Gizmos.DrawLine(spawn, roadStart);
        Gizmos.DrawLine(roadEnd, end);
        Gizmos.DrawWireSphere(spawn, 1.5f);

        // 진행 방향 화살촉 — 어느 쪽으로 달리는지가 배치의 전부다
        Gizmos.DrawLine(end, end - direction * 3f + right * 1.5f);
        Gizmos.DrawLine(end, end - direction * 3f - right * 1.5f);

        // 건너는 폭 — 하한 계산의 근거다. 플레이 중에는 Road 볼륨에서 읽은 실제 폭이 그려진다
        Gizmos.color = new Color(0f, 1f, 0f, 0.35f);
        float crossWidth = CrossWidth;
        Gizmos.DrawLine(roadStart - right * (crossWidth * 0.5f), roadStart + right * (crossWidth * 0.5f));
    }
}
