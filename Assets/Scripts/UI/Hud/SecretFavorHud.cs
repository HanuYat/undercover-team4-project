using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 비밀 청탁 한 줄 (#485) — 받은 사람 화면에만 라운드 내내 남는다. App.UI.SecretFavor로 접근한다.
///
/// <see cref="PromptView"/>와 같은 "지울 때까지 남는" 표시인데도 자리를 따로 두는 이유는, 그쪽은
/// 구조 프롬프트·기능 정지 안내가 쓰는 자리라 청탁 문구가 서로를 덮어쓰기 때문이다
/// (LocalizedMessageView는 자리마다 최신 하나만 띄운다).
///
/// 표시는 로컬 전용이다 — 누구에게 보일지는 <see cref="SecretFavorBroker"/>의 타깃 RPC가 정한다.
/// </summary>
// 실행 순서는 베이스에도 있지만 각 구체 클래스에 다시 명시한다 — architecture.md R4
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class SecretFavorHud : LocalizedMessageView
{
    /// <summary>의뢰를 띄운다 — 완수·취소·라운드 종료로 지울 때까지 남는다.</summary>
    public void Show(LocalizedString message) => ShowMessage(message);
}
