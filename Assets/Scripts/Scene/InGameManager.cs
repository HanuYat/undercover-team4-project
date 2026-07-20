using UnityEngine;

/// <summary>
/// InGame(Main Scene) 씬 진입점 — 라운드 진행은 RoundManager, 대기→게임 시작은 LobbyManager가
/// 그대로 담당하고, 여기는 씬 흐름의 자리만 잡는다. (#247)
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class InGameManager : SceneManagerBase
{
    // 씬 전환이 필요한 흐름(로비 복귀 등)이 생기면 여기로 모은다 — 지금은 진입점 역할만.
}
