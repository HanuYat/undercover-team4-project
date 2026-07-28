using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// ESC 일시정지 패널 (#326) — 인게임 어느 씬에서든 ESC로 열어 메인 복귀(세션 이탈)를 한다.
/// 씬의 ESC 진입 메뉴(IsEscMenu)라, 스택이 비었을 때 ESC로 열리고(UIManagerBase) 다시 ESC로 닫힌다.
///
/// 열려 있는 동안 로컬 플레이어 입력을 정지(PlayerInputHandler.SetSuspended)해 패널 뒤 월드로 이동·시점이
/// 새지 않게 하고, 버튼을 누를 수 있게 커서를 푼다(CursorLock.PushUnlock — 닫을 때 Pop).
/// (SettlementPanel과 같은 정지 패턴 — 라운드 종료 freeze는 건드리지 않는다는 점만 다르다.
///  ponytail: 세 번째 사용처가 생기면 공용 헬퍼로 뽑는다. 지금은 정산/일시정지 둘이라 각자 둔다.)
/// </summary>
public class PausePanel : PanelBase
{
    [Header("버튼")]
    [SerializeField]
    private Button m_resumeButton; // 계속하기 — 패널을 닫는다

    [SerializeField]
    private Button m_leaveButton; // 메인으로 나가기 — 세션 이탈 후 타이틀 복귀

    [SerializeField]
    private Button m_settingsButton; // 설정 — 설정 창을 이 패널 위로 겹쳐 연다

    [Header("배경 딤 (패널과 함께 켜고 끔)")]
    [SerializeField]
    private GameObject m_background;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;
    public override bool IsEscMenu => true;

    // 다른 모달(명부·폭탄 매뉴얼·신호 해석기)이 ESC를 소유 중이면 그 위로 일시정지가 겹쳐 이중 동작하지
    // 않게 진입을 거부한다. 모달은 열린 동안 EscMenuGuard를 찍고, IMGUI↔Input System 프레임 스큐까지 덮는다. (충돌 gate, #326)
    public override bool CanOpenFromEsc => !EscMenuGuard.IsBlocked;

    // 로컬 플레이어 입력을 정지 중인지 — 자동복귀(OnDestroy) 시 대칭 복구 + 커서 Push/Pop 1:1 판단용.
    private bool m_playerBlocked;

    protected override void Awake()
    {
        base.Awake();
        if (m_background != null)
            m_background.SetActive(false);
        if (m_resumeButton != null)
            m_resumeButton.onClick.AddListener(ClosePanel);
        if (m_leaveButton != null)
            m_leaveButton.onClick.AddListener(HandleLeave);
        if (m_settingsButton != null)
            m_settingsButton.onClick.AddListener(OpenSettings);
    }

    protected override void OnDestroy()
    {
        if (m_playerBlocked)
            SetLocalPlayerBlocked(false);
        if (m_resumeButton != null)
            m_resumeButton.onClick.RemoveListener(ClosePanel);
        if (m_leaveButton != null)
            m_leaveButton.onClick.RemoveListener(HandleLeave);
        if (m_settingsButton != null)
            m_settingsButton.onClick.RemoveListener(OpenSettings);
        base.OnDestroy();
    }

    public override void OpenPanel()
    {
        if (m_background != null)
            m_background.SetActive(true);
        base.OpenPanel();
        SetLocalPlayerBlocked(true);
    }

    public override void ClosePanel()
    {
        if (m_background != null)
            m_background.SetActive(false);
        SetLocalPlayerBlocked(false);
        base.ClosePanel();
    }

    // 설정 창은 ESC 스택 위로 쌓인다 — 일시정지는 열린 채 남고, ESC 한 번이면 설정만 닫혀 여기로 돌아온다.
    // 커서·입력 정지는 이 패널이 이미 잡고 있으므로(CursorLock Push 상태) 설정 창은 아무것도 건드리지 않는다.
    private static void OpenSettings() => App.UI.Current?.OpenPanel<SettingsPanel>();

    private void HandleLeave()
    {
        // 씬이 곧 타이틀로 넘어간다 — 연타·중복 요청을 막기 위해 버튼을 잠근다 (SessionFlow도 s_busy로 이중 방어).
        if (m_leaveButton != null)
            m_leaveButton.interactable = false;
        SessionFlow.LeaveToMainAsync().Forget();
    }

    // 로컬 플레이어(오너)의 입력 정지 + 커서 해제를 함께 처리한다. 씬에 플레이어가 없으면(로비 등 UI 씬)
    // 입력 정지는 no-op이지만 커서 해제는 그대로 건다 — 버튼을 눌러야 하는 건 플레이어 유무와 무관하다.
    private void SetLocalPlayerBlocked(bool blocked)
    {
        // 같은 값으로 두 번 불려도(PanelBase.OpenPanel엔 재진입 가드가 없다) 커서 Push/Pop이 어긋나지 않게 한다 (#352)
        if (m_playerBlocked == blocked)
            return;

        m_playerBlocked = blocked;

        // 플레이어 조회보다 먼저 — 플레이어가 도중에 사라져도 Push/Pop 짝은 유지돼야 한다 (#352)
        if (blocked)
            CursorLock.PushUnlock();
        else
            CursorLock.PopUnlock();

        GameObject player = LocalPlayer;
        if (player == null)
            return;

        PlayerInputHandler input = player.GetComponent<PlayerInputHandler>();
        if (input != null)
            input.SetSuspended(blocked);
    }

    private static GameObject LocalPlayer
    {
        get
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || nm.LocalClient == null || nm.LocalClient.PlayerObject == null)
                return null;
            return nm.LocalClient.PlayerObject.gameObject;
        }
    }
}
