using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 돌발 이벤트 프레임워크 — <see cref="ISuddenEvent"/>를 서버 권위로 발생·틱·정리시킨다. (GDD 6-4/7-4, #106)
/// 주기 추첨은 라운드 진행 중에만 돌고, 라운드 날씨(<see cref="IRoundWeather"/>)만 준비 단계에 따로 뽑는다 (#700).
/// 서버(또는 오프라인) 전용 — <see cref="RoundManager.Phase"/>를 서버만 굴리므로 클라에선 스케줄러가 안 돈다 (#56).
///
/// <b>이 매니저는 어떤 이벤트가 있는지 모른다</b> — "언제 발생시킬지"만 정한다. 효과·상태·전 클라 전파는
/// 구현체가 소유하므로(스폰형은 자기 NetworkObject, 전역형은 자기 NetworkVariable) 이벤트를 늘려도 이 파일은 그대로다.
///
/// 이벤트 풀은 인스펙터 <b>명시 리스트</b>(m_eventEntries)다 — 자동수집을 쓰지 않고 항목마다 enabled 토글이 있다 (#291).
/// ⚠ <b>이벤트 컴포넌트는 반드시 이 오브젝트에 둔다</b> — 전부 [RequireComponent(typeof(SuddenEventManager))]라
/// 다른 오브젝트에 붙이면 거기에 두 번째 매니저가 생긴다. 완화는 팀 결정 대기 (#291 고려사항 · PR #298 리뷰).
/// 수치는 전부 인스펙터 — 밸런싱 보류 항목이라 코드에 못 박지 않는다 (GDD 12장).
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class SuddenEventManager : NetworkedManagerBase
{
    private RoundManager Round => App.Game.Round;
    private RoundProgress Progress => App.Game.RoundProgress;

    [Header("발생 스케줄 (초)")]
    [Tooltip(
        "라운드 시작(또는 직전 이벤트 종료) 후 다음 이벤트까지 대기하는 최소/최대 시간 — 이 사이에서 랜덤"
    )]
    [SerializeField]
    private float m_minInterval = 20f;

    [SerializeField]
    private float m_maxInterval = 45f;

    [Tooltip("라운드 시작 직후 첫 이벤트까지의 추가 유예(초) — 준비 없이 곧바로 터지는 것 방지")]
    [SerializeField]
    private float m_startDelay = 15f;

    [Header("이벤트 발생 on/off")]
    [Tooltip(
        "끄면 스케줄러가 새 이벤트를 발생시키지 않는다 (디버그·튜토리얼용). 이미 진행 중인 이벤트는 계속된다"
    )]
    [SerializeField]
    private bool m_enabled = true;

    [System.Serializable]
    private class SuddenEventEntry
    {
        [Tooltip(
            "ISuddenEvent 또는 ISuddenEventProvider를 구현한 컴포넌트 (예: DeviceBlackoutEvent, RioterEvent, JailbreakEvent)"
        )]
        public MonoBehaviour component;

        [Tooltip("끄면 이 항목은 이벤트 풀에서 제외된다 — 특정 이벤트만 켜서 테스트할 때 쓴다")]
        public bool enabled = true;
    }

    [Header("이벤트 풀 (명시 리스트)")]
    [Tooltip(
        "발생 후보 이벤트를 여기 등록한다. 자동수집은 쓰지 않는다 — 항목의 enabled로 개별 토글 (#291)"
    )]
    [SerializeField]
    private List<SuddenEventEntry> m_eventEntries = new List<SuddenEventEntry>();

    [Header("라운드 날씨 (#700)")]
    [Tooltip(
        "라운드별 맑음 확률·날씨별 가중치 표. 이 맵의 표를 꽂는다 — 안 꽂으면 아래 폴백 확률 하나로 돈다"
    )]
    [SerializeField]
    private RoundWeatherTable m_weatherTable;

    [Tooltip("표를 안 꽂았을 때 쓸 맑음 확률(%) — 표 미장착 씬이 깨지지 않게 하는 값이다")]
    [Range(0, 100)]
    [SerializeField]
    private int m_clearPercentFallback = 50;

    // 명시 리스트에서 구성한 이벤트 풀 (Awake에서 1회)
    private readonly List<ISuddenEvent> m_events = new List<ISuddenEvent>();

    // 발생 후보 임시 버퍼 — 매 추첨마다의 할당을 피한다 (서버/오프라인에서만 쓰므로 공유 안전)
    private readonly List<ISuddenEvent> m_eligibleBuffer = new List<ISuddenEvent>();

    // 날씨 추첨 후보 버퍼 — 위와 갈라 둔다. 라운드당 1회라 겹칠 일은 없지만 담기는 것이 다르다 (#700)
    private readonly List<IRoundWeather> m_weatherBuffer = new List<IRoundWeather>();

    private float m_nextTriggerTime;
    private bool m_scheduling; // 라운드 InProgress 진입 시 켜진다 — Phase 폴링으로 스케줄 시작/정지를 판정
    private RoundPhase m_lastPhase = RoundPhase.Preparing;

    // 현재 페이즈를 한 번이라도 처리했는가 — 씬 진입 시점이 이미 Preparing이라 위 초기값과 같아
    // 전이가 감지되지 않고, 그러면 날씨를 뽑는 자리가 통째로 없어진다 (#700).
    private bool m_phaseSeen;

    /// <summary>이벤트 발생 알림 — 본부/현장 HUD 토스트가 구독할 훅. (표시 이름, 문구 키)</summary>
    public event Action<string, string> OnEventAnnounced;

    // 서버(또는 오프라인)에서만 의미 — 이 피어가 이벤트 권위를 가지는지. 스폰 전(오프라인)이면 항상 권위.
    private bool IsAuthority => !IsSpawned || IsServer;

    protected override void Awake()
    {
        base.Awake(); // App.Game.SuddenEvent 등록

        // 명시 리스트에서 이벤트 풀을 구성한다 — 자동수집(GetComponents) 대신 인스펙터 등록분만 (#291).
        // enabled=false 항목은 풀에서 제외 — 특정 이벤트만 켜서 반복 테스트한다.
        m_events.Clear();
        for (int i = 0; i < m_eventEntries.Count; i++)
        {
            SuddenEventEntry entry = m_eventEntries[i];
            if (!entry.enabled || entry.component == null)
                continue;

            if (entry.component is ISuddenEventProvider provider)
                provider.CollectEvents(m_events); // 제공자형: 1개가 여러 종 (현재 구현체 없음 — 확장 지점)
            else if (entry.component is ISuddenEvent evt)
                m_events.Add(evt); // 컴포넌트형: 1개 = 1종
            else
                Debug.LogWarning(
                    $"SuddenEventManager: '{entry.component.name}'은(는) ISuddenEvent/ISuddenEventProvider가 아니다 — 무시",
                    entry.component
                );
        }
    }

    /// <summary>
    /// 풀에서 T 타입 이벤트를 찾아 준다 — 특정 이벤트의 상태를 밖에서 조회해야 할 때 쓴다
    /// (예: 스캐너가 <see cref="DeviceBlackoutEvent"/>의 먹통 플래그를 본다).
    ///
    /// 이래도 <b>매니저는 여전히 어떤 이벤트가 있는지 모른다</b> — 타입은 부르는 쪽이 정하고
    /// 여기엔 남지 않는다. 그래서 이벤트마다 App에 필드를 하나씩 다는 대신 이 경로를 쓴다 (#372 리뷰, R3).
    ///
    /// 없으면 null — 인스펙터 리스트에 등록하지 않았거나 항목을 꺼 둔 구성이다. 그 이벤트는 발생하지도
    /// 않으므로 조회하는 쪽에서 "그 효과는 없다"로 처리하면 맞는다.
    /// </summary>
    public T GetEvent<T>()
        where T : class, ISuddenEvent => m_events.Find(e => e is T) as T;

    /// <summary>풀에 담긴 이벤트 수 — 개발자 단축키가 인덱스 범위를 알아야 한다 (#609).</summary>
    public int EventCount => m_events.Count;

    /// <summary>
    /// index번 이벤트의 표시 이름 — 범위 밖이면 null. <b>디버그 표시 전용</b>이다.
    /// 이벤트 자체를 넘기지 않는 이유는 GetEvent와 같다 — 밖에서 풀을 헤집게 두지 않는다.
    /// </summary>
    public string EventNameAt(int index) =>
        index >= 0 && index < m_events.Count ? m_events[index].DisplayName : null;

    private void Update()
    {
        // 발생 스케줄·판정은 서버 권위 — 클라이언트에서는 아예 돌지 않는다 (#56)
        if (!IsAuthority)
            return;
        if (Round == null)
            return;

        // 라운드 페이즈 전이 감지 — InProgress 진입 시 스케줄 시작, 이탈 시 진행 이벤트를 정리하고 멈춘다.
        // 첫 프레임은 전이가 없어도 1회 처리한다 — 씬 진입 시점(Preparing)이 날씨를 뽑는 자리다 (#700)
        RoundPhase phase = Round.Phase;
        if (!m_phaseSeen || phase != m_lastPhase)
        {
            m_phaseSeen = true;
            HandlePhaseChanged(phase);
            m_lastPhase = phase;
        }

        if (!m_scheduling)
            return;

        TickActiveEvents();
        TrySchedule();
    }

    private void HandlePhaseChanged(RoundPhase phase)
    {
        if (phase == RoundPhase.InProgress)
        {
            m_scheduling = true;
            ScheduleNext(m_startDelay); // 시작 직후 유예를 두고 첫 이벤트를 잡는다
            return;
        }

        // 라운드가 끝났거나 준비 상태로 되돌아감 — 진행 중이던 이벤트를 강제 정리하고 스케줄을 멈춘다
        m_scheduling = false;
        ResetAllEvents();

        // 준비 단계 진입이 오늘 날씨를 뽑는 자리다 (#700). ⚠ 위 정리 뒤여야 한다 — 앞에 두면
        // 방금 뽑은 날씨를 ResetAllEvents가 곧바로 끈다. 종료(Ended)는 정산 화면이라 뽑지 않는다.
        if (phase == RoundPhase.Preparing)
            TryBeginRoundWeather();
    }

    // 활성 이벤트를 매 프레임 서버에서 진행시킨다 — 각자 지속/자동 해제를 관리한다
    private void TickActiveEvents()
    {
        for (int i = 0; i < m_events.Count; i++)
        {
            if (m_events[i].IsActive)
                m_events[i].ServerTick();
        }
    }

    // 예약 시각에 도달하면 발생 가능한 이벤트 중 하나를 무작위로 골라 시작하고, 다음 예약을 잡는다
    private void TrySchedule()
    {
        if (Time.time < m_nextTriggerTime)
            return;

        if (m_enabled)
            TryTriggerRandom();

        ScheduleNext(0f); // 발생 성공 여부와 무관하게 다음 추첨 시각을 새로 잡는다 (막혔으면 다음 기회에 재시도)
    }

    private void TryTriggerRandom()
    {
        m_eligibleBuffer.Clear();
        for (int i = 0; i < m_events.Count; i++)
        {
            ISuddenEvent evt = m_events[i];

            // 날씨는 주기 추첨을 타지 않는다 — 라운드 시작에 한 번만 뽑힌다 (#700).
            // 틱·정리(TickActiveEvents·ResetAllEvents)는 그대로 태우므로 여기서만 뺀다.
            if (evt is IRoundWeather)
                continue;

            if (!evt.IsActive && evt.CanTrigger())
                m_eligibleBuffer.Add(evt);
        }

        if (m_eligibleBuffer.Count == 0)
            return; // 지금은 발생 가능한 이벤트가 없다 — 다음 예약에서 다시 시도

        ISuddenEvent chosen = m_eligibleBuffer[Random.Range(0, m_eligibleBuffer.Count)];
        chosen.ServerBegin();

        // 이벤트가 내부 사정(스폰 지점 실패 등)으로 발동을 접었을 수 있다 — IsActive로 확인하고 로그를 가른다.
        // 성공으로 단정하고 찍으면 실제로는 아무 일도 없는데 콘솔에만 "발생"이 남아 디버깅이 흔들린다.
        if (!chosen.IsActive)
        {
            Debug.Log($"[돌발이벤트] 발동 불발 — {chosen.DisplayName}");
            return;
        }

        Debug.Log($"[돌발이벤트] 발생 — {chosen.DisplayName}");

        // 조용히 시작하는 이벤트(AnnounceOnBegin=false)는 자기가 원하는 시점에 Announce를 직접 부른다
        if (chosen.AnnounceOnBegin)
            Announce(chosen.DisplayName, chosen.NoticeKey);
    }

    // ---- 라운드 날씨 (#700) ----

    // 오늘의 날씨를 뽑아 시작한다 — 라운드당 1회, 준비 단계. 맑음이면 아무것도 시작하지 않는다.
    // 매니저가 아는 것은 IRoundWeather라는 범주와 확률 표뿐이다 — 날씨가 몇 종인지·무엇을 하는지는
    // 구현체가 자기 Kind로 표를 가리키게 해서 밖에 둔다.
    private void TryBeginRoundWeather()
    {
        if (!m_enabled)
            return;

        m_weatherBuffer.Clear();
        for (int i = 0; i < m_events.Count; i++)
        {
            if (m_events[i] is IRoundWeather weather && !weather.IsActive && weather.CanTrigger())
                m_weatherBuffer.Add(weather);
        }

        if (m_weatherBuffer.Count == 0)
            return; // 이 씬 풀에 날씨가 없다 — 맑은 라운드와 결과가 같다

        // 오프라인 단독 Play에는 상주 진행도가 없다 — 1라운드로 친다 (RoundProgress 주석의 기존 규약)
        int round = Progress != null ? Progress.Current : RoundProgress.k_firstRound;
        int clearPercent =
            m_weatherTable != null
                ? m_weatherTable.GetClearPercent(round, m_clearPercentFallback)
                : m_clearPercentFallback;

        if (Random.Range(0, 100) < clearPercent)
        {
            Debug.Log($"[날씨] {round}라운드 — 맑음 (맑음 확률 {clearPercent}%)");
            return;
        }

        IRoundWeather chosen = PickWeighted();
        if (chosen == null)
        {
            // 후보는 있는데 가중치 합이 0 — 이 맵에서 나올 수 있는 날씨를 전부 막아 둔 구성이다
            Debug.Log($"[날씨] {round}라운드 — 후보 가중치가 전부 0이라 맑음으로 간다");
            return;
        }

        chosen.ServerBegin();

        // 이벤트가 내부 사정으로 발동을 접었을 수 있다 — TryTriggerRandom과 같은 이유로 IsActive를 확인한다
        if (!chosen.IsActive)
        {
            Debug.Log($"[날씨] 발동 불발 — {chosen.DisplayName}");
            return;
        }

        Debug.Log($"[날씨] {round}라운드 — {chosen.DisplayName} (맑음 확률 {clearPercent}%)");

        // 날씨 3종은 전부 AnnounceOnBegin=false(조용히 시작, 2026-08-13)지만 규약대로 따른다
        if (chosen.AnnounceOnBegin)
            Announce(chosen.DisplayName, chosen.NoticeKey);
    }

    // m_weatherBuffer에서 가중치 비례로 하나 뽑는다. 표가 없으면 전부 1(균등)이다.
    // 합이 0이면 null — 뽑을 수 있는 날씨가 없다는 뜻이라 호출부가 맑음으로 처리한다.
    private IRoundWeather PickWeighted()
    {
        float total = 0f;
        for (int i = 0; i < m_weatherBuffer.Count; i++)
            total += WeightOf(m_weatherBuffer[i]);

        if (total <= 0f)
            return null;

        float roll = Random.value * total;
        for (int i = 0; i < m_weatherBuffer.Count; i++)
        {
            roll -= WeightOf(m_weatherBuffer[i]);
            if (roll <= 0f)
                return m_weatherBuffer[i];
        }

        return m_weatherBuffer[m_weatherBuffer.Count - 1]; // 부동소수 오차로 끝까지 샌 경우
    }

    private float WeightOf(IRoundWeather weather) =>
        m_weatherTable != null ? m_weatherTable.WeightOf(weather.Kind) : 1f;

    /// <summary>
    /// 디버그 — 풀의 index번 이벤트를 즉시 발동한다(밸런싱·테스트용). 서버(또는 오프라인)에서만 동작하며
    /// 이미 활성이거나 발생 불가(CanTrigger=false)면 무시한다. (#291)
    /// </summary>
    public void ForceTrigger(int index)
    {
        if (!IsAuthority)
            return;
        if (index < 0 || index >= m_events.Count)
        {
            Debug.LogWarning(
                $"SuddenEventManager.ForceTrigger: 잘못된 index {index} (풀 크기 {m_events.Count})",
                this
            );
            return;
        }

        ISuddenEvent evt = m_events[index];
        if (evt.IsActive || !evt.CanTrigger())
        {
            Debug.Log($"[돌발이벤트] 강제발동 불가 — {evt.DisplayName} (활성이거나 조건 미충족)");
            return;
        }

        // 날씨 강제 발동은 <b>교체</b>다 (#700) — 켜져 있던 날씨를 먼저 걷는다.
        // 안 걷으면 눈 위에 안개를 얹어 한 라운드에 날씨가 둘이 되는데, 그건 이 기능이 없애려던 상태다.
        if (evt is IRoundWeather)
            ResetOtherWeather(evt);

        evt.ServerBegin();
        if (evt.IsActive && evt.AnnounceOnBegin)
            Announce(evt.DisplayName, evt.NoticeKey);
        Debug.Log($"[돌발이벤트] 강제발동 — {evt.DisplayName}");
    }

    // keep 말고 활성인 날씨를 전부 끈다 — 강제 발동 교체 전용.
    private void ResetOtherWeather(ISuddenEvent keep)
    {
        for (int i = 0; i < m_events.Count; i++)
        {
            ISuddenEvent evt = m_events[i];
            if (evt == keep || !(evt is IRoundWeather) || !evt.IsActive)
                continue;

            evt.ServerReset();
            Debug.Log($"[날씨] 강제 교체로 걷힘 — {evt.DisplayName}");
        }
    }

    [ContextMenu("Debug/Force Trigger First Event")]
    private void ForceTriggerFirst() => ForceTrigger(0);

    // 다음 발생까지의 대기 시간을 min~max 사이에서 뽑아 예약한다 (extraDelay는 라운드 시작 유예용)
    private void ScheduleNext(float extraDelay)
    {
        m_nextTriggerTime = Time.time + extraDelay + Random.Range(m_minInterval, m_maxInterval);
    }

    // 각 이벤트가 자기 효과를 스스로 되돌린다 — 매니저는 무엇을 정리해야 하는지 알 필요가 없다.
    private void ResetAllEvents()
    {
        for (int i = 0; i < m_events.Count; i++)
            m_events[i].ServerReset();
    }

    /// <summary>
    /// 이벤트 알림을 전 클라이언트에 발행한다 — HUD 알림용. 네트워크 세션에서만 RPC를 쏜다.
    /// 보통은 발생 시점에 매니저가 부르지만, <see cref="ISuddenEvent.AnnounceOnBegin"/>이 false인 이벤트는
    /// 알릴 시점을 스스로 정해 이 메서드를 직접 부른다. 서버(또는 오프라인) 전용.
    /// </summary>
    public void Announce(string displayName, string noticeKey = null)
    {
        OnEventAnnounced?.Invoke(displayName, noticeKey); // 서버·오프라인 로컬 발행
        if (IsSpawned && IsServer)
            AnnounceEventClientRpc(displayName, noticeKey ?? string.Empty);
    }

    [ClientRpc]
    private void AnnounceEventClientRpc(string displayName, string noticeKey)
    {
        // 서버(호스트)는 위에서 이미 발행했으므로 원격 클라에서만 중계
        if (IsServer)
            return;
        Debug.Log($"[돌발이벤트] 발생 알림 — {displayName}");
        OnEventAnnounced?.Invoke(displayName, string.IsNullOrEmpty(noticeKey) ? null : noticeKey);
    }
}
