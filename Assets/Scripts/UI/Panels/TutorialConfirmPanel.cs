/// <summary>
/// 첫 접속 튜토리얼 권유 창 (#663) — "튜토리얼을 해보시겠습니까?" 예/아니오.
/// <see cref="SessionPanel"/>이 처음 열릴 때 한 번만 뜬다(다시 묻지 않는 기억은 <see cref="TutorialFlow"/>).
/// 버튼 배선·연타 방어·딤 배경은 <see cref="ConfirmPanelBase"/>가 맡는다.
///
/// 창을 닫지 않는다 — '예'는 곧 씬 전환이라 닫을 대상이 사라진다. '아니오'는 베이스가 닫는다.
/// 씬당 하나뿐인 ESC 진입 메뉴는 타이틀에서 <see cref="QuitConfirmPanel"/>이므로 IsEscMenu는 켜지 않는다.
/// </summary>
public class TutorialConfirmPanel : ConfirmPanelBase
{
    protected override void OnConfirm() => TutorialFlow.Enter();
}
