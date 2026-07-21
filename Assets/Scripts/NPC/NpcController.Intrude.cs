using System;
using UnityEngine;

/// <summary>
/// NpcController의 침입 파트 — 범인 탈출 돌발 이벤트의 침입자 이동 (#231, GDD 6-4).
/// 스폰된 침입자가 유치장 자물쇠까지 걸어가 해제 채널링을 하는 흐름의 전이·통보를 든다.
/// 이동만 했고 동작 변화는 없다 (도메인 partial 분리).
/// </summary>
public partial class NpcController
{
    /// <summary>침입 중 걸어갈 목표 지점(유치장 자물쇠). 침입 중이 아니면 null. 서버에서만 유효. (#231)</summary>
    public Transform IntrudeTarget { get; private set; }

    /// <summary>자물쇠에 도달해 해제를 시작하기까지 걸리는 시간(초). 탈출 이벤트가 StartIntrude로 넘겨준다. (#231)</summary>
    public float IntrudeUnlockSeconds { get; private set; }

    /// <summary>침입 이동 종료 — reached=true 도달, false 경로 실패. 탈출 이벤트가 구독한다. 서버에서만 발생. (#231)</summary>
    public event Action<NpcController, bool> OnIntrudeFinished;

    /// <summary>
    /// 자물쇠 해제 착수 — 목표에 도달해 해제 채널링을 시작한 순간. 탈출 이벤트가 구독해 본부 경보를 울린다.
    /// 도달과 해제 완료(<see cref="OnIntrudeFinished"/>) 사이의 대응 구간을 여는 신호다. 서버에서만 발생. (#231)
    /// </summary>
    public event Action<NpcController> OnIntrudeUnlockStarted;

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
}
