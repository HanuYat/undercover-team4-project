using UnityEngine;

/// <summary>
/// UI 패널(창) 베이스. Awake에서 현재 씬의 UI 매니저에 스스로 등록된다.
/// 열고 닫기는 App.UI.Current.OpenPanel&lt;T&gt;() 경유 — SetActive 직접 호출 금지 (아키텍처 규칙).
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.UIPanel)]
public abstract class PanelBase : MonoBehaviour
{
    [Tooltip("비우면 자기 GameObject를 패널 루트로 사용")]
    [SerializeField]
    protected GameObject m_panelRoot;

    // 딤 배경은 패널 루트 바깥(형제)에 있어 m_panelRoot 토글로 함께 꺼지지 않는다 — 그래서 별도 필드다.
    // 여섯 패널이 같은 토글을 각자 들고 있던 것을 여기로 올렸다 (#598 리뷰).
    [Tooltip("패널과 함께 켜고 끄는 딤 배경 (쓰지 않으면 비워 둔다)")]
    [SerializeField]
    protected GameObject m_background;

    public bool IsOpened => m_panelRoot != null && m_panelRoot.activeSelf;

    /// <summary>ESC로 닫을 수 있는가.</summary>
    public abstract bool CanCloseWithESC { get; }

    /// <summary>열릴 때 ESC 스택에 쌓이는가 (창처럼 겹치는 UI만 true, 상시 HUD는 false).</summary>
    public abstract bool IsStackable { get; }

    /// <summary>ESC 스택이 비었을 때 ESC로 여는 씬의 진입 메뉴인가 (일시정지·종료 확인 등). 씬당 하나만.</summary>
    public virtual bool IsEscMenu => false;

    /// <summary>지금 ESC로 이 진입 메뉴를 열어도 되는가 — 다른 모달이 화면을 잡고 있으면 억제한다.</summary>
    public virtual bool CanOpenFromEsc => true;

    /// <summary>씬 시작 시 열린 상태로 시작하는가.</summary>
    protected virtual bool OpenOnAwake => false;

    // 창이 열린 동안 커서를 풀고 이동 입력을 멈춘다 — 네 패널이 같은 모양으로 복사해 쓰던 것을 모았다.
    // CursorLock의 Push/Pop은 카운터라 짝이 맞아야 하고(#352), 이 래치가 그 짝을 보장한다.
    private bool m_blocked;

    /// <summary>입력을 멈출 대상 — 막기를 쓰는 패널만 덮는다. null이면 커서만 푼다.</summary>
    protected virtual PlayerInputHandler BlockTarget => null;

    protected void SetBlocked(bool blocked)
    {
        if (m_blocked == blocked)
            return;

        m_blocked = blocked;

        // 대상 조회보다 먼저 — 플레이어가 도중에 사라져도 Push/Pop 짝은 유지돼야 한다 (#352)
        if (blocked)
            CursorLock.PushUnlock();
        else
            CursorLock.PopUnlock();

        // ?. 금지 — Unity 오브젝트의 ?.는 C# 참조 null만 보고 파괴 판정(fake null)을 우회한다.
        PlayerInputHandler input = BlockTarget;
        if (input != null)
            input.SetSuspended(blocked);
    }

    protected virtual void Awake()
    {
        if (m_panelRoot == null)
            m_panelRoot = gameObject;

        m_panelRoot.SetActive(OpenOnAwake);
        SetBackgroundActive(OpenOnAwake);

        if (App.UI.Current == null)
        {
            Debug.LogError(
                $"[{GetType().Name}] 씬에 UI 매니저가 없어 패널을 등록하지 못했습니다.",
                this
            );
            return;
        }

        App.UI.Current.RegisterPanel(this);
    }

    protected virtual void OnDestroy()
    {
        // 씬 도중 파괴되어도 매니저에 유령 항목이 남지 않게 한다
        if (App.UI.Current != null)
            App.UI.Current.UnregisterPanel(this);
    }

    public virtual void OpenPanel()
    {
        if (IsStackable && !IsOpened && App.UI.Current != null)
            App.UI.Current.PushUIStack(this);

        SetBackgroundActive(true);
        m_panelRoot.SetActive(true);
    }

    public virtual void ClosePanel()
    {
        if (IsStackable && IsOpened && App.UI.Current != null)
            App.UI.Current.PopUIStack(this);

        SetBackgroundActive(false);
        m_panelRoot.SetActive(false);
    }

    private void SetBackgroundActive(bool active)
    {
        if (m_background != null)
            m_background.SetActive(active);
    }
}
