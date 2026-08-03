using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 상태가 유지되는 동안 화면 중앙 아래에 떠 있는 안내 — App.UI.Prompt로 접근한다. (#493)
/// 구조 프롬프트·다운/기능 정지 안내처럼 <b>조건이 성립하는 내내</b> 보여야 하는 문구를 맡는다
/// (<see cref="PlayerReviveHud"/>가 상태를 보고 넣고 뺀다).
///
/// 시간제(<see cref="TimedMessageView"/>)와 갈리는 지점: 이쪽은 스스로 사라지지 않는다.
/// 조건이 끝나면 호출부가 <see cref="LocalizedMessageView.Hide"/>로 지워야 한다 —
/// 안 지우면 다운이 풀린 뒤에도 "구조를 기다리는 중"이 남는다.
/// </summary>
// 실행 순서는 베이스에도 있지만 각 구체 클래스에 다시 명시한다 — architecture.md R4.
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class PromptView : LocalizedMessageView
{
    /// <summary>안내를 띄운다 — 지울 때까지 남는다. 이미 떠 있으면 덮어쓴다.</summary>
    public void Show(LocalizedString message) => ShowMessage(message);
}
