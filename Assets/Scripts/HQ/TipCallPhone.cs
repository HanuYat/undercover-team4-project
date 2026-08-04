using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 본부 제보 전화 (#102) — 간헐적으로 울리고, 받으면 대기 중이던 예비 용의자 1명이
/// 수배 리스트에 공개되면서 라운드 검거 할당량도 함께 1 늘어난다.
///
/// 한 라운드에 걸려오는 횟수는 라운드 시작에 [최소, 최대] 사이에서 뽑는다 — 이번 라운드에 몇 명이 더 늘어날지 미리 알 수 없다. 횟수는 '받았을 때'가 아니라 '울릴 때' 깎이므로, 자리를 비워
/// 놓친 전화도 한 번을 소모한다.
///
/// 서버 권위 — 수신 타이머·승격은 서버(또는 오프라인)에서만 돌고, 울림 여부만 동기화한다.
/// 벨소리·표시는 각 클라의 로컬 연출이므로 OnRingingChanged를 구독해 붙이면 된다(이 이슈 범위 밖).
/// 씬 배치·모델·콜라이더는 Editor 작업이다 — 코드는 상호작용 경로까지만 만든다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class TipCallPhone : NetworkBehaviour, IInteractable
{
    private CriminalAssigner Assigner => App.Game.CriminalAssigner;
    private RoundManager Round => App.Game.Round;

    [Header("수신 간격 (#102)")]
    [Tooltip("전화가 끊긴 뒤 다음 수신까지의 최소 대기(초). 최대값과의 사이에서 불규칙하게 뽑는다")]
    [Min(0f)]
    [SerializeField] private float m_minInterval = 40f;

    [Tooltip("다음 수신까지의 최대 대기(초)")]
    [Min(0f)]
    [SerializeField] private float m_maxInterval = 90f;

    [Tooltip("라운드 시작 후 첫 전화까지의 추가 유예(초) — 시작하자마자 울리지 않게")]
    [Min(0f)]
    [SerializeField] private float m_startDelay = 20f;

    [Header("수신 횟수 (#102)")]
    [Tooltip("한 라운드에 걸려올 전화 횟수의 하한. 라운드 시작에 상한과의 사이에서 뽑는다")]
    [Min(0)]
    [SerializeField] private int m_minCallCount = 2;

    [Tooltip("한 라운드에 걸려올 전화 횟수의 상한. 예비 용의자 풀이 이보다 작으면 풀이 먼저 마르므로, CriminalAssigner의 풀 크기를 '초기 공개 수 + 이 값'으로 잡을 것")]
    [Min(0)]
    [SerializeField] private int m_maxCallCount = 4;

    [Header("울림")]
    [Tooltip("한 번 울리고 스스로 끊기까지의 시간(초). 이게 곧 '현장 다녀올 수 있는 시간'이라 체감에 직결된다")]
    [Min(1f)]
    [SerializeField] private float m_ringDuration = 15f;

    // 울림 여부만 동기화한다 — 타이머와 승격은 서버 안에서 끝난다
    private readonly NetworkVariable<bool> m_isRingingSynced = new(false);

    // 서버·오프라인의 진실값 (NpcController.m_networkState와 동일 이중 구조)
    private bool m_isRinging;

    private float m_nextRingTime;
    private float m_ringEndTime;
    private bool m_scheduling;
    private int m_remainingCalls;
    private RoundPhase m_lastPhase = RoundPhase.Preparing;
    private bool m_warnedNoRound;

    /// <summary>지금 울리는 중인가. 서버·오프라인은 진실값, 원격 피어는 동기화 값.</summary>
    public bool IsRinging => IsSpawned && !IsServer ? m_isRingingSynced.Value : m_isRinging;

    /// <summary>울림 시작·종료 — 벨소리와 표시 연출이 구독할 훅. 전 피어에서 발행된다.</summary>
    public event Action OnRingingChanged;

    /// <summary>
    /// 전화를 받았다 — 받은 클라이언트의 id를 함께 넘긴다. 서버(또는 오프라인)에서만 발행된다. (#485)
    /// 전화기는 받았다는 사실만 알린다: 비밀 청탁을 붙일지·누구에게 무엇을 시킬지는
    /// <see cref="SecretFavorBroker"/>가 판단한다. 이 전화기에 청탁 로직까지 얹으면
    /// 타이머·횟수·울림에 의뢰 추적까지 붙어 단일 책임을 넘긴다.
    /// </summary>
    public event Action<ulong> OnAnswered;

    // 스폰 전(오프라인 단독 Play)이면 이 피어가 곧 권위다 — SuddenEventManager와 동일
    private bool IsAuthority => !IsSpawned || IsServer;

    public override void OnNetworkSpawn()
    {
        m_isRingingSynced.OnValueChanged += HandleRingingSyncedChanged;
    }

    public override void OnNetworkDespawn()
    {
        m_isRingingSynced.OnValueChanged -= HandleRingingSyncedChanged;
    }

    private void HandleRingingSyncedChanged(bool previous, bool current) => OnRingingChanged?.Invoke();

    // ---- 상호작용 (#184) ----

    public void Interact(GameObject interactor)
    {
        if (!CanInteract(interactor)) return;

        // 세션 밖(오프라인 단독 Play)에서는 RPC를 보낼 곳이 없다 — 이 피어가 곧 서버다
        if (!IsSpawned)
        {
            Answer(NetworkManager.ServerClientId);
            return;
        }

        RequestAnswerRpc();
    }

    /// <summary>울리는 중에만 받을 수 있다 — 조준 윤곽선도 그때만 켜진다. (#184)</summary>
    public bool CanInteract(GameObject interactor) => IsRinging;

    [Rpc(SendTo.Server)]
    private void RequestAnswerRpc(RpcParams rpcParams = default)
    {
        // CanInteract는 조준 피드백용 클라이언트 게이팅이라 RPC 직접 호출을 막지 못한다.
        // 서버에서 한 번 더 검증한다 — CCTVSwitcher.RequestTogglePowerRpc와 같은 이유 (#362)
        if (!m_isRinging) return;

        // 발신 클라이언트가 곧 '받은 사람'이다 — 비밀 청탁의 대상자 식별 근거 (#485)
        Answer(rpcParams.Receive.SenderClientId);
    }

    // ---- 수신 스케줄 (서버 · 오프라인 전용) ----

    private void Update()
    {
        if (!IsAuthority)
            return;

        if (Round == null)
        {
            // 여기서 조용히 return하면 전화가 영영 안 오는데 아무 단서도 안 남는다 — 1회만 알린다
            if (!m_warnedNoRound)
            {
                m_warnedNoRound = true;
                Debug.LogWarning("TipCallPhone: RoundManager를 찾지 못해 수신 타이머가 돌지 않는다", this);
            }
            return;
        }

        // 라운드 페이즈 전이 감지 — InProgress 진입 시 수신 시작, 이탈 시 울림을 끊고 멈춘다
        RoundPhase phase = Round.Phase;
        if (phase != m_lastPhase)
        {
            HandlePhaseChanged(phase);
            m_lastPhase = phase;
        }

        if (!m_scheduling)
            return;

        if (m_isRinging)
        {
            // 울림 시간 초과 = 놓침. 승격 없이 다음 수신만 예약한다 — 기회는 복구되지 않는다
            if (Time.time >= m_ringEndTime)
            {
                SetRinging(false);
                ScheduleNext();
                Debug.Log("[제보 전화] 받지 않아 끊겼다 — 수배 공개 기회를 놓쳤다");
            }
            return;
        }

        if (Time.time < m_nextRingTime)
            return;

        // 이번 라운드에 배정된 수신 횟수를 다 썼다 — 예비 용의자가 남아 있어도 더 울리지 않는다 (#102)
        if (m_remainingCalls <= 0)
        {
            m_scheduling = false;
            Debug.Log("[제보 전화] 이번 라운드 수신 횟수를 모두 소진해 더 이상 전화가 오지 않는다");
            return;
        }

        // 예비 풀이 비면 전화를 멈춘다 — 받아도 아무 일 없는 전화는 플레이어를 속이는 셈이고,
        // 본부 상주 압박도 그 시점엔 풀어주는 게 맞다 (#102 설계 결정 5)
        if (Assigner == null || !Assigner.HasPendingSuspect)
        {
            m_scheduling = false;
            Debug.Log("[제보 전화] 예비 용의자가 모두 공개되어 더 이상 전화가 오지 않는다");
            return;
        }

        // 받든 놓치든 이 한 번은 소모된다 — 놓친 전화가 진짜 손실이 되려면 받은 시점이 아니라 여기서 깎아야 한다
        m_remainingCalls--;
        SetRinging(true);
        m_ringEndTime = Time.time + m_ringDuration;
        Debug.Log($"[제보 전화] 수신 — {m_ringDuration:0}초 안에 받아야 한다 (남은 수신 {m_remainingCalls}회)");
    }

    private void HandlePhaseChanged(RoundPhase phase)
    {
        if (phase == RoundPhase.InProgress)
        {
            m_scheduling = true;
            // 상한이 하한보다 작게 설정돼도 하한은 보장한다 — 인스펙터 오설정으로 전화가 0회가 되지 않게
            int maxCalls = Mathf.Max(m_minCallCount, m_maxCallCount);
            m_remainingCalls = UnityEngine.Random.Range(m_minCallCount, maxCalls + 1);
            ScheduleNext(m_startDelay);
            Debug.Log($"[제보 전화] 이번 라운드 수신 횟수: {m_remainingCalls}회");
        }
        else
        {
            // 라운드가 끝났거나 준비 상태로 되돌아감 — 울리던 전화를 끊고 수신을 멈춘다
            m_scheduling = false;
            SetRinging(false);
        }
    }

    // 전화를 받았다 — 서버(또는 오프라인)에서만 돈다
    private void Answer(ulong answeredBy)
    {
        SetRinging(false);

        if (Assigner == null)
        {
            Debug.LogWarning("TipCallPhone: CriminalAssigner를 찾지 못해 수배를 공개할 수 없다", this);
        }
        else if (Assigner.PromoteNext())
        {
            // 라운드 목표는 검거 인원 수가 아니라 금액이라(#395) 여기서 목표를 건드리지 않는다.
            // 제보 전화의 역할은 '벌 수 있는 총액을 늘리는 것'이다 — 승격된 용의자의 현상금이
            // CriminalAssigner.PromoteNext에서 진범 몫으로 배정되고, 잡아서 유치장에 넣으면
            // JailZone.BountyTotal로 목표 진행도에 반영된다.
            // (#102의 Round.AddQuota(1) 호출은 할당량 개념과 함께 폐기됐다)
        }
        else
        {
            // 지금 승격 가능한 후보가 없다(연행 중 등). 풀 소진 판정은 HasPendingSuspect가 따로 하므로
            // 여기서 멈추지 않는다 — 다음 전화 때 다시 시도한다
            Debug.Log("[제보 전화] 받았지만 지금 공개할 수 있는 용의자가 없다 — 다음 전화를 기다린다");
        }

        // 수배 갱신(팀 이득)은 위에서 이미 끝났다. 청탁은 그 위에 얹히므로, 청탁이 붙어도
        // 팀은 갱신 기회를 잃지 않는다 — "모르는 사이에 손해"를 만들지 않기 위한 순서다 (#485)
        OnAnswered?.Invoke(answeredBy);

        ScheduleNext();
    }

    private void ScheduleNext(float extraDelay = 0f)
    {
        m_nextRingTime = Time.time + extraDelay + UnityEngine.Random.Range(m_minInterval, m_maxInterval);

        // 첫 전화까지 startDelay + 간격이라 1~2분이 걸릴 수 있다. 이 로그가 없으면 타이머가 도는지
        // 죽었는지 구분할 방법이 없다 — 서버(또는 오프라인)에서만 찍힌다.
        Debug.Log($"[제보 전화] 다음 수신 예약 — {m_nextRingTime - Time.time:0}초 뒤");
    }

    private void SetRinging(bool value)
    {
        if (m_isRinging == value)
            return;

        m_isRinging = value;

        // 호스트는 동기화 변수 쓰기가 곧 자기 이벤트 발행이 아니다 — 아래에서 직접 발행한다
        if (IsSpawned && IsServer)
            m_isRingingSynced.Value = value;

        OnRingingChanged?.Invoke();
    }
}
