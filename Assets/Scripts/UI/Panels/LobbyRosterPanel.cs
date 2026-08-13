using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.UI;

/// <summary>
/// 로비 접속자 목록 패널 (#429) — 상시 노출이라 ESC로 닫히지 않고 스택에도 쌓이지 않는다 (SessionCodePanel 선례).
/// 명부는 세션 상주라 씬에 없다 — App.Game.Roster로 잡는다 (#598).
/// 입퇴장 토스트는 이 패널이 띄우지 않는다 — 로비 밖에서도 떠야 해서 PlayerPresenceToastView로 옮겼다 (#598).
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

    [Header("UI 참조")]
    [SerializeField] private RectTransform m_rowContainer; // 행 부모 (Vertical Layout Group)

    [SerializeField] private LobbyRosterRowView m_rowPrefab;

    [Tooltip("세션이 없을 때(씬 직접 Play) 쓸 정원 표시값")]
    [SerializeField] private int m_fallbackMaxSlots = 6;

    [Header("음성 (#430)")]
    [SerializeField] private TextMeshProUGUI m_voiceStatusText; // 내 음성 연결 상태

    [SerializeField] private TextMeshProUGUI m_radioKeyText; // 무전 키 안내

    [Tooltip("무전 키 안내 — Lobby.Voice.RadioKey ({0}=키 이름)")]
    [SerializeField] private LocalizedString m_radioKeyFormat;

    [Tooltip("카드에 넣을 얼굴을 굽는 무대 (#598). 비워 두면 얼굴 칸 없이 이름만 나온다")]
    [SerializeField] private LobbyPortraitStage m_portraitStage;

    // 음성 상태 문구는 enum 이름에서 키를 만든다 — 상태가 늘면 테이블에 키만 추가하면 되고
    // 인스펙터 배선이나 매핑 에셋을 함께 고칠 일이 없다 (문서 §2 결정 (h)).
    // 그래서 SerializeField가 아니다 — 고를 것이 없으므로 인스펙터에 내보내면 오히려 잘못 만질 여지만 생긴다.
    private const string k_voiceTable = "LobbyTable";
    private const string k_voiceKeyPrefix = "Lobby.Voice.";
    private readonly LocalizedString m_voiceStatus = new LocalizedString();

    // 구독 여부 플래그 — 두 문구는 참조가 고정(readonly/SerializeField)이라 SessionPanel처럼
    // "지금 걸린 LocalizedString"을 들고 있을 필요가 없고, 걸렸는지만 알면 된다.
    private bool m_voiceStatusBound;
    private bool m_radioKeyBound;

    private readonly List<LobbyRosterRowView> m_rows = new List<LobbyRosterRowView>();

    // 접속자 명부. 세션 상주라 씬에 없다 — App 경유로 잡는다 (#598). 로비에 닿을 때는 보통 이미
    // 스폰돼 있지만, 원격 클라는 스폰 동기화가 늦게 도착할 수 있어 잡힐 때까지 기다린다.
    private SessionRoster m_roster;

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

        TryBindRoster();

        if (Vivox != null)
        {
            Vivox.OnSpeakingChanged += HandleSpeakingChanged;
            Vivox.OnVoiceStateChanged += HandleVoiceStateChanged;
        }

        // 구독 전에 이미 정해진 상태를 한 번 반영한다 — 로비에 닿을 때는 보통 연결이 이미 끝나 있다
        RefreshVoiceStatus();
        RefreshRadioKey();

        // 명부 스폰이 패널보다 빨랐으면 OnListReady를 놓쳤으므로 지금 그린다.
        // 아직 명부를 못 잡았어도 빈 슬롯은 그려 둔다 — 정원이 먼저 보이는 게 낫다.
        Rebuild();
    }

    // 초상은 LobbyPortraitStage가 Awake에서 굽는다. 실행 순서로 앞세워 뒀지만(EExecutionOrder.UIContent)
    // 그것 하나에 기대면 순서가 어긋나는 날 얼굴 없는 카드가 조용히 굳는다 — 혼자 있으면 명단이
    // 바뀌지 않아 다시 그릴 계기가 없기 때문이다. 모든 Awake가 끝난 뒤 한 번 더 그린다.
    private void Start() => Rebuild();

    // 아직 못 잡았을 때만 도는 폴링 — 잡는 즉시 이벤트 구동으로 넘어간다 (TeamFundBalanceView와 같은 방식)
    private void Update()
    {
        if (m_roster == null)
            TryBindRoster();
    }

    private void TryBindRoster()
    {
        SessionRoster roster = App.Game.Roster;
        if (roster == null)
            return; // 세션 없이 씬을 직접 Play하는 경우도 여기로 — 빈 슬롯만 그린 채 넘어간다

        m_roster = roster;
        m_roster.Players.OnListChanged += HandleListChanged;
        // NetworkList는 late-join 클라에 초기 내용을 OnListChanged로 알리지 않는다 — 이 이벤트로 최초 1회를 받는다
        m_roster.OnListReady += Rebuild;

        // 폴링으로 늦게 잡았으면 그 사이의 변경을 놓쳤다 — 잡은 김에 현재 상태로 그린다
        Rebuild();
    }

    private void OnDisable()
    {
        // ?. 금지 — 파괴된 Unity 오브젝트 fake null 우회 방지 (HqPanelView 관례)
        if (m_roster != null)
        {
            m_roster.Players.OnListChanged -= HandleListChanged;
            m_roster.OnListReady -= Rebuild;
        }
        m_roster = null;

        if (Vivox != null)
        {
            Vivox.OnSpeakingChanged -= HandleSpeakingChanged;
            Vivox.OnVoiceStateChanged -= HandleVoiceStateChanged;
        }

        // 꺼진 패널이 언어 변경에 반응해 갱신을 돌리지 않게 끊는다 — 다시 켜질 때 OnEnable이 건다
        UnbindVoiceStatus();
        UnbindRadioKey();
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

                // 지금은 무대가 한 장만 굽는다 — 외형이 개인별로 갈리면(#432) 여기서 사람별 텍스처가 나간다
                m_rows[i].SetPortrait(m_portraitStage != null ? m_portraitStage.Portrait : null);
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

    // 상태별 문구의 주인은 이제 LobbyTable이다 — VivoxManager.ToLabel은 디버그 GUI 전용으로 남는다 (#497).
    // 표시까지가 이 이슈의 범위다(재시도 버튼은 별건) — 지금은 로그인이 실패해도 알 방법이 없다. (#430)
    private void RefreshVoiceStatus()
    {
        if (m_voiceStatusText == null)
            return;

        if (Vivox == null)
        {
            UnbindVoiceStatus();
            m_voiceStatusText.text = string.Empty;
            return;
        }

        // 끊고 다시 거는 이유는 키가 바뀌기 때문이다 — 구독을 유지한 채 참조만 갈아끼우는 것보다
        // 재구독이 확실하고(구독 시점에 1회 발화한다) 상태 변화는 드물어 비용도 없다.
        UnbindVoiceStatus();

        m_voiceStatus.TableReference = k_voiceTable;
        m_voiceStatus.TableEntryReference = k_voiceKeyPrefix + Vivox.VoiceState;
        m_voiceStatus.StringChanged += HandleVoiceStatusChanged;
        m_voiceStatusBound = true;
    }

    private void HandleVoiceStatusChanged(string localized)
    {
        if (m_voiceStatusText != null)
            m_voiceStatusText.text = localized;
    }

    private void UnbindVoiceStatus()
    {
        if (!m_voiceStatusBound)
            return;

        m_voiceStatus.StringChanged -= HandleVoiceStatusChanged;
        m_voiceStatusBound = false;
    }

    // 켜질 때 한 번만 세운다 — 키 리바인딩 경로가 없어 도중에 바뀌지 않는다 (settings-ui.md Phase 2).
    // 다만 언어는 도중에 바뀌므로 대입이 아니라 구독이다 (#374).
    private void RefreshRadioKey()
    {
        if (m_radioKeyText == null)
            return;

        if (Vivox == null)
        {
            UnbindRadioKey();
            m_radioKeyText.text = string.Empty;
            return;
        }

        if (m_radioKeyFormat == null || m_radioKeyFormat.IsEmpty)
        {
            Debug.LogWarning("LobbyRosterPanel: 무전 키 안내 문구가 연결되지 않았습니다.", this);
            return;
        }

        UnbindRadioKey();

        // 인자를 먼저 넣어야 구독 시점의 첫 발화부터 키 이름이 들어간 문장이 나온다
        m_radioKeyFormat.Arguments = new object[] { Vivox.PushToTalkBinding };
        m_radioKeyFormat.StringChanged += HandleRadioKeyChanged;
        m_radioKeyBound = true;
    }

    private void HandleRadioKeyChanged(string localized)
    {
        if (m_radioKeyText != null)
            m_radioKeyText.text = localized;
    }

    private void UnbindRadioKey()
    {
        if (!m_radioKeyBound)
            return;

        m_radioKeyFormat.StringChanged -= HandleRadioKeyChanged;
        m_radioKeyBound = false;
    }
}
