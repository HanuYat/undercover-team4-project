using UnityEngine;

/// <summary>
/// UI 패널(창) 베이스. Awake에서 현재 씬의 UI 매니저에 스스로 등록된다.
/// 열고 닫기는 App.UI.Current.OpenPanel&lt;T&gt;() 경유 — SetActive 직접 호출 금지 (아키텍처 규칙).
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.UIPanel)]
public abstract class PanelBase : MonoBehaviour
{
    [Tooltip("비우면 자기 GameObject를 패널 루트로 사용")]
    [SerializeField] protected GameObject m_panelRoot;

    public bool IsOpened => m_panelRoot != null && m_panelRoot.activeSelf;

    /// <summary>ESC로 닫을 수 있는가.</summary>
    public abstract bool CanCloseWithESC { get; }

    /// <summary>열릴 때 ESC 스택에 쌓이는가 (창처럼 겹치는 UI만 true, 상시 HUD는 false).</summary>
    public abstract bool IsStackable { get; }

    /// <summary>씬 시작 시 열린 상태로 시작하는가.</summary>
    protected virtual bool OpenOnAwake => false;

    protected virtual void Awake()
    {
        if (m_panelRoot == null)
            m_panelRoot = gameObject;

        m_panelRoot.SetActive(OpenOnAwake);

        if (App.UI.Current == null)
        {
            Debug.LogError($"[{GetType().Name}] 씬에 UI 매니저가 없어 패널을 등록하지 못했습니다.", this);
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

        m_panelRoot.SetActive(true);
    }

    public virtual void ClosePanel()
    {
        if (IsStackable && IsOpened && App.UI.Current != null)
            App.UI.Current.PopUIStack(this);

        m_panelRoot.SetActive(false);
    }
}
