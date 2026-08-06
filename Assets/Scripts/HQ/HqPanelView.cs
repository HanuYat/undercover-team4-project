using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 본부 열람 패널 공통 뼈대 — 설치물 상호작용(E)으로 열고 Esc로 닫는 로컬 UI. (#222/#223)
/// 여는 동안 게임플레이 입력을 정지하고 커서를 푼다. 파생 클래스는 데이터 구독·그리기만 맡는다.
/// </summary>
public abstract class HqPanelView : MonoBehaviour
{
    [Header("루트")]
    [SerializeField]
    protected GameObject m_root; // 켜고 끌 패널 루트 (기본 비활성)

    private PlayerInputHandler m_input;
    private bool m_isOpen;

    protected virtual void Awake()
    {
        if (m_root != null)
            m_root.SetActive(false);
    }

    public void Open(GameObject interactor)
    {
        if (m_isOpen || interactor == null || m_root == null)
            return;

        m_input = interactor.GetComponent<PlayerInputHandler>();

        m_isOpen = true;
        m_root.SetActive(true);
        m_input?.SetSuspended(true);
        CursorLock.PushUnlock();

        OnOpened();
    }

    public void Close()
    {
        if (!m_isOpen)
            return;

        m_isOpen = false;
        if (m_root != null)
            m_root.SetActive(false);

        OnClosed();

        // ?. 금지 — 파괴된 Unity 오브젝트 fake null 우회 방지 (CitizenDirectoryView와 같은 관례)
        if (m_input != null)
            m_input.SetSuspended(false);
        CursorLock.PopUnlock();

        m_input = null;
    }

    /// <summary>열린 직후 — 데이터 구독·최초 그리기.</summary>
    protected abstract void OnOpened();

    /// <summary>닫히는 중 — 구독 해제.</summary>
    protected virtual void OnClosed() { }

    protected virtual void OnDisable() => Close();

    protected virtual void Update()
    {
        if (!m_isOpen)
            return;

        // 열려 있는 동안 ESC 진입 메뉴(일시정지) 오픈을 막는다 — 이중 동작 방지 (#326)
        EscMenuGuard.BlockThisFrame();

        if (m_input == null) // 연 플레이어 디스폰 시 입력 잠김 방지
        {
            Close();
            return;
        }
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            Close();
    }
}
