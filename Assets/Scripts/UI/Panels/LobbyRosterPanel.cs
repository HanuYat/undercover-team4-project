using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 로비 접속자 목록 패널 (#429) — 상시 노출이라 ESC로 닫히지 않고 스택에도 쌓이지 않는다 (SessionCodePanel 선례).
/// LobbyRoster는 App에 올리지 않고 같은 씬에서 SerializeField로 연결한다 (참조자가 여기 한 곳 — R3).
/// 최대 6인이라 행을 풀링하지 않고 변경 시 전체를 다시 바인딩한다 (WantedListView와 동일).
/// </summary>
public class LobbyRosterPanel : PanelBase
{
    public override bool CanCloseWithESC => false;
    public override bool IsStackable => false;
    protected override bool OpenOnAwake => true;

    [Header("호스트 버튼")]
    [SerializeField] private Button m_startButton;

    [SerializeField] private Button m_leaveButton; // 호스트·클라 공통 — 항상 보인다

    [Header("로스터 — 같은 씬의 LobbyRoster 오브젝트를 연결")]
    [SerializeField] private LobbyRoster m_roster;

    [Header("UI 참조")]
    [SerializeField] private RectTransform m_rowContainer; // 행 부모 (Vertical Layout Group)

    [SerializeField] private LobbyRosterRowView m_rowPrefab;

    [Tooltip("세션이 없을 때(씬 직접 Play) 쓸 정원 표시값")]
    [SerializeField] private int m_fallbackMaxSlots = 6;

    [Header("음성 (#430)")]
    [SerializeField] private TextMeshProUGUI m_voiceStatusText; // 내 음성 연결 상태

    [SerializeField] private TextMeshProUGUI m_radioKeyText; // 무전 키 안내

    private readonly List<LobbyRosterRowView> m_rows = new List<LobbyRosterRowView>();

    private VivoxManager Vivox => App.Net.Vivox;
    private SessionManager Session => App.Net.Session;

    // 정원 — 빈 슬롯을 몇 개 그릴지. 세션이 정본이고 없으면 인스펙터 값으로 떨어진다.
    private int MaxSlots =>
        Session != null && Session.CurrentSession != null
            ? Session.CurrentSession.MaxPlayers
            : m_fallbackMaxSlots;

    private LobbyManager Lobby => App.SceneFlow.Lobby;

    // 시작은 호스트만 — LobbyManager.StartGame도 IsServer로 한 번 더 막는다
    private static bool IsServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

    protected override void Awake()
    {
        base.Awake();
        if (m_startButton != null)
            m_startButton.onClick.AddListener(HandleStart);
        if (m_leaveButton != null)
            m_leaveButton.onClick.AddListener(HandleLeave);
    }

    private void OnEnable()
    {
        // 클라이언트에게는 목록과 세션 코드만 보인다 — 시작 버튼 없음
        if (m_startButton != null)
            m_startButton.gameObject.SetActive(IsServer);

        if (m_roster == null)
        {
            Debug.LogError("LobbyRosterPanel: LobbyRoster가 연결되지 않았습니다.", this);
            return;
        }

        m_roster.Players.OnListChanged += HandleListChanged;
        // NetworkList는 late-join 클라에 초기 내용을 OnListChanged로 알리지 않는다 — 이 이벤트로 최초 1회를 받는다
        m_roster.OnListReady += Rebuild;

        if (Vivox != null)
        {
            Vivox.OnSpeakingChanged += HandleSpeakingChanged;
            Vivox.OnVoiceStateChanged += HandleVoiceStateChanged;
        }

        // 구독 전에 이미 정해진 상태를 한 번 반영한다 — 로비에 닿을 때는 보통 연결이 이미 끝나 있다
        RefreshVoiceStatus();
        RefreshRadioKey();

        // 로스터 스폰이 패널보다 빨랐으면 OnListReady를 놓쳤으므로 지금 그린다.
        // 아직 스폰 전이어도 빈 슬롯은 그려 둔다 — 정원이 먼저 보이는 게 낫다.
        Rebuild();
    }

    private void OnDisable()
    {
        if (m_roster != null)
        {
            m_roster.Players.OnListChanged -= HandleListChanged;
            m_roster.OnListReady -= Rebuild;
        }

        if (Vivox != null)
        {
            Vivox.OnSpeakingChanged -= HandleSpeakingChanged;
            Vivox.OnVoiceStateChanged -= HandleVoiceStateChanged;
        }
    }

    protected override void OnDestroy()
    {
        if (m_startButton != null)
            m_startButton.onClick.RemoveListener(HandleStart);
        if (m_leaveButton != null)
            m_leaveButton.onClick.RemoveListener(HandleLeave);
        base.OnDestroy();
    }

    private void HandleStart()
    {
        // 씬이 곧 상점으로 넘어간다 — 연타를 끊는다 (LobbyManager의 m_started가 이중 방어)
        m_startButton.interactable = false;
        Lobby?.StartGame();
    }

    // 확인창을 띄우기만 한다 — 실제 이탈은 LeaveConfirmPanel이 SessionFlow에 넘긴다
    private static void HandleLeave() => App.UI.Current?.OpenPanel<LeaveConfirmPanel>();

    private void HandleListChanged(NetworkListEvent<LobbyPlayerEntry> _) => Rebuild();

    private void Rebuild()
    {
        if (m_rowPrefab == null || m_rowContainer == null)
        {
            Debug.LogWarning("LobbyRosterPanel: 행 프리팹/컨테이너가 지정되지 않았습니다.", this);
            return;
        }

        // 스폰 전에는 NetworkList를 건드리지 않고 빈 목록으로 취급한다
        int playerCount = m_roster != null && m_roster.IsSpawned ? m_roster.Players.Count : 0;
        int slots = Mathf.Max(playerCount, MaxSlots);

        // 행 수만 정원에 맞추고 재사용한다 (매번 전부 파괴/생성하지 않는다)
        while (m_rows.Count < slots)
            m_rows.Add(Instantiate(m_rowPrefab, m_rowContainer));

        while (m_rows.Count > slots)
        {
            int last = m_rows.Count - 1;
            if (m_rows[last] != null)
                Destroy(m_rows[last].gameObject);
            m_rows.RemoveAt(last);
        }

        for (int i = 0; i < slots; i++)
        {
            if (i < playerCount)
            {
                LobbyPlayerEntry entry = m_roster.Players[i];
                m_rows[i].Bind(entry, m_roster.IsHostEntry(entry));
            }
            else
            {
                m_rows[i].BindEmpty();
            }
        }

        // 행을 새로 바인딩했으니 현재 발화 상태를 다시 얹는다 (재빌드로 아이콘이 꺼진 채 남지 않게)
        RefreshSpeaking();
    }

    private void HandleSpeakingChanged(string playerId, bool speaking)
    {
        if (string.IsNullOrEmpty(playerId))
            return;

        foreach (LobbyRosterRowView row in m_rows)
        {
            if (row != null && row.PlayerId == playerId)
                row.SetSpeaking(speaking);
        }
    }

    private void RefreshSpeaking()
    {
        if (Vivox == null)
            return;

        foreach (LobbyRosterRowView row in m_rows)
        {
            if (row != null && !string.IsNullOrEmpty(row.PlayerId))
                row.SetSpeaking(Vivox.IsSpeaking(row.PlayerId));
        }
    }

    private void HandleVoiceStateChanged(EVoiceState _) => RefreshVoiceStatus();

    // 상태별 문구는 VivoxManager.ToLabel이 소유한다 — 같은 문장을 UI마다 다시 쓰지 않는다.
    // 표시까지가 이 이슈의 범위다(재시도 버튼은 별건) — 지금은 로그인이 실패해도 알 방법이 없다. (#430)
    private void RefreshVoiceStatus()
    {
        if (m_voiceStatusText == null)
            return;

        m_voiceStatusText.text = Vivox != null ? VivoxManager.ToLabel(Vivox.VoiceState) : string.Empty;
    }

    // 켜질 때 한 번만 세운다 — 키 리바인딩 경로가 없어 도중에 바뀌지 않는다 (settings-ui.md Phase 2)
    private void RefreshRadioKey()
    {
        if (m_radioKeyText == null)
            return;

        m_radioKeyText.text = Vivox != null ? $"무전: [{Vivox.PushToTalkBinding}]" : string.Empty;
    }
}
