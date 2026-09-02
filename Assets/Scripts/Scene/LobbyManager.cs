using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Lobby 씬 진입점 — 세션 생성 후 최초 대기 공간. 호스트 '시작'으로 상점(허브)으로 넘어간다. (#214)
/// 라운드 루프는 Shop↔Game이고 로비는 최초 1회만 거친다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class LobbyManager : SceneManagerBase
{
    // 내 몸이 정리되기를 기다리는 상한 — 안 오면 경고하고 진행한다(InGameManager와 같은 방침).
    // 로딩 화면에 갇히는 것이 떨어지는 것을 보는 것보다 나쁘다.
    private const float k_localReadyTimeoutSeconds = 10f;

    private SessionManager Session => App.Net.Session;
    private bool IsServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
    private bool m_started;

    /// <summary>
    /// 로비 준비 완료 대기 (#656) — <b>내 플레이어가 사라질 때까지</b> 로딩 화면을 유지한다.
    ///
    /// 로비는 플레이어를 두지 않아(PlayerSpawnManager의 m_spawnPlayers=false) 진입 즉시 서버가
    /// 전원을 despawn한다. 조건이 <b>게임 씬과 정반대</b>인 이유다(그쪽은 도착을 기다린다).
    ///
    /// 플레이어는 destroyWithScene:false라 씬을 넘어와 <b>직전 맵 좌표에 서 있다.</b> 호스트는
    /// Start에서 동기적으로 정리하지만 클라는 그 지시가 네트워크로 와야 해서, 먼저 화면을 내리면
    /// 그 사이가 맵 밖으로 튕겨 나가는 것으로 보인다.
    ///
    /// 프레임 수만 세면(LoadingScreen.k_settleFrames) fps가 높고 RTT가 붙는 빌드에서 진다 —
    /// 에디터에서 재현되지 않던 이유다. 그래서 시간이 아니라 조건을 기다린다.
    /// </summary>
    public override UniTask WaitUntilReadyAsync(CancellationToken token) =>
        WaitUntilLocallyReadyAsync(token, IsLocallyReady, k_localReadyTimeoutSeconds, "플레이어 정리");

    // 내 몸이 없어야 준비 완료다. 세션 밖(오프라인·씬 직접 Play)이면 지워 줄 서버가 없으므로 기다리지 않는다.
    private static bool IsLocallyReady()
    {
        NetworkManager net = NetworkManager.Singleton;
        if (net == null || !net.IsListening)
            return true;

        return net.LocalClient == null || net.LocalClient.PlayerObject == null;
    }

    // 로비는 조인 가능 — 진입 시 세션 잠금 해제. (잠금은 Shop의 '출동'에서)
    private void Start()
    {
        if (IsServer)
            Session?.SetLockedAsync(false).Forget();
    }

    /// <summary>호스트 전용 — 게임 시작. 상점(허브)으로 전환. 전 클라가 NGO 동기화로 따라온다.</summary>
    public void StartGame()
    {
        if (!IsServer || m_started) return;
        m_started = true;
        MoveToNextScene(EScene.Shop);
    }
}
