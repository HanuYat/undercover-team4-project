using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

/// <summary>
/// 돌발 이벤트 핸들러들이 공유하는 서버 권위 헬퍼 — 현장 플레이어 탐색·NavMesh 스폰 지점 산출·스폰물 정리. (#106)
/// 모두 서버(또는 오프라인)에서만 호출되는 것을 전제로 한다.
/// </summary>
public static class SuddenEventUtil
{
    /// <summary>네트워크 세션이 켜져 있는지 — 꺼져 있으면 스폰물을 NGO에 싣지 않고 로컬로만 다룬다. (NpcSpawner와 동일 판정)</summary>
    public static bool IsNetworkSessionActive =>
        NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

    /// <summary>
    /// 행동 가능한 현장 플레이어 중 하나를 무작위로 고른다 — 없으면 null.
    /// 다운된 플레이어는 제외한다: 전원 다운이면 습격·소란을 걸 대상이 없으므로 이벤트 자체가 성립하지 않는다. (#105)
    /// (본부/현장 구분 도입 전이라 씬에 존재하는 <see cref="PlayerData"/>를 현장 플레이어로 본다)
    /// </summary>
    public static Transform FindRandomFieldPlayer()
    {
        PlayerData[] players = UnityEngine.Object.FindObjectsByType<PlayerData>(
            FindObjectsSortMode.None
        );

        // 후보를 배열 앞쪽에 모아두고 그 안에서 고른다 — 추가 할당 없이 다운된 플레이어를 걸러낸다
        int candidateCount = 0;
        for (int i = 0; i < players.Length; i++)
        {
            if (players[i].IsTargetable)
                players[candidateCount++] = players[i];
        }

        if (candidateCount == 0)
            return null;
        return players[Random.Range(0, candidateCount)].transform;
    }

    /// <summary>
    /// 기준점에서 <paramref name="maxRadius"/>(m) 이내의 가장 가까운 행동 가능한 현장 플레이어 — 없으면 null.
    /// 다운된 플레이어는 제외한다. (#105, #106)
    ///
    /// 물리 쿼리(OverlapSphere) 대신 플레이어 목록을 직접 훑는다 — 플레이어는 최대 6명이라 훨씬 저렴하고,
    /// 도시 씬처럼 콜라이더가 빽빽한 곳에서 논알록 버퍼가 넘쳐 표적을 놓치는 문제가 없다.
    /// </summary>
    public static PlayerData FindNearestFieldPlayer(Vector3 origin, float maxRadius)
    {
        PlayerData[] players = UnityEngine.Object.FindObjectsByType<PlayerData>(
            FindObjectsSortMode.None
        );

        PlayerData nearest = null;
        float nearestSqr = maxRadius * maxRadius; // 반경 밖은 애초에 후보가 되지 않는다
        for (int i = 0; i < players.Length; i++)
        {
            PlayerData player = players[i];
            if (!player.IsTargetable)
                continue;

            float sqr = (player.transform.position - origin).sqrMagnitude;
            if (sqr <= nearestSqr)
            {
                nearestSqr = sqr;
                nearest = player;
            }
        }
        return nearest;
    }

    /// <summary>
    /// 기준점에서 <paramref name="maxRadius"/>(m) 이내의 행동 가능한 현장 플레이어를 전부 <paramref name="results"/>에 모은다.
    /// 다운된 플레이어는 제외한다. 호출 시 리스트를 비우므로 호출자는 버퍼를 재사용할 수 있다. (#213)
    ///
    /// <see cref="FindNearestFieldPlayer"/>와 같은 기준(PlayerData 목록 직접 순회 + IsTargetable)을 쓴다 —
    /// 한쪽만 물리 쿼리를 쓰면 두 경로의 대상 집합이 어긋난다.
    /// </summary>
    public static void CollectFieldPlayers(Vector3 origin, float maxRadius, List<Transform> results)
    {
        results.Clear();

        PlayerData[] players = UnityEngine.Object.FindObjectsByType<PlayerData>(
            FindObjectsSortMode.None
        );

        float maxSqr = maxRadius * maxRadius;
        for (int i = 0; i < players.Length; i++)
        {
            PlayerData player = players[i];
            if (!player.IsTargetable)
                continue;

            if ((player.transform.position - origin).sqrMagnitude <= maxSqr)
                results.Add(player.transform);
        }
    }

    /// <summary>
    /// 기준점 주변 링(min~max 거리) 안에서 NavMesh 위 스폰 지점을 찾는다 — 시도 실패가 반복되면 false.
    /// 화면 밖·너무 붙지 않게 플레이어에게서 일정 거리를 두고 스폰하기 위함.
    /// </summary>
    public static bool TryFindSpawnPositionNear(
        Vector3 origin,
        float distanceMin,
        float distanceMax,
        float navSampleMaxDistance,
        int maxAttempts,
        out Vector3 result
    )
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            Vector2 dir = Random.insideUnitCircle.normalized;
            float distance = Random.Range(distanceMin, distanceMax);
            Vector3 candidate = origin + new Vector3(dir.x, 0f, dir.y) * distance;

