using System;
using System.Collections.Generic;

public class NpcStateMachine
{
    private readonly Dictionary<NpcState, NpcStateBase> m_states =
        new Dictionary<NpcState, NpcStateBase>();
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

        // 사망은 빠져나가지 않는다 — <b>규약이 아니라 구조로 막는다</b> (#571).
        //
        // 코어 Update의 사망 게이트로는 부족하다: 그건 FSM 자신의 전이만 멈추는데, 밖에서 직접
        // 거는 경로가 여럿이다(오검거·납치 매니저의 NpcPenaltyAgent, 신병 석방 등). 그중 하나라도
        // 시체를 배회·추격으로 되돌리면 에이전트가 꺼진 채 상태만 살아나 그 자리에 굳는다.
        //
        // 조용히 무시하지 않고 알리는 이유: 여기 걸렸다는 것은 그 시스템이 사망 통보
        // (<see cref="NpcDeath.OnDied"/>)를 구독하지 않아 죽은 대상을 아직 붙들고 있다는 뜻이고,
        // 그건 이 한 줄이 막아 준 증상보다 위에 있는 원인이다.
        if (CurrentState == NpcState.Dead)
        {
            UnityEngine.Debug.LogError(
                $"NpcStateMachine: 죽은 NPC를 {state}(으)로 되돌리려 했다 — 무시한다. "
                    + "그 시스템이 NpcDeath.OnDied로 자기 참조를 정리해야 한다"
            );
            return;
        }

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
