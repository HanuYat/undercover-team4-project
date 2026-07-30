using System;
using UnityEngine;

public partial class NpcController
{
    // ---- 침입 (#231) ----

    /// <summary>
    /// 침입 시작 — 돌발 이벤트가 스폰한 침입자를 target(유치장 자물쇠)까지 걸어가게 한다. (GDD 6-4)
    /// 도달하면 unlockSeconds 동안 그 자리에서 해제 채널링을 하고, 다 채워야 <see cref="OnIntrudeFinished"/>가
    /// reached=true로 통보된다 — 그 사이가 플레이어의 대응 구간이다.
    /// </summary>
    public void StartIntrude(Transform target, float unlockSeconds)
    {
        // FSM 전이는 서버 권위 — StartEscort와 동일하게 클라이언트 호출은 무시한다
        if (IsSpawned && !IsServer)
            return;

        IntrudeTarget = target;
        IntrudeUnlockSeconds = unlockSeconds;
        m_stateMachine.ChangeState(NpcState.Intruding);
    }

    /// <summary>침입 이동 종료 통보 — NpcIntrudeState 전용.</summary>
    public void NotifyIntrudeFinished(bool reached) => OnIntrudeFinished?.Invoke(this, reached);

    /// <summary>자물쇠 해제 착수 통보 — NpcIntrudeState 전용.</summary>
    public void NotifyIntrudeUnlockStarted() => OnIntrudeUnlockStarted?.Invoke(this);

    /// <summary>
    /// 수갑 해제 — 오검거로 판정된 무고한 시민을 풀어준다. 배회(Idle)로 복귀한다. (GDD 7-2/7-3, #228)
    /// 체포(Captured) 상태에서만 유효 — 판정 직후 ArrestJudge가 연행을 풀어 Captured로 만들어 둔 상태를 이어받는다.
    /// 오검거 카운트·페널티는 여기서 다루지 않는다 (ArrestJudge.OnArrestJudged를 구독하는 #101 담당).
    /// </summary>
    public void ReleaseFromCustody()
    {
        if (IsSpawned && !IsServer)
            return;
        if (m_stateMachine.CurrentState != NpcState.Captured)
            return;

        JailCell = null;
        m_stateMachine.ChangeState(NpcState.Idle);
    }

}
