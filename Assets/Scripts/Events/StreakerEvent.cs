/// <summary>
/// 공연음란범 — 속옷 차림으로 도심을 <b>쉬지 않고</b> 뛰어다닌다. 쫓아가 무력화한 뒤 밧줄로 잡는다.
/// (GDD 6-4, #106)
///
/// 다른 스폰형과 달리 <b>진정하지도, 잔류로 넘어가지도 않는다</b> — 소란 지속 시간을 0(무제한)으로
/// 두면 공통 골격이 타이머를 끄고, 이벤트가 대상을 계속 쥐고 있어 <b>라운드당 한 명</b>이 된다
/// (<see cref="SuddenEventManager"/>는 비활성 이벤트만 다시 추첨한다). 잡아서 인계하면 이벤트가
/// 풀리므로 나중에 새 공연음란범이 나올 수 있다.
///
/// 도주(<see cref="NpcFleeState"/>)를 쓰지 않는 이유는 그쪽이 <b>위협에게서</b> 달아나는 행동이라
/// 멀어지면 배회로 가라앉기 때문이다 — 여기 필요한 것은 대상도 끝도 없는 질주라
/// <see cref="NpcSprintState"/>를 따로 둔다.
/// </summary>
public class StreakerEvent : SpawnedNpcEventBase
{
    public override string NoticeKey => "Hud.Event.Notice.Streaker";

    protected override ERiotBehavior RiotBehavior => ERiotBehavior.Sprint;

    protected override void ApplyBehavior()
    {
        // 위협을 보지 않는다 — 스폰 기준 플레이어가 사라져도 하던 대로 계속 뛴다.
        m_npc.Reaction.StartSprint();
    }
}
