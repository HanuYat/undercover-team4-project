using Unity.Netcode;
using UnityEngine;

/// <summary>
/// UFO 기체 (#819) — 맵 상공을 <b>늘 떠다니며 빔을 켜 놓고</b> 있는 상주 기믹이다.
///
/// <b>돌발 이벤트가 아니다.</b> 스폰되지도 사라지지도 않고 씬에 놓인 채로 라운드 내내 돌아다닌다 —
/// 그래서 <see cref="SuddenEventManager"/> 풀에 등록하지 않는다. 저 위에 뭔가 떠 있고 그것이
/// 천천히 다가온다는 상시 압박이 이 기믹의 값이고, 발생·종료를 스케줄러가 쥐면 그 값이 사라진다.
///
/// <b>이 컴포넌트는 움직임과 겉모습만 든다.</b> 빔에 걸린 사람을 어떻게 할지는 같은 오브젝트의
/// <see cref="UfoAbductor"/>가 서버 권위로 판정한다. 나눈 이유는 기체를 판정 없이 재사용할 수 있게
/// 하려는 것이다(그냥 지나가는 UFO).
///
/// <b>위치는 서버가 민다</b> — 오너가 없는 씬 배치물이라 NetworkTransform의 기본(서버 권위)이 맞는다.
///
/// <b>빔 길이는 각 피어가 스스로 잰다.</b> 늘 켜져 있으므로 상태를 복제할 것이 없고, 지면까지의
/// 거리는 기체 위치(이미 복제된다)에서 아래로 레이를 쏘면 나온다. 판정도 같은 레이를 쓰므로
/// <b>보이는 기둥과 걸리는 범위가 어긋나지 않는다</b>.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class UfoCraft : NetworkBehaviour
{
    [Header("빔")]
    [Tooltip("빔 기둥의 뿌리 — 기체 원점에 두고 [b]아래로 2유닛[/b] 길이의 메시를 자식으로 둘 것. " +
             "이 트랜스폼의 배율로 굵기와 길이를 맞춘다. 비우면 빔이 안 보일 뿐 판정은 그대로 돈다")]
    [SerializeField] private Transform m_beamPivot;

    [Tooltip("빔 반경(m) — 보이는 굵기와 걸리는 범위가 모두 이 값이다")]
    [Min(0.5f)]
    [SerializeField] private float m_beamRadius = 3f;

    [Tooltip("지면을 찾을 때 볼 레이어 — 환경만 넣을 것. 사람이 들어가면 머리 위에서 빔이 끊긴다")]
    [SerializeField] private LayerMask m_groundMask = 1; // Default

    [Tooltip("이 거리(m) 안에서 지면을 못 찾으면 기체 바로 아래를 지면으로 친다")]
    [Min(1f)]
    [SerializeField] private float m_groundProbeDistance = 200f;

    [Tooltip("빔을 멈춰 세우는 면의 최소 가로세로(m) — 이보다 좁으면 가로등·간판으로 보고 통과한다. " +
             "실측: 가로등 0.34~0.5, 간판 0.19, 인도 조각 2.5, 건물 9.5")]
    [Min(0f)]
    [SerializeField] private float m_blockingFootprint = 2f;

    [Tooltip("기둥 밑면을 지면에서 이만큼(m) 띄운다 — 0이면 밑면과 지면이 겹쳐 그 자리가 번쩍인다")]
    [Min(0f)]
    [SerializeField] private float m_beamGroundClearance = 0.2f;

    private static readonly RaycastHit[] s_groundHitBuffer = new RaycastHit[16];

    [Header("배회")]
    [Tooltip("씬에 놓인 처음 자리를 중심으로 이 반경(m) 안을 떠다닌다")]
    [Min(1f)]
    [SerializeField] private float m_roamRadius = 90f;

    [Tooltip("이동 속도(m/s) — 느릴수록 피할 시간이 길어진다. 이 값이 곧 난이도다")]
    [Min(0.1f)]
    [SerializeField] private float m_roamSpeed = 5f;

    [Tooltip("다음 목적지에 이 거리(m) 안까지 오면 새 목적지를 고른다")]
    [Min(0.5f)]
    [SerializeField] private float m_arriveDistance = 3f;

    [Header("연출")]
    [Tooltip("기체가 제자리에서 도는 속도(도/초)")]
    [SerializeField] private float m_spinDegreesPerSecond = 20f;

    [Tooltip("떠 있는 높이를 위아래로 흔드는 폭(m). 0이면 흔들지 않는다")]
    [Min(0f)]
    [SerializeField] private float m_bobAmplitude = 0.5f;

    [Tooltip("위아래 흔들림 한 주기(초)")]
    [Min(0.1f)]
    [SerializeField] private float m_bobPeriod = 4f;

    private Vector3 m_home;        // 씬에 놓인 자리 — 배회 반경의 중심
    private Vector3 m_destination; // 지금 향하는 곳 (흔들림을 뺀 기준 높이)
    private float m_bobPhase;
    private bool m_held;           // 제자리 정지 — 빨아올리는 동안 판정부가 건다

    /// <summary>빔 반경 — 판정도 이 값을 쓴다.</summary>
    public float BeamRadius => m_beamRadius;

    /// <summary>하늘을 막는 것으로 치는 레이어 — 판정부의 실내 검사도 같은 값을 써야 보이는 것과 걸리는 것이 맞는다. (#885)</summary>
    public LayerMask GroundMask => m_groundMask;

    /// <summary>
    /// 서버 전용 — 제자리에 세우거나 다시 배회시킨다. <b>빨아올리는 동안</b> 판정부가 건다:
    /// 기체가 계속 날아가면 매달린 몸이 하늘을 가로질러 끌려가고, 빔도 발밑을 떠나
    /// "저 기둥에 잡혔다"가 화면에서 성립하지 않는다. 흔들림·회전은 계속한다.
    /// </summary>
    public void ServerSetHold(bool held) => m_held = held;

    private void Awake()
    {
        m_home = transform.position;
        m_destination = m_home;

        // 개체마다 다른 위상으로 흔들린다 — 여럿이 떠 있을 때 한 몸처럼 오르내리지 않게
        m_bobPhase = Random.Range(0f, Mathf.PI * 2f);

        if (m_beamPivot != null)
            m_beamPivot.gameObject.SetActive(true); // 상시 점등 — 끄고 켜는 상태가 없다
    }

    /// <summary>
    /// 빔이 닿는 지면 지점 — 기체 바로 아래로 레이를 쏴 찾는다. 못 찾으면 기체 아래
    /// <see cref="m_groundProbeDistance"/>만큼을 지면으로 친다(허공을 지날 때의 폴백).
    /// </summary>
    public Vector3 BeamGroundPoint() => BeamGroundPoint(out _);

    /// <summary>지면 지점과 함께 실제로 레이가 맞았는지도 돌려준다 — <see cref="StretchBeam"/>이
    /// 폴백 거리(기본 200m)로 시각 기둥을 늘리지 않게 가르는 데 쓴다.</summary>
    private Vector3 BeamGroundPoint(out bool grounded)
    {
        Vector3 origin = transform.position;

        int count = Physics.RaycastNonAlloc(
            origin, Vector3.down, s_groundHitBuffer, m_groundProbeDistance,
            m_groundMask, QueryTriggerInteraction.Ignore);

        if (count == 0)
        {
            grounded = false;
            return origin + Vector3.down * m_groundProbeDistance;
        }

        // <b>빔을 막을 만큼 넓은 것 중 가장 가까운(=가장 높은) 히트</b>가 지면이다. 가로등 같은 얇은
        // 소품은 건너뛰고(#819), 건물 지붕에서는 멈춘다 — 가장 먼 히트를 쓰면 지붕을 뚫고 내려가
        // 기둥이 건물을 관통하고(#890) 실내에 선 사람이 빨려 올라갔다 (#885).
        int nearest = -1;
        int farthest = 0;
        for (int i = 0; i < count; i++)
        {
            if (s_groundHitBuffer[i].distance > s_groundHitBuffer[farthest].distance)
                farthest = i;

            if (!BlocksBeam(s_groundHitBuffer[i].collider))
                continue;

            if (nearest < 0 || s_groundHitBuffer[i].distance < s_groundHitBuffer[nearest].distance)
                nearest = i;
        }

        grounded = true;
        // 전부 얇은 소품뿐이면 종전대로 가장 먼 히트 — 기둥이 소품 위에 떠서 멈추는 것보다 낫다
        return s_groundHitBuffer[nearest >= 0 ? nearest : farthest].point;
    }

    // 빔을 멈춰 세울 만큼 넓은 면인가 — 도로·인도·지붕은 참, 기둥·간판은 거짓.
    private bool BlocksBeam(Collider collider)
    {
        Vector3 size = collider.bounds.size;
        return Mathf.Min(size.x, size.z) >= m_blockingFootprint;
    }

    private void Update()
    {
        // 연출은 전 피어가 각자 돈다 — 회전까지 복제할 이유가 없다
        if (m_spinDegreesPerSecond != 0f)
            transform.Rotate(Vector3.up, m_spinDegreesPerSecond * Time.deltaTime, Space.World);

        StretchBeam();

        // 위치는 서버만 민다. 나머지 피어는 NetworkTransform이 채운다.
        if (!HasServerAuthority)
            return;

        // 흔들림을 뺀 기준 높이에서 옮긴 뒤 새 흔들림을 얹는다 — 진폭이 더해진 채로 수렴하면
        // 상하로 떨면서 영영 도착하지 못한다
        Vector3 position = transform.position;
        position.y -= BobOffset();

        // 세워 둔 동안에는 목적지도 새로 고르지 않는다 — 풀리는 순간 가던 곳으로 이어 간다. 흔들림도 함께 멈춘다.
        if (!m_held)
        {
            position = Vector3.MoveTowards(position, m_destination, m_roamSpeed * Time.deltaTime);

            Vector3 flat = m_destination - position;
            flat.y = 0f;
            if (flat.sqrMagnitude <= m_arriveDistance * m_arriveDistance)
                PickDestination();

            m_bobPhase += m_bobPeriod > 0f ? Time.deltaTime * (Mathf.PI * 2f / m_bobPeriod) : 0f;
        }

        position.y += BobOffset();
        transform.position = position;
    }

    // 처음 자리를 중심으로 한 원 안에서 다음 목적지를 고른다. 고도는 놓인 높이를 그대로 지킨다 —
    // 지형을 따라 오르내리게 하려면 지면을 읽어야 하는데, 그러면 건물 위를 지날 때 기체가 튄다.
    private void PickDestination()
    {
        Vector2 offset = Random.insideUnitCircle * m_roamRadius;
        m_destination = new Vector3(m_home.x + offset.x, m_home.y, m_home.z + offset.y);
    }

    private float BobOffset() => m_bobAmplitude <= 0f ? 0f : Mathf.Sin(m_bobPhase) * m_bobAmplitude;

    // 자식 메시가 아래로 2유닛이라는 전제 위에서 굵기·길이를 배율로 맞춘다 (Unity 기본 Cylinder가 그렇다).
    // 기체가 회전하지만 요 회전뿐이라 기둥은 늘 수직이다.
    private void StretchBeam()
    {
        if (m_beamPivot == null)
            return;

        Vector3 groundPoint = BeamGroundPoint(out bool grounded);

        // 지면을 못 찾았다면(맵 밖·허공 위) 판정은 그대로 폴백 지점을 쓰되(그 자리엔 아무도 없다),
        // 시각 기둥은 200m짜리로 늘리는 대신 접어 둔다 — 허공에 뜬 긴 기둥이 눈에 띄지 않게.
        // 지면에서 살짝 띄운다 — 가산 블렌딩이라 밑면이 지면과 겹치면 그 자리가 번쩍인다 (#890)
        float length = grounded
            ? Mathf.Max(0.1f, transform.position.y - groundPoint.y - m_beamGroundClearance)
            : 0f;
        float diameter = m_beamRadius * 2f;
        m_beamPivot.localScale = new Vector3(diameter, length * 0.5f, diameter);
    }

    // 씬 배치물이라 자기 Update가 스스로 돈다 — 서버 권한을 직접 게이트한다 (AbductionEvent와 같은 패턴)
    private bool HasServerAuthority =>
        NetworkManager.Singleton == null
        || !NetworkManager.Singleton.IsListening
        || NetworkManager.Singleton.IsServer;
}
