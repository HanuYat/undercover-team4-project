using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// InGame(Main Scene) 씬 진입점 — 라운드 진행은 RoundManager, 대기→게임 시작은 LobbyManager가
/// 그대로 담당하고, 여기는 씬 흐름의 자리만 잡는다. (#247)
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class InGameManager : SceneManagerBase
{
    // 이 피어의 로컬 준비(스폰·복제 도착)를 기다리는 상한.
    private const float k_localReadyTimeoutSeconds = 20f;

    /// <summary>
    /// 게임 씬 준비 완료 대기 (#403 → #410).
    /// 로컬 준비(서버·오프라인은 NPC 스폰 완료, 클라는 내 플레이어 오브젝트 도착)까지만 기다리고,
    /// 자기 준비를 SceneReadyGate에 보고한 뒤 곧바로 로딩 화면을 내린다 — <b>전원을 기다리지 않는다.</b>
    ///
    /// 준비된 사람부터 씬에 들어와 돌아다닐 수 있게 한 선택이다. 아직 준비 안 된 동료가 있다는 사실은
    /// ReadyWaitHud가 "대기 중 (2/4)"로 알리고, <b>라운드 시작</b>만 서버가 전원 보고까지 미룬다
    /// (RoundManager). 제한시간은 StartRound부터 흐르므로 일찍 들어와도 손해·이득이 없다.
    /// </summary>
    public override async UniTask WaitUntilReadyAsync(CancellationToken token)
    {
        float deadline = Time.realtimeSinceStartup + k_localReadyTimeoutSeconds;
        await UniTask.WaitUntil(
            () => IsLocallyReady() 
            || Time.realtimeSinceStartup >= deadline,
            cancellationToken: token
        );

        if (!IsLocallyReady())
            Debug.LogWarning(
                $"[InGameManager] 로컬 준비를 {k_localReadyTimeoutSeconds}초 내에 확인하지 못했다 — 그대로 진행한다",
                this
            );

        // 준비 완료 보고 (#410) — 기다리지 않고 바로 들어간다. 게이트가 없으면(오프라인·씬 직접 Play) 무동작.
        App.Game.ReadyGate?.ReportSelfReady();
    }

    private static bool IsLocallyReady()
    {
        NetworkManager net = NetworkManager.Singleton;

        // 클라이언트 — 내 플레이어 오브젝트가 복제로 도착했는가
        if (net != null && net.IsListening && !net.IsServer)
            return net.LocalClient != null && net.LocalClient.PlayerObject != null;

        // 서버·오프라인 — NPC 스폰 완료. 스포너가 없는 씬 구성이면 기다릴 것이 없다.
        NpcSpawner spawner = App.Game.NpcSpawner;
        return spawner == null || spawner.IsSpawnCompleted;
    }
}