            if (
                NavMesh.SamplePosition(
                    candidate,
                    out NavMeshHit hit,
                    navSampleMaxDistance,
                    NavMesh.AllAreas
                )
            )
            {
                result = hit.position;
                return true;
            }
        }

        result = default;
        return false;
    }

    // 출구 선정 시 현장 플레이어와 유지해야 하는 최소 거리(m) — 이벤트 NPC는 플레이어 근처에 스폰되므로
    // '무조건 최근접 출구'는 방금 싸운 자리 옆일 수 있다. 그러면 걸어 나가는 그림 없이 눈앞에서 글리치가
    // 터져 즉시 증발처럼 보인다 (#310 후속 피드백).
    private const float k_exitClearOfPlayersMeters = 12f;

    /// <summary>
    /// 이탈하는 이벤트 NPC가 걸어 나갈 '출구' — 씬에 배치된 전용 마커(<see cref="SuddenEventExitPoint"/>) 중
    /// NPC 최근접을 고른다. 마커가 없는 씬은 일반 NPC 스폰 포인트 폴백으로 동작한다: 모든 현장
    /// 플레이어에게서 <see cref="k_exitClearOfPlayersMeters"/> 이상 떨어진 포인트 중 NPC 최근접, 전부 플레이어
    /// 근처면 플레이어에게서 가장 먼 포인트. 그마저 없으면 null(호출부가 그 자리 소멸로 처리). (#310)
    /// 스폰 포인트 겸용은 액션 한복판이라 퇴장 그림이 어색하다는 피드백으로 전용 마커를 도입했다.
    /// </summary>
    public static Transform FindExitPoint(Vector3 npcPosition)
    {
        // 1순위 — 전용 출구 마커 중 최근접. 마커는 맵 가장자리 등 '나가는 그림'이 되는 곳에 배치돼 있다.
        IReadOnlyList<SuddenEventExitPoint> exits = SuddenEventExitPoint.Active;
        Transform nearestExit = null;
        float nearestExitSqr = float.MaxValue;
        for (int i = 0; i < exits.Count; i++)
        {
            SuddenEventExitPoint exit = exits[i];
            if (exit == null)
                continue;

            float sqr = (exit.transform.position - npcPosition).sqrMagnitude;
            if (sqr < nearestExitSqr)
            {
                nearestExitSqr = sqr;
                nearestExit = exit.transform;
            }
        }
        if (nearestExit != null)
            return nearestExit;

        // 폴백 — 마커가 없는 씬(테스트 씬 등)은 기존 스폰 포인트 선정으로 동작한다.
        NpcSpawner spawner = App.Game.NpcSpawner;
        IReadOnlyList<Transform> points = spawner != null ? spawner.SpawnPoints : null;
        if (points == null)
            return null;

        Transform nearestClear = null;        // 플레이어 간섭 없는 포인트 중 NPC 최근접
        float nearestClearSqr = float.MaxValue;
        Transform farthestFromPlayers = null; // 폴백 — 플레이어에게서 가장 먼 포인트
        float farthestPlayerSqr = -1f;

        for (int i = 0; i < points.Count; i++)
        {
            Transform point = points[i];
            if (point == null)
                continue;

            PlayerData nearbyPlayer = FindNearestFieldPlayer(point.position, k_exitClearOfPlayersMeters);
            if (nearbyPlayer == null)
            {
                float sqr = (point.position - npcPosition).sqrMagnitude;
                if (sqr < nearestClearSqr)
                {
                    nearestClearSqr = sqr;
                    nearestClear = point;
                }
                continue;
            }

            float playerSqr = (nearbyPlayer.transform.position - point.position).sqrMagnitude;
            if (playerSqr > farthestPlayerSqr)
            {
                farthestPlayerSqr = playerSqr;
                farthestFromPlayers = point;
            }
        }

        return nearestClear != null ? nearestClear : farthestFromPlayers;
    }

    /// <summary>
    /// 스폰물을 정리한다 — 네트워크 세션이면 Despawn, 아니면 Destroy. null·미스폰 상황을 안전하게 처리한다.
    /// 대상 프리팹에 <see cref="NpcDespawnVfx"/>가 배선돼 있으면 사라지는 자리에 소멸 연출을 남긴다 —
    /// 라운드 종료 일괄 정리처럼 연출이 필요 없는 경로만 playVfx=false로 끈다. (#310)
    /// </summary>
    public static void DespawnOrDestroy(GameObject target, bool playVfx = true)
    {
        if (target == null)
            return;

        if (playVfx)
        {
            NpcDespawnVfx vfx = target.GetComponent<NpcDespawnVfx>();
            if (vfx != null)
                vfx.ServerPlay();
        }

        if (IsNetworkSessionActive)
        {
            NetworkObject netObj = target.GetComponent<NetworkObject>();
            if (netObj != null && netObj.IsSpawned)
            {
                netObj.Despawn();
                return;
            }
        }

        UnityEngine.Object.Destroy(target);
    }
}
