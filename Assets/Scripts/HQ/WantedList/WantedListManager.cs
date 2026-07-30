using System;
using System.Collections.Generic;
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

    // 검거로 리스트에서 내린 항목 보관함 — 범인 탈출(#231) 시 몽타주를 그대로 되살리기 위해 남긴다.
    // 몽타주를 재생성하면 본부가 기억하던 인상착의와 달라져 "아까 그 놈"이 성립하지 않는다.
    // 서버에서만 쓰므로 동기화하지 않는다(재등재도 서버 권위).
    private readonly Dictionary<ulong, WantedEntry> m_arrestedEntries = new Dictionary<ulong, WantedEntry>();

    // 동기화된 수배 리스트 — 본부 UI(#58)가 구독·열람한다. 서버 외에는 읽기 전용으로 취급.
    public NetworkList<WantedEntry> Wanted => m_wanted;

    // 이번 라운드에 등록된 진범 총수 — 검거/탈출로 남은 수가 줄고 늘어도 바뀌지 않는다(신규 몽타주 등록 시에만 +1).
    // HUD "남은/전체" 표시(#331)를 위해 서버 권위로 전 클라이언트에 동기화한다.
    private readonly NetworkVariable<int> m_totalWanted = new NetworkVariable<int>();
    public int TotalWanted => m_totalWanted.Value;
    public event Action OnTotalWantedChanged;

    /// <summary>
    /// 이 피어에서 리스트가 스폰·초기 동기화된 시점 — 뷰가 최초 표시를 위해 구독한다.
    /// NetworkList는 뒤늦게 접속한 클라이언트에 초기 내용을 OnListChanged로 알리지 않으므로,
    /// 이 이벤트로 "지금 상태 그대로 한 번 그려라"를 알려 late-join 빈 화면을 막는다.
    /// </summary>
    public event Action OnListReady;

    public override void OnNetworkSpawn()
    {
        // 전체 수(#331) 변경을 전 피어에서 구독 — 클라 HUD 재갱신용
        m_totalWanted.OnValueChanged += HandleTotalWantedChanged;

        // 서버만 리스트를 채우고 지운다 — 배정·판정이 서버 권위이므로 (#56 패턴)
        if (IsServer)
        {
            m_wanted.Clear();
            m_totalWanted.Value = 0;    // 재시작 시 이전 세션 총수도 함께 초기화 (#209와 동일 취지)
            m_arrestedEntries.Clear(); // 탈출 재등재용 보관함도 함께 — 이전 세션 항목이 새 라운드 NetworkObjectId와 겹치면 엉뚱한 몽타주가 되살아난다 (#231)

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
        m_totalWanted.OnValueChanged -= HandleTotalWantedChanged;

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
            Name = wantedName.ToFixed64(),
            Montage = montageText.ToFixed128(),
            // 현상금은 서버 전용 값이라 항목에 실어야 본부에서 볼 수 있다 (#395)
            Bounty = identity != null ? identity.Bounty : 0,
        });
        // 전체 진범 수 누적(#331) — 검거/탈출로는 줄지 않는다.
        // ⚠ 라운드 도중 수배 리스트에 진범을 새로 추가하는 다른 경로(#102 제보 전화 '승격' 등)가 생기면,
        //   그 경로에서도 반드시 m_totalWanted를 함께 증가시켜야 HUD 전체 진범 수가 어긋나지 않는다.
        m_totalWanted.Value++;
        Debug.Log($"[수배] 등록: {wantedName} — \"{montageText}\" / 현상금 {(identity != null ? identity.Bounty : 0)}원 (현재 {m_wanted.Count}건)");
    }

    // 검거 판정 수신 — 진범을 검거했을 때만 해당 개체의 수배 항목을 지운다. (서버 전용)
    private void HandleArrestJudged(ArrestResult result)
    {
        if (result.Verdict != ArrestVerdict.WantedCriminal || result.Npc == null) 
            return;

        RemoveByNpcId(result.Npc.NetworkObjectId);
    }

    // 전체 진범 수(#331) 변경을 얇은 C# 이벤트로 재발행 — HUD가 값에 관심만 있고 이전/이후 값은 불필요.
    private void HandleTotalWantedChanged(int previous, int current) => OnTotalWantedChanged?.Invoke();

    // 고유 NetworkObjectId로 검거된 그 개체의 항목만 찾아 제거한다.
    private void RemoveByNpcId(ulong npcId)
    {
        for (int i = 0; i < m_wanted.Count; i++)
        {
            if (m_wanted[i].NpcId != npcId) continue;

            WantedEntry removed = m_wanted[i];
            m_wanted.RemoveAt(i);
            // 탈출(#231) 시 되살릴 수 있게 보관 — 지워버리면 몽타주 텍스트를 복원할 방법이 없다
            m_arrestedEntries[npcId] = removed;
            Debug.Log($"[수배] 검거 완료로 제거: {removed.Name} (남은 {m_wanted.Count}건)");
            return;
        }
    }

    /// <summary>
    /// 검거로 내렸던 수배 항목을 되살린다 — 범인 탈출 이벤트(#231) 전용. 서버에서만 호출된다.
    /// 몽타주는 검거 시점에 보관해 둔 것을 그대로 쓴다 — 본부가 기억하던 인상착의와 일치해야
    /// "아까 그 놈"을 다시 찾는 재미가 성립한다.
    /// </summary>
    public void ReinstateByNpcId(ulong npcId)
    {
        // 등록·제거 구독이 OnNetworkSpawn(IsServer)에서만 걸리므로, 네트워크 세션 밖에서는
        // 수배 리스트 자체가 돌지 않는다 — 오프라인 단독 Play에서 되살릴 항목이 없는 것은 정상이라
        // 경고 없이 조용히 빠진다(아래 '보관 항목 없음' 경고는 진짜 이상 상황에만 뜨게 한다).
        if (!IsSpawned || !IsServer)
            return;

        if (!m_arrestedEntries.TryGetValue(npcId, out WantedEntry entry))
        {
            Debug.LogWarning($"WantedListManager: 보관된 수배 항목이 없어 재등재하지 못했다 (NpcId {npcId})", this);
            return;
        }

        m_arrestedEntries.Remove(npcId);
        m_wanted.Add(entry);
        Debug.Log($"[수배] 탈출로 재등재: {entry.Name} (현재 {m_wanted.Count}건)");
    }
}
