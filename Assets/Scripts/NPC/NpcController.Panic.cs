using UnityEngine;

/// <summary>
/// NpcController의 패닉 파트 — 소란 전파와 패닉 전이 (#81).
/// 저항·도주 중인 NPC가 주기적으로 주변 시민을 놀래키고, 놀란 시민은 Panic 상태로 흩어진다.
/// 이동만 했고 동작 변화는 없다 (도메인 partial 분리).
/// </summary>
public partial class NpcController
{
    [Header("패닉 (#81)")]
    [Tooltip("소란(저항 전투·도주)이 주변 시민을 패닉시키는 전파 반경(m)")]
    [SerializeField] private float m_disturbanceRadius = 8f;
    [Tooltip("저항·도주 중 소란 펄스를 발산하는 주기(초) — 소란이 계속되면 지나가던 시민도 놀란다")]
    [SerializeField] private float m_disturbancePulseInterval = 1f;
    [Tooltip("패닉 시 기본 이동 속도에 곱하는 배율")]
    [SerializeField] private float m_panicSpeedMultiplier = 1.8f;
    [Tooltip("패닉 도주 지점을 한 번에 이만큼(m) 앞으로 잡는다")]
    [SerializeField] private float m_panicStepDistance = 8f;
    [Tooltip("마지막 소란 감지 후 이 시간(초)이 지나면 진정하고 배회로 복귀")]
    [SerializeField] private float m_panicCalmSeconds = 5f;

    // 소란 전파용 공유 버퍼 — 서버(또는 오프라인)에서만 쓰므로 공유해도 안전하다
    private static readonly Collider[] s_disturbanceBuffer = new Collider[64];

    private float m_nextDisturbancePulseTime;

    public float PanicSpeedMultiplier => m_panicSpeedMultiplier;
    public float PanicStepDistance => m_panicStepDistance;
    public float PanicCalmSeconds => m_panicCalmSeconds;

    /// <summary>패닉의 원인이 된 소란 지점 — 이 반대 방향으로 달아난다. 서버에서만 유효. (#81)</summary>
    public Vector3 PanicSource { get; private set; }

    /// <summary>마지막으로 소란을 감지한 시각(Time.time) — 패닉 진정 타이머 기준. 서버에서만 유효. (#81)</summary>
    public float LastDisturbedTime { get; private set; }

    // 저항·도주 중인 NPC는 그 자체가 소란의 원천 — 주기적으로 주변 시민을 패닉시킨다 (#81)
    // 코어 Update(서버 전용 Tick)가 매 프레임 호출한다.
    private void EmitDisturbancePulse()
    {
        NpcState state = m_stateMachine.CurrentState;
        if (state != NpcState.Run && state != NpcState.Attack)
            return;

        RequestDisturbancePulse();
    }

    /// <summary>
    /// 소란 펄스를 1회 요청한다 — 상태 클래스가 자기 사정으로 소란을 낼 때 쓴다.
    /// 주기 스로틀은 자동 펄스(<see cref="EmitDisturbancePulse"/>)와 공유하므로 펄스 타이머가 둘로 갈라지지 않는다.
    /// 서버(또는 오프라인) 전용 — FSM Tick 안에서만 불린다. (#81, #230)
    /// </summary>
    public void RequestDisturbancePulse()
    {
        if (Time.time < m_nextDisturbancePulseTime)
            return;

        m_nextDisturbancePulseTime = Time.time + m_disturbancePulseInterval;
        BroadcastDisturbance(transform.position, m_disturbanceRadius);
    }

    /// <summary>
    /// 소란 발생 — position 반경 radius 안의 배회 NPC를 전부 패닉시킨다. (#81)
    /// 저항·도주 NPC의 펄스가 호출하며, 돌발 이벤트(GDD 6-4 후속 이슈)도 이 API로 소란을 일으킨다.
    /// 서버(또는 오프라인)에서 호출할 것 — 클라이언트에서 불려도 각 NPC의 EnterPanic이 무시한다.
    /// </summary>
    public static void BroadcastDisturbance(Vector3 position, float radius)
    {
        int hitCount = Physics.OverlapSphereNonAlloc(position, radius, s_disturbanceBuffer);
        for (int i = 0; i < hitCount; i++)
        {
            NpcController npc = s_disturbanceBuffer[i].GetComponentInParent<NpcController>();
            if (npc != null)
                npc.EnterPanic(position);
        }
    }

    /// <summary>
    /// 패닉 진입/갱신 — 배회 중(Idle/Walk)일 때만 전이한다.
    /// 검거(채널링·체포·연행)는 소란이 아니므로 이 메서드를 부르지 않고,
    /// 체포·연행·기절·반응 중(도주/저항)인 NPC는 여기서 걸러진다.
    /// 이미 패닉 중이면 소란 지점·진정 타이머만 갱신한다 (도주 방향은 다음 지점 갱신 때 반영).
    /// </summary>
    public void EnterPanic(Vector3 disturbancePosition)
    {
        if (IsSpawned && !IsServer)
            return;

        NpcState state = m_stateMachine.CurrentState; // 서버 진실값 — 동기화 지연 없이 판정
        if (state == NpcState.Panic)
        {
            PanicSource = disturbancePosition;
            LastDisturbedTime = Time.time;
            return;
        }

        if (state != NpcState.Idle && state != NpcState.Walk)
            return;

        PanicSource = disturbancePosition;
        LastDisturbedTime = Time.time;
        m_stateMachine.ChangeState(NpcState.Panic);
    }
}
