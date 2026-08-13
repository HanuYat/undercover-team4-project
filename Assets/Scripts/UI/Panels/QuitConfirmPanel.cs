/// <summary>
/// Title 씬 ESC 종료 확인창 (#326) — 타이틀엔 이탈할 세션이 없으므로, ESC는 일시정지 대신 게임 종료를 묻는다.
/// 씬의 ESC 진입 메뉴(IsEscMenu)라 스택이 비었을 때 ESC로 열리고 다시 ESC(또는 '아니오')로 닫힌다.
/// 버튼 배선·딤 배경은 <see cref="ConfirmPanelBase"/>가 맡는다.
/// </summary>
public class QuitConfirmPanel : ConfirmPanelBase
{
    public override bool IsEscMenu => true;

    protected override void OnConfirm() => TitleUIManager.QuitGame();
}
