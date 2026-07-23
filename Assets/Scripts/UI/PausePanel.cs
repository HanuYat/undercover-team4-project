using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// ESC 일시정지 패널 (#326) — 인게임 어느 씬에서든 ESC로 열어 메인 복귀(세션 이탈)를 한다.
/// 씬의 ESC 진입 메뉴(IsEscMenu)라, 스택이 비었을 때 ESC로 열리고(UIManagerBase) 다시 ESC로 닫힌다.
///
/// 열려 있는 동안 로컬 플레이어 입력을 정지(PlayerInputHandler.SetSuspended)해 패널 뒤 월드로 이동·시점이
/// 새지 않게 하고, 버튼을 누를 수 있게 커서를 띄운다. 닫으면 열기 직전의 커서 상태로 되돌린다.
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

    [Header("배경 딤 (패널과 함께 켜고 끔)")]
    [SerializeField]
    private GameObject m_background;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;
    public override bool IsEscMenu => true;

    // 다른 모달(명부·폭탄 매뉴얼·신호 해석기)이 ESC를 소유 중이면 그 위로 일시정지가 겹쳐 이중 동작하지
    // 않게 진입을 거부한다. 모달은 열린 동안 EscMenuGuard를 찍고, IMGUI↔Input System 프레임 스큐까지 덮는다. (충돌 gate, #326)
    public override bool CanOpenFromEsc => !EscMenuGuard.IsBlocked;

    // 열기 직전의 커서 잠금 상태 — 닫을 때 이 상태로 되돌린다.
    private bool m_cursorUnlockedBeforeOpen;

    // 로컬 플레이어 입력을 정지 중인지 — 자동복귀(OnDestroy) 시 대칭 복구 판단용.
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
    }

    protected override void OnDestroy()
    {
        if (m_playerBlocked)
            SetLocalPlayerBlocked(false);
        if (m_resumeButton != null)
            m_resumeButton.onClick.RemoveListener(ClosePanel);
        if (m_leaveButton != null)
            m_leaveButton.onClick.RemoveListener(HandleLeave);
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

    private void HandleLeave()
    {
        // 씬이 곧 타이틀로 넘어간다 — 연타·중복 요청을 막기 위해 버튼을 잠근다 (SessionFlow도 s_busy로 이중 방어).
        if (m_leaveButton != null)
            m_leaveButton.interactable = false;
        SessionFlow.LeaveToMainAsync().Forget();
    }

    // 로컬 플레이어(오너)의 입력 정지 + 커서 해제를 함께 처리한다. 씬에 플레이어가 없으면(로비 등 UI 씬) no-op.
    private void SetLocalPlayerBlocked(bool blocked)
    {
        m_playerBlocked = blocked;

        GameObject player = LocalPlayer;
        if (player == null)
            return;

        PlayerInputHandler input = player.GetComponent<PlayerInputHandler>();
        if (input != null)
            input.SetSuspended(blocked);

        PlayerMovement movement = player.GetComponent<PlayerMovement>();
        if (movement == null)
            return;

        if (blocked)
        {
            // 열기 직전 커서 상태를 기억해 닫을 때 되돌린다 (SettlementPanel·InventoryBarView 관례)
            m_cursorUnlockedBeforeOpen = Cursor.lockState == CursorLockMode.None;
            movement.SetCursorUnlocked(true);
        }
        else
        {
            movement.SetCursorUnlocked(m_cursorUnlockedBeforeOpen);
        }
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
