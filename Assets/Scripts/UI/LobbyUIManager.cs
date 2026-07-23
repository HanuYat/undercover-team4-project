using UnityEngine;

// R4: UIManagerBase의 실행 순서(UIManagement)는 상속돼도 재선언해야 적용된다.
// 누락 시 매니저가 PanelBase(UIPanel)보다 늦게 Awake → SessionCodePanel 등록 실패.
[DefaultExecutionOrder((int)EExecutionOrder.UIManagement)]
public class LobbyUIManager : UIManagerBase { }
