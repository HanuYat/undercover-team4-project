/// <summary>
/// 나체(속옷) 난동꾼 — 현장 근처에 스폰돼 <b>플레이어에게서 도주</b>하며 뛰어다녀 소란을 퍼뜨린다. (GDD 6-4, #106)
/// 쫓아가 무력화한 뒤 밧줄로 잡는다.
///
/// 도주 상태(<see cref="NpcFleeState"/>)의 소란 펄스(#81)를 그대로 쓴다 — 이 이벤트 고유 코드는
/// "누구에게서 달아나는가" 한 줄뿐이고, 나머지는 전부 공통 골격(<see cref="SpawnedNpcEventBase"/>)이 맡는다.
/// </summary>
public class StreakerEvent : SpawnedNpcEventBase
{
    protected override ERiotBehavior RiotBehavior => ERiotBehavior.Flee;

    protected override void ApplyBehavior()
    {
        // 위협(스폰 기준이 된 플레이어)에게서 도주하며 뛰어다녀 소란을 퍼뜨린다.
        // 대상이 없으면 배회로 둔다 — 공통 골격이 곧 이탈(잔류)로 끝낸다.
        if (m_threat != null)
            m_npc.Reaction.StartFlee(m_threat);
    }
}
