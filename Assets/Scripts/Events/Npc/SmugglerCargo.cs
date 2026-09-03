using System;
using UnityEngine;

/// <summary>
/// 밀수 화물 부품 — 운반책이 어디로 가는지를 들고, 운반을 시작시키고, 결과를 밖에 알린다. (#991)
///
/// <see cref="NpcIntruder"/>와 같은 역할이지만 <b>plain MonoBehaviour</b>다 —
/// <see cref="SmugglerCourierEvent"/>가 스폰 직후 <c>AddComponent</c>로 붙인다
/// (<see cref="MisdemeanorOffender"/>와 같은 방침). NPC 프리팹에 미리 배선할 필요가 없고,
/// FSM은 서버 전용이라 복제할 것도 없다.
///
/// 값을 읽는 것은 <see cref="NpcSmuggleState"/> 하나뿐이다.
/// </summary>
public class SmugglerCargo : MonoBehaviour
{
    /// <summary>걸어갈 맨홀 지점 — 없으면 이 운반책은 불발이다.</summary>
    public Transform Destination { get; private set; }

    /// <summary>지금 적용할 걸음 속도 배율 — 평소에는 짐이 무거워 느리고, 맞으면 <see cref="ServerPanic"/>로 올라간다.
    /// <see cref="NpcSmuggleState"/>가 <b>매 틱</b> 읽어 에이전트에 건다 — 그래야 도중에 바뀐 값이 반영된다.</summary>
    public float SpeedMultiplier { get; private set; } = 1f;

    /// <summary>맞고 달리기 시작했는가 — 한 번 켜지면 도착하거나 잡힐 때까지 유지된다.</summary>
    public bool IsPanicked { get; private set; }

    /// <summary>운반 종료 — reached=true 맨홀 도달(이벤트 실패), false 경로 실패(불발).
    /// <see cref="SmugglerCourierEvent"/>가 구독한다. 서버(또는 오프라인)에서만 발생한다.</summary>
    public event Action<NpcController, bool> OnFinished;

    private float m_walkMultiplier = 1f;
    private float m_panicMultiplier = 1f;

    /// <summary>
    /// 운반 시작 — 맨홀 지점을 박고 FSM을 <see cref="NpcState.Smuggling"/>으로 넘긴다.
    /// <b>서버(또는 오프라인) 전용</b> — plain MonoBehaviour라 스스로 권한을 물을 수 없으므로,
    /// 서버에서만 도는 자리(<c>SpawnedNpcEventBase.ApplyBehavior</c>)에서 부르는 것이 계약이다.
    /// </summary>
    public void ServerBeginSmuggling(
        Transform destination,
        float walkMultiplier,
        float panicMultiplier
    )
    {
        Destination = destination;
        m_walkMultiplier = Mathf.Max(0.1f, walkMultiplier);
        m_panicMultiplier = Mathf.Max(m_walkMultiplier, panicMultiplier);
        SpeedMultiplier = m_walkMultiplier;
        IsPanicked = false;

        GetComponent<NpcController>().StateMachine.ChangeState(NpcState.Smuggling);
    }

    /// <summary>
    /// 맞았다 — 짐을 진 채 맨홀로 <b>달린다</b>. 서버(또는 오프라인) 전용. (#991)
    ///
    /// 도주(<see cref="NpcFleeState"/>)로 갈아타지 않는 이유는 그쪽이 <b>위협에게서</b> 달아나는 행동이라
    /// 목적지를 잃기 때문이다 — 이 개체가 급해지는 방향은 언제나 맨홀 쪽이다. 그래서 상태는 그대로 두고
    /// 속도만 올린다. 되돌리지 않는 것은 한 대 맞고 다시 느긋해지면 때린 쪽이 손해를 보기 때문이다.
    /// </summary>
    public void ServerPanic()
    {
        if (IsPanicked)
            return;

        IsPanicked = true;
        SpeedMultiplier = m_panicMultiplier;
    }

    /// <summary>운반 종료 통보 — <see cref="NpcSmuggleState"/> 전용.</summary>
    public void NotifyFinished(NpcController npc, bool reached) => OnFinished?.Invoke(npc, reached);
}
