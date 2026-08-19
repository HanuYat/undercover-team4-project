using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 씬 전환 예고 (#748) — 서버가 화면을 덮기 직전에 알려 클라이언트도 같이 덮게 한다.
/// 이게 없으면 클라는 NGO 씬 이벤트가 와야 덮어서, 서버의 렌더 보장 대기만큼 늦는다.
///
/// 자리는 SessionState 프리팹 — 전환을 알리는 쪽이 씬과 함께 죽으면 안 된다 (TeamFund와 같은 상주 홀더).
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class SceneTransitionAnnouncer : NetworkedManagerBase
{
    /// <summary>클라이언트 전원에게 로딩 화면을 미리 덮으라고 알린다 — 서버 전용.</summary>
    public void AnnounceCover()
    {
        if (!IsSpawned || !IsServer)
            return;

        CoverRpc();
    }

    // 서버는 App.LoadSceneAsync가 이미 덮고 있다.
    [Rpc(SendTo.NotServer)]
    private void CoverRpc() => App.UI.Loading?.CoverForIncomingSceneChange();
}
