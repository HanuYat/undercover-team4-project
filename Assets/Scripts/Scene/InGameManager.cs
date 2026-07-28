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
    // 준비 신호가 오지 않아도 로딩 화면에 갇히지 않도록 하는 상한.
    private const float k_readyTimeoutSeconds = 20f;

    /// <summary>
    /// 게임 씬 준비 완료 대기 (#403) — 씬 오브젝트는 활성화 프레임에 다 서지만 NPC는 그 뒤에 채워진다.
    /// 서버와 클라이언트가 기다리는 대상이 다르다:
    ///  · 서버·오프라인 — NpcSpawner가 프레임당 한 마리씩 스폰하므로 스폰 완료까지 기다린다.
    ///  · 클라이언트   — NpcSpawner.StartSpawn이 클라에서 곧바로 return하므로 IsSpawnCompleted가 영영
    ///    false다. 그걸 기다리면 타임아웃까지 갇히므로, 자기 플레이어 오브젝트 도착까지만 기다리는
    ///    근사로 둔다. (NPC 전원 도착까지 정확히 맞추려면 서버 권위 플래그를 복제해야 한다 — 후속)
    /// </summary>
    public override async UniTask WaitUntilReadyAsync(CancellationToken token)
    {
        float deadline = Time.realtimeSinceStartup + k_readyTimeoutSeconds;
        await UniTask.WaitUntil(
            () => IsReady() || Time.realtimeSinceStartup >= deadline,
            cancellationToken: token
        );

        if (!IsReady())
            Debug.LogWarning(
                $"[InGameManager] 씬 준비 완료를 {k_readyTimeoutSeconds}초 내에 확인하지 못했다 — 로딩 화면을 내린다",
                this
            );
    }

    private static bool IsReady()
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
