using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 본부 시민 인명부 (#223) — 라운드에 스폰된 전 NPC의 정본 신원을 서버 권위로 채워 전 클라에 동기화한다.
/// 현장이 스캔한 표시값을 구두로 전달하면 본부가 이 인명부와 대조해 위조 여부를 판단한다 (GDD 5-4).
/// WantedListManager와 동일 패턴: NetworkList + late-join OnListReady.
/// 데이터는 참고자료라 전 클라 공개, 열람 UI만 본부 위치에서 게이트한다(DirectoryView, Step D).
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class DirectoryManager : NetworkedManagerBase
{
    private CriminalAssigner Assigner => App.Game.CriminalAssigner;
    private NpcSpawner Spawner => App.Game.NpcSpawner;

    // 서버만 쓰기, 전 클라 읽기. DirectoryView가 OnListChanged로 갱신받는다.
    private readonly NetworkList<DirectoryEntry> m_directory = new NetworkList<DirectoryEntry>();

    /// <summary>동기화된 인명부 — 본부 UI가 구독·열람한다. 서버 외에는 읽기 전용.</summary>
    public NetworkList<DirectoryEntry> Directory => m_directory;

    /// <summary>이 피어에서 리스트가 스폰·초기 동기화된 시점 — late-join 빈 화면 방지(WantedList와 동일).</summary>
    public event Action OnListReady;

    public override void OnNetworkSpawn()
    {
        // 서버만 채운다 — 배정이 서버 권위 (#56 패턴)
        if (IsServer)
        {
            // 재시작 시 씬 NetworkObject의 NetworkList에 이전 세션 항목이 남으므로 항상 빈 상태로 시작 (#209 패턴)
            m_directory.Clear();

            // 배정은 매니저 스폰 이후(라운드 시작 → 스폰 → 배정) 일어나므로 여기서 구독하면 놓치지 않는다.
            // 재라운드 시 OnCriminalAssigned가 다시 발행되면 BuildDirectory가 Clear 후 다시 채운다.
            if (Assigner != null)
                Assigner.OnCriminalAssigned += HandleAssigned;
            else
                Debug.LogWarning(
                    "DirectoryManager: CriminalAssigner를 찾지 못해 인명부를 채울 수 없다",
                    this
                );
        }

        // 모든 피어 공통 — 이 시점엔 NetworkList가 초기 동기화된 상태. late-join도 여기서 처음 그린다.
        OnListReady?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        if (Assigner != null)
            Assigner.OnCriminalAssigned -= HandleAssigned;
    }

    // 배정 완료 = 전 NPC 정본 확정 시점. 인수(범인 목록)는 무시하고 전 NPC를 순회해 정본으로 채운다. (서버 전용)
    private void HandleAssigned(IReadOnlyList<NpcController> _) => BuildDirectory();

    private void BuildDirectory()
    {
        m_directory.Clear();

        if (Spawner == null)
        {
            Debug.LogWarning(
                "DirectoryManager: NpcSpawner를 찾지 못해 인명부를 채울 수 없다",
                this
            );
            return;
        }

        foreach (NpcController npc in Spawner.SpawnedNpcs)
        {
            if (npc == null)
                continue;

            CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();
            CitizenProfile profile = identity != null ? identity.Profile : null;
            if (profile == null)
                continue;

            // 정본 신원으로 등재 — 위조범도 '진짜 이름'으로 들어간다(스캔 표시값이 이와 어긋남 = 위조)
            m_directory.Add(DirectoryEntry.FromProfile(profile));
        }

        Debug.Log($"[인명부] {m_directory.Count}명 등재 완료");
    }
}
