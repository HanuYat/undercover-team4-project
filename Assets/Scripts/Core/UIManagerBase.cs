using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 씬당 하나의 UI 매니저 베이스. 패널을 타입으로 관리하고 ESC 스택을 처리한다.
/// 패널은 PanelBase.Awake에서 스스로 등록된다 — 실행 순서(UIManagement < UIPanel)가 이를 보장.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.UIManagement)]
public abstract class UIManagerBase : CommonManagerBase
{
    private readonly Dictionary<Type, PanelBase> m_panels = new();
    private readonly Stack<PanelBase> m_escStack = new();

    // 스택이 비었을 때 ESC로 여는 씬의 진입 메뉴(일시정지·종료 확인). 씬당 하나 — 패널이 스스로 등록.
    private PanelBase m_escMenuPanel;

    protected virtual void Update()
    {
        if (Keyboard.current == null || !Keyboard.current.escapeKey.wasPressedThisFrame)
            return;

        // 창이 쌓여 있으면 ESC는 그 창만 처리한다 — 닫든(닫기 가능) 말든 진입 메뉴로는 새지 않는다.
        if (m_escStack.TryPeek(out PanelBase top) && top != null)
        {
            if (top.CanCloseWithESC)
                top.ClosePanel();
            return;
        }

        // 스택이 비었을 때만 진입 메뉴를 연다. 다른 모달(명부·폭탄 매뉴얼 등)이 화면을 잡고 있으면
        // 그 패널이 CanOpenFromEsc로 거부해 이중 동작을 막는다.
        if (m_escMenuPanel != null && m_escMenuPanel.CanOpenFromEsc)
            m_escMenuPanel.OpenPanel();
    }

    public void RegisterPanel(PanelBase panel)
    {
        if (panel == null)
            return;

        if (!m_panels.TryAdd(panel.GetType(), panel))
        {
            Debug.LogError($"[{GetType().Name}] 패널 중복 등록: {panel.GetType().Name}", panel);
            return;
        }

        if (!panel.IsEscMenu)
            return;

        if (m_escMenuPanel != null)
            Debug.LogError(
                $"[{GetType().Name}] ESC 진입 메뉴가 이미 있음: {m_escMenuPanel.GetType().Name} — {panel.GetType().Name} 무시",
                panel
            );
        else
            m_escMenuPanel = panel;
    }

    public void UnregisterPanel(PanelBase panel)
    {
        if (panel == null)
            return;

        // 내가 등록한 그 인스턴스일 때만 제거 (ManagerHandler와 같은 방침)
        if (
            m_panels.TryGetValue(panel.GetType(), out PanelBase current)
            && ReferenceEquals(current, panel)
        )
            m_panels.Remove(panel.GetType());

        if (ReferenceEquals(m_escMenuPanel, panel))
            m_escMenuPanel = null;
    }

    public void PushUIStack(PanelBase panel)
    {
        if (panel != null)
            m_escStack.Push(panel);
    }

    public void PopUIStack(PanelBase panel)
    {
        // 최상단일 때만 pop — 중간 패널을 코드로 닫아도 스택이 꼬이지 않는다 (템플릿 버그 수정)
        if (m_escStack.TryPeek(out PanelBase top) && ReferenceEquals(top, panel))
            m_escStack.Pop();
    }

    public bool TryGetPanel<T>(out T panel)
        where T : PanelBase
    {
        if (m_panels.TryGetValue(typeof(T), out PanelBase value) && value is T typed)
        {
            panel = typed;
            return true;
        }

        panel = null;
        return false;
    }

    public T GetPanel<T>()
        where T : PanelBase
    {
        if (TryGetPanel(out T panel))
            return panel;

        throw new InvalidOperationException(
            $"[{GetType().Name}] 등록되지 않은 패널: {typeof(T).Name}"
        );
    }

    public bool OpenPanel<T>()
        where T : PanelBase
    {
        if (!TryGetPanel(out T panel))
        {
            // 호출부는 모두 반환값을 버린다 — 씬에 패널을 두는 걸 잊으면 버튼이 조용히 죽어 원인을 찾기 어렵다.
            // 배치 누락(또는 프리팹에 스크립트 미부착)을 콘솔에서 바로 드러낸다 (#441).
            Debug.LogError(
                $"[{GetType().Name}] 등록되지 않은 패널을 열려 했습니다: {typeof(T).Name} — 씬에 배치됐는지 확인하세요."
            );
            return false;
        }

        panel.OpenPanel();
        return true;
    }

    public bool ClosePanel<T>()
        where T : PanelBase
    {
        if (!TryGetPanel(out T panel))
            return false;

        panel.ClosePanel();
        return true;
    }
}
