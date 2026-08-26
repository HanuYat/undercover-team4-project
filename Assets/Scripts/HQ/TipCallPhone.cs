using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 본부 제보 전화 (#102) — 간헐적으로 울리고, 벨이 다 울리면 자동으로 받혀 예비 용의자 1명이
/// 수배 리스트에 공개된다. 상호작용(<see cref="Interact"/>)은 그 시간을 앞당길 뿐이다 (#663).
/// 수신 횟수는 라운드 시작에 [최소, 최대] 사이에서 뽑고, '울릴 때' 깎인다.
///
/// <see cref="TryRingExternal"/>(#485)로도 울린다 — 그 벨은 수신 횟수·승격에 영향이 없고 겉모습도
/// 동일해야 한다(옆 사람이 용무를 구분하면 안 됨).
///
/// 서버 권위 — 타이머·승격은 서버(또는 오프라인)에서만 돌고 울림 여부만 동기화한다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class TipCallPhone : NetworkBehaviour, IInteractable
{
    private CriminalAssigner Assigner => App.Game.CriminalAssigner;
    private RoundManager Round => App.Game.Round;

    [Header("수신 간격 (#102)")]
    [Tooltip("다음 수신까지 최소 대기(초)")]
    [Min(0f)]
    [SerializeField] private float m_minInterval = 40f;

    [Tooltip("다음 수신까지 최대 대기(초)")]
    [Min(0f)]
    [SerializeField] private float m_maxInterval = 90f;

    [Tooltip("라운드 시작 후 첫 전화까지 유예(초)")]
    [Min(0f)]
    [SerializeField] private float m_startDelay = 20f;

    [Header("수신 횟수 (#102)")]
    [Tooltip("한 라운드 전화 횟수 하한")]
    [Min(0)]
    [SerializeField] private int m_minCallCount = 2;

    [Tooltip("한 라운드 전화 횟수 상한. CriminalAssigner의 예비 풀 크기가 이보다 작으면 풀이 먼저 마른다")]
    [Min(0)]
    [SerializeField] private int m_maxCallCount = 4;

    [Header("울림")]
    [Tooltip("벨이 울리다 자동으로 받히기까지의 시간(초) — 상호작용으로 그 전에 받을 수도 있다")]
    [Min(1f)]
    [SerializeField] private float m_ringDuration = 3f;

    [Tooltip("외부 요청 벨(#485)이 끝난 뒤 제보 전화까지 비워 두는 간격(초). 짧게 유지 — 늘리면 라운드 막판 제보 전화가 시간에 밀려 못 울릴 수 있다")]
    [Min(0f)]
    [SerializeField] private float m_externalRingGrace = 8f;

    // 울림 여부만 동기화 — 타이머·승격은 서버 안에서 끝난다
    private readonly NetworkVariable<bool> m_isRingingSynced = new(false);

    // 서버·오프라인의 진실값 (NpcController.m_networkState와 동일 구조)
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

    // 스폰 전(오프라인 단독 Play)이면 이 피어가 곧 권위다 — SuddenEventManager와 동일
    private bool IsAuthority => !IsSpawned || IsServer;

    // 채워져 있으면 외부 요청 벨(TryRingExternal) — 받으면 승격 대신 이 콜백을 부른다. 서버(또는 오프라인) 전용.
    private Action<ulong> m_externalAnswered;

    /// <summary>
    /// 외부 요청으로 벨을 울린다 (#485) — 받으면 <paramref name="onAnswered"/>에 받은 클라이언트 id가 온다.
    /// 수배는 승격되지 않고 제보 전화 횟수도 소모하지 않는다. 이미 울리는 중이거나 라운드 진행 중이
    /// 아니면 false — 요청자가 나중에 다시 시도한다. 서버(또는 오프라인) 전용.
    /// </summary>
    public bool TryRingExternal(Action<ulong> onAnswered)
    {
        if (!IsAuthority || onAnswered == null) return false;
        if (m_isRinging) return false;
        if (Round == null || Round.Phase != RoundPhase.InProgress) return false;

        m_externalAnswered = onAnswered;
        SetRinging(true);
        m_ringEndTime = Time.time + m_ringDuration;
        return true;
    }

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

    // 조준 안내 (#664)
    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.TipCall;

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
            // 조용히 return하면 단서 없이 전화가 영영 안 온다 — 1회만 경고
            if (!m_warnedNoRound)
            {
                m_warnedNoRound = true;
                Debug.LogWarning("TipCallPhone: RoundManager를 찾지 못해 수신 타이머가 돌지 않는다", this);
            }
            return;
        }

        RoundPhase phase = Round.Phase;
        if (phase != m_lastPhase)
        {
            HandlePhaseChanged(phase);
            m_lastPhase = phase;
        }

        // 스케줄 게이트보다 먼저 본다 — 외부 요청 벨(#485)은 m_scheduling이 꺼진 뒤에도 울릴 수 있다
        if (m_isRinging)
        {
            if (Time.time >= m_ringEndTime)
                HandleRingTimeout();
            return;
        }

        if (!m_scheduling)
            return;

        if (Time.time < m_nextRingTime)
            return;

        if (m_remainingCalls <= 0)
        {
            m_scheduling = false;
            Debug.Log("[제보 전화] 이번 라운드 수신 횟수를 모두 소진해 더 이상 전화가 오지 않는다");
            return;
        }

        // 예비 풀이 비면 멈춘다 — 받아도 아무 일 없는 전화는 플레이어를 속이는 셈이다 (#102 설계 결정 5)
        if (Assigner == null || !Assigner.HasPendingSuspect)
        {
            m_scheduling = false;
            Debug.Log("[제보 전화] 예비 용의자가 모두 공개되어 더 이상 전화가 오지 않는다");
            return;
        }

        // 놓친 전화도 소모되어야 하므로 받은 시점이 아니라 여기서 깎는다
        m_remainingCalls--;
        SetRinging(true);
        m_ringEndTime = Time.time + m_ringDuration;
        Debug.Log($"[제보 전화] 수신 — {m_ringDuration:0}초 안에 받아야 한다 (남은 수신 {m_remainingCalls}회)");
    }

    // 외부 요청 벨은 놓친 것으로 처리(요청자에게 알리지 않음, 횟수 미소모). 일반 제보 전화는
    // 아무도 안 받아도 여기서 자동으로 받힌다 — 상호작용은 이 시간을 앞당길 뿐이다 (#663).
    private void HandleRingTimeout()
    {
        bool wasExternal = m_externalAnswered != null;

        if (wasExternal)
        {
            m_externalAnswered = null;
            SetRinging(false);
            DelayNextRing();
            Debug.Log("[전화] 외부 요청 벨을 받지 않아 끊겼다 — 제보 전화 횟수는 소모되지 않았다");
            return;
        }

        Answer(NetworkManager.ServerClientId);
        Debug.Log("[제보 전화] 자동 응답 — 수배 목록이 갱신됐다");
    }

    private void HandlePhaseChanged(RoundPhase phase)
    {
        if (phase == RoundPhase.InProgress)
        {
            m_scheduling = true;
            // 인스펙터 오설정(상한 < 하한)으로 전화가 0회가 되지 않게 하한을 보장한다
            int maxCalls = Mathf.Max(m_minCallCount, m_maxCallCount);
            m_remainingCalls = UnityEngine.Random.Range(m_minCallCount, maxCalls + 1);
            ScheduleNext(m_startDelay);
            Debug.Log($"[제보 전화] 이번 라운드 수신 횟수: {m_remainingCalls}회");
        }
        else
        {
            m_scheduling = false;
            m_externalAnswered = null; // 다음 라운드로 새지 않게 걸려 있던 외부 용무도 버린다
            SetRinging(false);
        }
    }

    // 전화를 받았다(자동 또는 상호작용) — 서버(또는 오프라인)에서만 돈다
    private void Answer(ulong answeredBy)
    {
        SetRinging(false);

        Action<ulong> external = m_externalAnswered;
        m_externalAnswered = null;
        if (external != null)
        {
            external(answeredBy);
            DelayNextRing();
            return;
        }

        if (Assigner == null)
        {
            Debug.LogWarning("TipCallPhone: CriminalAssigner를 찾지 못해 수배를 공개할 수 없다", this);
        }
        else if (Assigner.PromoteNext())
        {
            // 라운드 목표는 검거 수가 아니라 금액이라(#395) 목표는 건드리지 않는다 —
            // 승격된 용의자의 현상금은 JailZone.BountyTotal로만 반영된다
        }
        else
        {
            // 승격 가능한 후보가 없을 뿐(연행 중 등) — 풀 소진은 HasPendingSuspect가 따로 판정하므로 재시도만 한다
            Debug.Log("[제보 전화] 받았지만 지금 공개할 수 있는 용의자가 없다 — 다음 전화를 기다린다");
        }

        ScheduleNext();
    }

    private void ScheduleNext(float extraDelay = 0f)
    {
        m_nextRingTime = Time.time + extraDelay + UnityEngine.Random.Range(m_minInterval, m_maxInterval);
        Debug.Log($"[제보 전화] 다음 수신 예약 — {m_nextRingTime - Time.time:0}초 뒤");
    }

    // 외부 요청 벨 종료 직후에만 최소한으로 미룬다 — 두 벨이 붙어 울리는 것만 막을 정도로 짧게
    // (m_minInterval만큼 밀면 라운드 막판 제보 전화가 시간에 걸려 못 울릴 수 있다)
    private void DelayNextRing()
    {
        m_nextRingTime = Mathf.Max(m_nextRingTime, Time.time + m_externalRingGrace);
    }

    private void SetRinging(bool value)
    {
        if (m_isRinging == value)
            return;

        m_isRinging = value;

        // 호스트는 동기화 변수 쓰기가 자기 이벤트 발행으로 이어지지 않아 직접 발행한다
        if (IsSpawned && IsServer)
            m_isRingingSynced.Value = value;

        OnRingingChanged?.Invoke();
    }
}
