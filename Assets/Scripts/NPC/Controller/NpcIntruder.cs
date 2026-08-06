using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 침입 도메인 부품 — 돌발 이벤트가 스폰한 침입자를 유치장 자물쇠까지 보내고, 해제 진행을 밖에 알린다. (GDD 6-4, #231)
/// <see cref="NpcController"/>에서 분리해 나온 첫 부품이다 (#503).
///
/// 판정과 이동은 <see cref="NpcIntrudeState"/>가 하고, 이 부품은 그 상태가 읽을 목표·시간을 들고
/// 결과를 이벤트로 중계한다. FSM 전이가 필요하므로 코어의 <see cref="NpcController.StateMachine"/>을 쓴다.
///
/// <b>반드시 <see cref="NpcController"/>와 같은 GameObject에 둔다</b> — 코어 쪽 [RequireComponent]가 이를 보장한다.
/// 반대 방향으로도 걸면 순환 의존이 되어 둘 중 하나만 떼는 것이 막히므로, 선언은 코어에만 둔다.
/// </summary>
public class NpcIntruder : NetworkBehaviour
{
    private NpcController m_owner;

    /// <summary>침입 중 걸어갈 목표 지점(유치장 자물쇠). 침입 중이 아니면 null. 서버에서만 유효. (#231)</summary>
    public Transform IntrudeTarget { get; private set; }

    /// <summary>자물쇠에 도달해 해제를 시작하기까지 걸리는 시간(초). 탈출 이벤트가 StartIntrude로 넘겨준다. (#231)</summary>
    public float IntrudeUnlockSeconds { get; private set; }

    /// <summary>침입 이동 종료 — reached=true 도달, false 경로 실패. 탈출 이벤트가 구독한다. 서버에서만 발생. (#231)
    /// 인자가 부품이 아니라 코어인 것은 구독자(JailbreakEvent)가 NPC를 NpcController로 들고 대조하기 때문이다.</summary>
    public event Action<NpcController, bool> OnIntrudeFinished;

    /// <summary>
    /// 자물쇠 해제 착수 — 목표에 도달해 해제 채널링을 시작한 순간. 탈출 이벤트가 구독해 본부 경보를 울린다.
    /// 도달과 해제 완료(<see cref="OnIntrudeFinished"/>) 사이의 대응 구간을 여는 신호다. 서버에서만 발생. (#231)
    /// </summary>
    public event Action<NpcController> OnIntrudeUnlockStarted;

    private void Awake()
    {
        m_owner = GetComponent<NpcController>();
    }

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
        m_owner.StateMachine.ChangeState(NpcState.Intruding);
    }

    /// <summary>침입 이동 종료 통보 — NpcIntrudeState 전용.</summary>
    public void NotifyIntrudeFinished(bool reached) => OnIntrudeFinished?.Invoke(m_owner, reached);

    /// <summary>자물쇠 해제 착수 통보 — NpcIntrudeState 전용.</summary>
    public void NotifyIntrudeUnlockStarted() => OnIntrudeUnlockStarted?.Invoke(m_owner);
}
