/// <summary>
/// 거리 난동자 — 현장 근처에 스폰돼 <b>그 자리에서 저항</b>하며 소란을 일으키고 근접 HP 타격을 넣는다. (GDD 6-4, #106)
/// 스스로 멈추지 않으므로 소란의 끝은 공통 지속 타이머다(<see cref="SpawnedNpcEventBase"/>).
///
/// 저항 상태(<see cref="NpcResistState"/>)의 소란 펄스(#81)를 그대로 쓴다 — 이 이벤트 고유 코드는
/// "저항을 시작한다" 한 줄뿐이고, 제압·연행·경범죄 판정·잔류는 전부 공통 골격이 맡는다.
/// </summary>
public class RioterEvent : SpawnedNpcEventBase
{
    protected override ERiotBehavior RiotBehavior => ERiotBehavior.Resist;

    protected override void ApplyBehavior()
    {
        // 그 자리에서 버티며 저항 — 진압봉·테이저로 무력화한 뒤 밧줄로 잡는 대상이자 소란원
        m_npc.Reaction.StartResist();
    }
}
