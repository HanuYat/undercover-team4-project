using UnityEngine;

/// <summary>
/// 신호 해석기 수신 표시 — 화면 중앙 살짝 위(높이 40% 지점)에 뜬다. App.UI.SignalMessage로 접근한다. (#493)
/// 표시·수명·번역은 <see cref="TimedMessageView"/>가 담당하고, 이 클래스는 자리만 구분한다.
///
/// 상단 경보(<see cref="ToastView"/>)와 자리를 나눠 둔 이유: 무전 수신(8초)과 경보는 동시에 뜰 수
/// 있어 한 자리를 공유하면 서로를 덮는다. 크로스헤어(정중앙)와 입력창(높이 60%) 사이를 비켜
/// 40% 지점에 둔다.
/// </summary>
// 실행 순서는 베이스에도 있지만 각 구체 클래스에 다시 명시한다 — architecture.md R4.
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class SignalMessageView : TimedMessageView { }
