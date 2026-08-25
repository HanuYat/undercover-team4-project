using Unity.Netcode;
using UnityEngine;

/// <summary>
/// UFO 기체 (#819) — 상공을 날아다니며 지면에 빔을 비춘다.
///
/// <b>이 컴포넌트는 움직임과 겉모습만 든다.</b> 누가 빔에 걸렸는지·걸리면 어떻게 되는지는
/// <see cref="UfoAbductionEvent"/>가 서버 권위로 판정한다. 나눈 이유는 스폰형 이벤트를 골격과
/// 행동으로 가른 것과 같다 — 판정이 기체에 붙어 있으면 "지나가기만 하는 UFO" 같은 다른 연출에
/// 재사용할 수 없다.
///
/// <b>위치는 서버가 민다</b> — 오너가 없는 스폰물이라 NetworkTransform의 기본(서버 권위)이 그대로 맞는다.
/// 플레이어를 옮길 때 오너가 스스로 움직여야 했던 사정(<see cref="PlayerTowedMotion"/>)과 정반대다.
///
/// <b>빔은 RPC가 아니라 <see cref="NetworkVariable{T}"/>로 켠다</b> — 늦게 들어온 피어도 이미 켜져 있는
/// 빔을 봐야 하기 때문이다. RPC로 켜면 그 순간 접속해 있던 사람에게만 보인다. 굵기·길이를 값에 함께
/// 실어 보내는 이유도 같다: 판정 반경은 이벤트가 쥐고 있으므로 기체가 따로 알 방법이 없고,
/// 두 곳에 같은 값을 두면 <b>보이는 빔과 걸리는 범위가 어긋난다</b>.
///
/// 세션 없이 씬만 Play하는 테스트에서도 돌아야 하므로, 스폰되지 않았으면 로컬 값으로 떨어진다
/// (<see cref="SuddenEventUtil.IsNetworkSessionActive"/>를 보는 다른 스폰물과 같은 방침).
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class UfoCraft : NetworkBehaviour
{
    /// <summary>빔 한 줄기의 상태 — 켜짐·굵기·닿는 높이. 셋이 늘 함께 바뀌므로 한 값으로 묶는다.</summary>
    public struct BeamState : INetworkSerializable
    {
        public bool On;
        public float Radius;  // m — 판정 반경과 같은 값이다
        public float GroundY; // 빔이 닿는 지면 높이(월드 y)

        public void NetworkSerialize<T>(BufferSerializer<T> serializer)
            where T : IReaderWriter
        {
            serializer.SerializeValue(ref On);
            serializer.SerializeValue(ref Radius);
            serializer.SerializeValue(ref GroundY);
        }
    }

    [Header("겉모습")]
    [Tooltip("빔 기둥의 뿌리 — 기체 원점에 두고 [b]아래로 2유닛[/b] 길이의 메시를 자식으로 둘 것. " +
             "이 트랜스폼의 배율로 굵기와 길이를 맞춘다. 비우면 빔이 안 보일 뿐 판정은 그대로 돈다")]
    [SerializeField] private Transform m_beamPivot;

    [Tooltip("기체가 제자리에서 도는 속도(도/초) — 순수 연출")]
    [SerializeField] private float m_spinDegreesPerSecond = 30f;

    [Tooltip("떠 있는 높이를 위아래로 흔드는 폭(m). 0이면 흔들지 않는다")]
    [Min(0f)]
    [SerializeField] private float m_bobAmplitude = 0.4f;

    [Tooltip("위아래 흔들림 한 주기(초)")]
    [Min(0.1f)]
    [SerializeField] private float m_bobPeriod = 3f;

    [Header("비행")]
    [Tooltip("이동 속도(m/s)")]
    [Min(0.1f)]
    [SerializeField] private float m_flySpeed = 12f;

    [Tooltip("목적지까지 수평으로 이 거리(m) 안에 들어오면 도착으로 본다")]
    [Min(0.1f)]
    [SerializeField] private float m_arriveDistance = 1.5f;

    // 쓰기는 서버만. 늦게 들어온 피어는 OnNetworkSpawn에서 현재 값을 한 번 적용받는다.
    private readonly NetworkVariable<BeamState> m_beam = new NetworkVariable<BeamState>();

    // 세션 없이 Play할 때의 폴백 — 스폰되지 않은 NetworkVariable에는 쓰지 않는다
    private BeamState m_beamLocal;

    // 서버가 향하는 지점. 흔들림은 여기에 얹으므로 목적지 자체는 흔들리지 않는다.
    private Vector3 m_destination;
    private bool m_hasDestination;

    private float m_bobPhase;

    /// <summary>지금 빔이 켜져 있는가.</summary>
    public bool IsBeamOn => (IsSpawned ? m_beam.Value : m_beamLocal).On;

    /// <summary>
    /// 목적지에 닿았는가 — 서버 판정. <b>수평 거리만 본다</b>: 흔들림(bob)이 높이를 계속 바꾸므로
    /// 3D 거리로 재면 진폭에 따라 영영 도착하지 못한다. 목적지가 없으면 참이다(대기 중).
    /// </summary>
    public bool HasArrived
    {
        get
        {
            if (!m_hasDestination)
                return true;

            Vector3 delta = m_destination - transform.position;
            delta.y = 0f;
            return delta.sqrMagnitude <= m_arriveDistance * m_arriveDistance;
        }
    }

    public override void OnNetworkSpawn()
    {
        m_beam.OnValueChanged += HandleBeamChanged;
        ApplyBeam(m_beam.Value); // 늦게 들어온 피어 — 값은 복제받지만 변경 통지는 못 받는다
    }

    public override void OnNetworkDespawn()
    {
        m_beam.OnValueChanged -= HandleBeamChanged;
    }

    private void Awake()
    {
        // 프리팹에 켜 둔 채 저장돼 있어도 스폰 전 한 프레임 동안 빔이 보이지 않게 한다
        ApplyBeam(default);

        // 개체마다 다른 위상으로 흔들린다 — 여럿이 떠 있을 때 한 몸처럼 오르내리지 않게
        m_bobPhase = Random.Range(0f, Mathf.PI * 2f);
    }

    /// <summary>서버 전용 — 이 지점으로 날아간다. 도착 판정은 <see cref="HasArrived"/>.</summary>
    public void ServerFlyTo(Vector3 worldPosition)
    {
        m_destination = worldPosition;
        m_hasDestination = true;
    }

    /// <summary>서버 전용 — 그 자리에 머문다(이동 정지). 흔들림·회전은 계속한다.</summary>
    public void ServerHold()
    {
        m_hasDestination = false;
    }

    /// <summary>
    /// 서버 전용 — 빔을 켠다. <paramref name="radius"/>는 <b>판정 반경과 같은 값</b>이어야 한다.
    /// 보이는 굵기와 걸리는 범위가 어긋나면 플레이어가 규칙을 배울 수 없다.
    /// </summary>
    public void ServerSetBeam(bool on, float radius = 0f, float groundY = 0f)
    {
        var next = new BeamState { On = on, Radius = radius, GroundY = groundY };

        if (IsSpawned)
            m_beam.Value = next;
        else
            ApplyBeam(next); // 세션 없는 Play 테스트

        m_beamLocal = next;
    }

    private void Update()
    {
        // 연출은 전 피어가 각자 돈다 — 회전까지 복제할 이유가 없다
        if (m_spinDegreesPerSecond != 0f)
            transform.Rotate(Vector3.up, m_spinDegreesPerSecond * Time.deltaTime, Space.World);

        // 켜져 있는 동안 길이를 매 프레임 다시 맞춘다 — 기체가 흔들리고 움직이므로 지면까지의 거리가 변한다
        if (IsBeamOn)
            StretchBeam(IsSpawned ? m_beam.Value : m_beamLocal);

        // 위치는 서버만 민다. 나머지 피어는 NetworkTransform이 채운다.
        if (!IsServerAuthority)
            return;

        Vector3 position = transform.position;

        // 흔들림을 뺀 기준 높이로 옮긴 뒤 새 흔들림을 얹는다 — 목적지 높이에 진폭이 더해진 채로
        // 수렴하면 상하로 떨면서 영영 도착하지 못한다
        position.y -= BobOffset();

        if (m_hasDestination)
            position = Vector3.MoveTowards(position, m_destination, m_flySpeed * Time.deltaTime);

        m_bobPhase += m_bobPeriod > 0f ? Time.deltaTime * (Mathf.PI * 2f / m_bobPeriod) : 0f;
        position.y += BobOffset();
        transform.position = position;
    }

    private float BobOffset() => m_bobAmplitude <= 0f ? 0f : Mathf.Sin(m_bobPhase) * m_bobAmplitude;

    // 이벤트 프레임워크는 서버에서만 돌지만 이 Update는 스스로 도므로 직접 게이트한다 (AbductionEvent와 같은 패턴)
    private bool IsServerAuthority =>
        NetworkManager.Singleton == null
        || !NetworkManager.Singleton.IsListening
        || NetworkManager.Singleton.IsServer;

    private void HandleBeamChanged(BeamState previous, BeamState current) => ApplyBeam(current);

    private void ApplyBeam(BeamState state)
    {
        if (m_beamPivot == null)
            return;

        m_beamPivot.gameObject.SetActive(state.On);
        if (state.On)
            StretchBeam(state);
    }

    // 자식 메시가 아래로 2유닛이라는 전제 위에서 굵기·길이를 배율로 맞춘다 (Unity 기본 Cylinder가 그렇다).
    // 기체가 회전하지만 요 회전뿐이라 기둥은 늘 수직이다.
    private void StretchBeam(BeamState state)
    {
        float length = Mathf.Max(0.1f, transform.position.y - state.GroundY);
        float diameter = Mathf.Max(0.1f, state.Radius * 2f);
        m_beamPivot.localScale = new Vector3(diameter, length * 0.5f, diameter);
    }
}
