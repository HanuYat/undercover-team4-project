using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 본부 수배 리스트(#58) — 라운드 검거 대상을 서버 권위로 채우고, 검거되면 해당 항목을 지운다.
/// 리스트는 NetworkList로 전 클라이언트에 동기화되며, 본부 UI는 이 컴포넌트의 <see cref="Wanted"/>를
/// 구독해 표시한다. 진범이 여러 명이면(#127) OnMontageGenerated가 범인마다 발행되어 항목도 그만큼 쌓인다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class WantedListManager : NetworkedManagerBase
{
    private AppearanceAssigner Appearance => App.Game.Appearance;
    private ArrestJudge Judge => App.Game.ArrestJudge;

    // 서버만 쓰기, 전 클라이언트 읽기. UI(#58)는 Wanted.OnListChanged로 갱신을 받는다.
    private readonly NetworkList<WantedEntry> m_wanted = new NetworkList<WantedEntry>();

    // 동기화된 수배 리스트 — 본부 UI(#58)가 구독·열람한다. 서버 외에는 읽기 전용으로 취급.
    public NetworkList<WantedEntry> Wanted => m_wanted;

    /// <summary>
    /// 이 피어에서 리스트가 스폰·초기 동기화된 시점 — 뷰가 최초 표시를 위해 구독한다.
    /// NetworkList는 뒤늦게 접속한 클라이언트에 초기 내용을 OnListChanged로 알리지 않으므로,
    /// 이 이벤트로 "지금 상태 그대로 한 번 그려라"를 알려 late-join 빈 화면을 막는다.
    /// </summary>
    public event Action OnListReady;

    public override void OnNetworkSpawn()
    {
        // 서버만 리스트를 채우고 지운다 — 배정·판정이 서버 권위이므로 (#56 패턴)
        if (IsServer)
        {
            // 재시작(Shutdown 후 StartHost) 시 씬 NetworkObject의 NetworkList에는 이전 세션 항목이
            // 그대로 남아 새 라운드 항목과 섞인다 — 서버가 새로 뜨면 항상 빈 상태로 시작한다 (#209)
            // (몽타주 등록은 라운드 시작 → NPC 스폰 이후라 여기서 지워질 새 항목은 없다)
            m_wanted.Clear();

            // 등록은 외형·몽타주까지 확정된 시점(OnMontageGenerated)에 한다.
            // OnCriminalAssigned 시점엔 외형이 아직 배정 전이라 몽타주가 비어 있다 (AppearanceAssigner).
            if (Appearance != null)
                Appearance.OnMontageGenerated += HandleMontageGenerated;
            else
                Debug.LogWarning("WantedListManager: AppearanceAssigner를 찾지 못해 수배 항목을 등록할 수 없다", this);

            if (Judge != null)
                Judge.OnArrestJudged += HandleArrestJudged;
            else
                Debug.LogWarning("WantedListManager: ArrestJudge를 찾지 못해 검거 시 항목을 지울 수 없다", this);
        }

        // 모든 피어(호스트·클라이언트) 공통: 이 시점엔 NetworkList가 초기 동기화된 상태다.
        // 뒤늦게 접속한 클라이언트도 여기서 현재 수배 내용을 처음 한 번 그리게 된다.
        OnListReady?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        if (Appearance != null)
            Appearance.OnMontageGenerated -= HandleMontageGenerated;

        if (Judge != null)
            Judge.OnArrestJudged -= HandleArrestJudged;
    }

    // 몽타주 생성 완료 = 범인·외형·이름 모두 확정된 시점. 수배 항목을 리스트에 추가한다. (서버 전용)
    // 진범이 여러 명이면(#127) 범인마다 한 번씩 호출되어 항목이 그만큼 추가된다.
    private void HandleMontageGenerated(NpcController criminal, string montageText)
    {
        if (criminal == null)
        {
            Debug.LogWarning("WantedListManager: 범인 NPC가 없어 수배 항목을 등록하지 못했다", this);
            return;
        }

        // 수배 이름은 범인 신원(CitizenIdentity)에서 직접 읽는다 — CriminalAssigner가 배정해 둔 프로필
        CitizenIdentity identity = criminal.GetComponent<CitizenIdentity>();
        CitizenProfile profile = identity != null ? identity.Profile : null;
        string wantedName = profile != null ? profile.CitizenName : criminal.name;

        m_wanted.Add(new WantedEntry
        {
            NpcId = criminal.NetworkObjectId,
            Name = ToFixed64(wantedName),
            Montage = ToFixed128(montageText),
        });
        Debug.Log($"[수배] 등록: {wantedName} — \"{montageText}\" (현재 {m_wanted.Count}건)");
    }

    // 검거 판정 수신 — 진범을 검거했을 때만 해당 개체의 수배 항목을 지운다. (서버 전용)
    private void HandleArrestJudged(ArrestResult result)
    {
        if (result.Verdict != ArrestVerdict.WantedCriminal || result.Npc == null) 
            return;

        RemoveByNpcId(result.Npc.NetworkObjectId);
    }

    // 고유 NetworkObjectId로 검거된 그 개체의 항목만 찾아 제거한다.
    private void RemoveByNpcId(ulong npcId)
    {
        for (int i = 0; i < m_wanted.Count; i++)
        {
            if (m_wanted[i].NpcId != npcId) continue;

            WantedEntry removed = m_wanted[i];
            m_wanted.RemoveAt(i);
            Debug.Log($"[수배] 검거 완료로 제거: {removed.Name} (남은 {m_wanted.Count}건)");
            return;
        }
    }

    // FixedString은 용량 초과 시 던지므로, 초과분은 잘라 안전하게 담는다.
    private static FixedString64Bytes ToFixed64(string value)
    {
        var result = new FixedString64Bytes();
        result.CopyFromTruncated(value ?? string.Empty);
        return result;
    }

    private static FixedString128Bytes ToFixed128(string value)
    {
        var result = new FixedString128Bytes();
        result.CopyFromTruncated(value ?? string.Empty);
        return result;
    }
}
