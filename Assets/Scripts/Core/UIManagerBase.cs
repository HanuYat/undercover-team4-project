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

    protected virtual void Update()
    {
        if (Keyboard.current == null || !Keyboard.current.escapeKey.wasPressedThisFrame)
            return;

        if (m_escStack.TryPeek(out PanelBase top) && top != null && top.CanCloseWithESC)
            top.ClosePanel();
    }

    public void RegisterPanel(PanelBase panel)
    {
        if (panel == null)
            return;

        if (!m_panels.TryAdd(panel.GetType(), panel))
            Debug.LogError($"[{GetType().Name}] 패널 중복 등록: {panel.GetType().Name}", panel);
    }

    public void UnregisterPanel(PanelBase panel)
    {
        if (panel == null)
            return;

        // 내가 등록한 그 인스턴스일 때만 제거 (ManagerHandler와 같은 방침)
        if (m_panels.TryGetValue(panel.GetType(), out PanelBase current) && ReferenceEquals(current, panel))
            m_panels.Remove(panel.GetType());
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

    public bool TryGetPanel<T>(out T panel) where T : PanelBase
    {
        if (m_panels.TryGetValue(typeof(T), out PanelBase value) && value is T typed)
        {
            panel = typed;
            return true;
        }

        panel = null;
        return false;
    }

    public T GetPanel<T>() where T : PanelBase
    {
        if (TryGetPanel(out T panel))
            return panel;

        throw new InvalidOperationException($"[{GetType().Name}] 등록되지 않은 패널: {typeof(T).Name}");
    }

    public bool OpenPanel<T>() where T : PanelBase
    {
        if (!TryGetPanel(out T panel))
            return false;

        panel.OpenPanel();
        return true;
    }

    public bool ClosePanel<T>() where T : PanelBase
    {
        if (!TryGetPanel(out T panel))
            return false;

        panel.ClosePanel();
        return true;
    }
}
