/// <summary>
/// 사망(Dead) 상태 — 체력 0. (#571)
///
/// <b>이 클래스는 거의 비어 있고, 그게 사양이다.</b> 죽은 몸은 판단하지 않는다.
/// 진입 시의 정리(링크 해제·에이전트 정지)는 <see cref="NpcDeath.ServerEnterDead"/>가 <b>전이 뒤에</b>
/// 한다 — <see cref="NpcKnockback.ServerApplyKnockback"/>과 같은 순서다. 여기서 에이전트를 끄면
/// 직전 상태의 <c>Exit()</c>이 이미 꺼진 에이전트를 건드린다(<c>NpcStunnedState.Exit</c>의
/// <c>isStopped = false</c>가 대표적이다).
///
/// <b><c>Exit()</c>은 도달하지 않는다</b> — <see cref="NpcStateMachine.ChangeState"/>가 사망에서
/// 나가는 전이를 아예 막고 그 자리에서 알린다. 여기에 방어를 겹쳐 두면 같은 사고를 두 곳이
/// 보고하게 되므로 비워 둔다.
/// </summary>
public class NpcDeadState : NpcStateBase
{
    public NpcDeadState(NpcController owner)
        : base(owner) { }

    public override void Enter() { }

    public override void Tick() { }

    public override void Exit() { }
}
