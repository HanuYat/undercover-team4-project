using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 씬 전환 예고 (#748) — 서버가 화면을 덮기 <b>직전에</b> "지금 넘어간다"를 클라이언트에게 알린다.
///
/// 이게 없으면 클라이언트가 눈에 띄게 늦게 덮는다. 서버는 <see cref="App.LoadSceneAsync"/>에서 먼저
/// 덮고 렌더 보장 프레임을 흘린 뒤에야 NGO 씬 로드를 시작하는데, 클라이언트는 그 NGO 씬 이벤트를
/// 받아야 비로소 덮기 때문이다 — 그 대기가 통째로 시차가 된다.
///
/// TeamFund·RoundProgress와 같은 세션 상주 홀더다 (#214 §6) — 전환을 알리는 쪽이 씬과 함께 죽으면
/// 알릴 수 없다. 세션 없이 씬을 직접 Play하면 스폰되지 않아 null이고, 그때는 알릴 상대도 없다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class SceneTransitionAnnouncer : NetworkedManagerBase
{
    /// <summary>클라이언트 전원에게 로딩 화면을 미리 덮으라고 알린다 — 서버 전용, 세션 밖에서는 무동작.</summary>
    public void AnnounceCover()
    {
        if (!IsSpawned || !IsServer)
            return;

        CoverRpc();
    }

    // 서버 자신은 App.LoadSceneAsync가 이미 덮고 있어 받을 필요가 없다.
    // 로딩 화면은 AppBootstrap 상주라 어느 씬에서 받아도 살아 있다.
    [Rpc(SendTo.NotServer)]
    private void CoverRpc() => App.UI.Loading?.CoverForIncomingSceneChange();
}
