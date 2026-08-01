using System;
using System.Collections.Generic;

public class NpcStateMachine
{
    private readonly Dictionary<NpcState, NpcStateBase> m_states = new Dictionary<NpcState, NpcStateBase>();
    private NpcStateBase m_currentState;

    public NpcState CurrentState { get; private set; }

    // 상태 전이 훅 — 이후 애니메이션·Netcode 동기화(NetworkVariable)를 여기에 연결한다
    public event Action<NpcState> OnStateChanged;

    public void AddState(NpcState state, NpcStateBase stateInstance)
    {
        m_states[state] = stateInstance;
    }

    public void ChangeState(NpcState state)
    {
        if (m_currentState != null && CurrentState == state)
            return;

        m_currentState?.Exit();
        CurrentState = state;
        m_currentState = m_states[state];
        m_currentState.Enter();
        OnStateChanged?.Invoke(state);
    }

    public void Tick()
    {
        m_currentState?.Tick();
    }
}
