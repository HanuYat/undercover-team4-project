/// <summary>
/// 동네 깡패 — 현장 근처에 스폰돼 <b>표적 플레이어 한 명을 정해 파이프를 들고 쫓아가 때린다</b>. (GDD 6-4, #806)
///
/// 그 자리에서 버티던 거리 난동자를 대체한다 (2026-08-23 확정). 저항형은 <b>플레이어가 다가가야</b>
/// 사건이 되어 "돌발 이벤트가 났다"가 읽히지 않았다 — 능동적으로 달려오면 현장은 즉시 알아채고
/// 본부도 CCTV로 추격을 본다.
///
/// 상태를 새로 만들지 않는다 — 저항(<see cref="NpcResistState"/>)이 이미 <b>표적 추격 + 텔레그래프
/// 스윙</b>이다. 달라지는 것은 둘뿐이고 코드가 아니라 배선에 있다:
///  · <b>표적 고정</b> — 스폰 기준 플레이어를 유발자로 넘긴다(예전에는 표적 없이 시작해 근처 아무나를 잡았다).
///  · <b>포기하지 않는다</b> — 깡패 전용 <see cref="NpcResistConfig"/>가 포기 거리를 크게 잡는다.
///    공용 에셋은 시민 저항형도 쓰므로 건드리지 않는다.
///
/// <b>무기는 파이프다</b> — 프리팹(<c>NPC_StreetThug</c>) 오른손에 붙고, 스윙 모션은
/// <c>NPC_StreetThug.overrideController</c>가 맨손 권투 클립을 1H 무기 스윙으로 갈아 끼운다.
/// 타격 프레임은 그 클립에 맞춘 값이 깡패 전용 config에 있다 (<c>NpcAnimatorControllerBuilder</c> 주석 참고).
///
/// <b>소란 지속 시간은 0(무제한)이다</b> — 끝은 제압·인계 또는 라운드 종료뿐이다.
/// 표적이 다운되면 놓아주고 근처의 다른 플레이어로 넘어간다(저항 상태의 기존 규칙) — 쓰러진 사람을
/// 계속 때려 구조를 막지 않는다.
///
/// 납치(<see cref="AbductionEvent"/>)와 갈래가 다르다: 그쪽은 조용히 뒤를 잡아 라운드 아웃까지 가고,
/// 이쪽은 정면에서 시끄럽게 HP만 깎는다. 잡으면 경범죄 수익은 다른 스폰형과 같다.
/// </summary>
public class StreetThugEvent : SpawnedNpcEventBase
{
    public override string NoticeKey => "Hud.Event.Notice.StreetThug";

    protected override ERiotBehavior RiotBehavior => ERiotBehavior.Resist;

    protected override void ApplyBehavior()
    {
        // 스폰 기준 플레이어를 유발자로 넘겨 표적을 고정한다. 대상이 사라졌으면 저항 상태가
        // 근처 플레이어를 스스로 찾는다 — 표적 없이 서 있는 그림은 나오지 않는다.
        m_npc.Reaction.StartResist(m_threat);
    }
}
