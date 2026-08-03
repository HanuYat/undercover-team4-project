using UnityEngine;

/// <summary>
/// 화면 상단 경보 토스트 — App.UI.Toast로 접근한다. (#493)
/// 표시·수명·번역은 <see cref="TimedMessageView"/>가 전부 담당하고, 이 클래스는 "상단 경보 자리"라는
/// 정체성만 갖는다 (App 등록이 타입별이라 자리마다 타입이 하나씩 필요하다).
///
/// 표시는 로컬 전용이다: "내 화면에 띄우기"만 하고, 누구에게 보일지는 호출부가 정한다
/// (예: <see cref="PlayerPenaltyView"/>의 추격 경고는 [Rpc(SendTo.Owner)]로 대상 본인에게만 온다).
/// </summary>
// 실행 순서는 베이스에도 있지만 각 구체 클래스에 다시 명시한다 — architecture.md R4.
// (ChannelingGaugeUI·CrosshairUI도 CommonManagerBase 직속인데 각자 선언한다)
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class ToastView : TimedMessageView { }
