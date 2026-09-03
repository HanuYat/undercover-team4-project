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
    /// <summary>걸어갈 거래 지점 — 없으면 이 운반책은 불발이다.</summary>
    public Transform Destination { get; private set; }

    /// <summary>운반 중 걸음 속도 배율 — 짐이 무거워 보이게 기본 걸음보다 느리게 간다.</summary>
    public float WalkSpeedMultiplier { get; private set; } = 1f;

    /// <summary>운반 종료 — reached=true 거래 지점 도달(이벤트 실패), false 경로 실패(불발).
    /// <see cref="SmugglerCourierEvent"/>가 구독한다. 서버(또는 오프라인)에서만 발생한다.</summary>
    public event Action<NpcController, bool> OnFinished;

    /// <summary>
    /// 운반 시작 — 거래 지점을 박고 FSM을 <see cref="NpcState.Smuggling"/>으로 넘긴다.
    /// <b>서버(또는 오프라인) 전용</b> — plain MonoBehaviour라 스스로 권한을 물을 수 없으므로,
    /// 서버에서만 도는 자리(<c>SpawnedNpcEventBase.ApplyBehavior</c>)에서 부르는 것이 계약이다.
    /// </summary>
    public void ServerBeginSmuggling(Transform destination, float walkSpeedMultiplier)
    {
        Destination = destination;
        WalkSpeedMultiplier = Mathf.Max(0.1f, walkSpeedMultiplier);

        GetComponent<NpcController>().StateMachine.ChangeState(NpcState.Smuggling);
    }

    /// <summary>운반 종료 통보 — <see cref="NpcSmuggleState"/> 전용.</summary>
    public void NotifyFinished(NpcController npc, bool reached) => OnFinished?.Invoke(npc, reached);
}
