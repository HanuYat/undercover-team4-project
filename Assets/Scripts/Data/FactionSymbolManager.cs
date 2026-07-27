using System;
using Unity.Mathematics;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 세력별 '진짜 문양' index를 세션 시작 시 1회 선정해 전 클라이언트에 동기화한다. (#222)
/// 세션-상주 오브젝트(SessionObjectSpawner가 destroyWithScene:false로 스폰)라 라운드·씬이 바뀌어도
/// 값이 유지된다 — 방을 새로 팔 때만 새로 선정된다. TeamFund와 동일한 상주 패턴.
/// 정직한 시민은 이 진짜 index의 문양을, 위조범은 그와 다른 가짜 index의 문양을 단다(CriminalAssigner, #223).
/// 본부 대조자료 뷰(#222)는 RealIndex로 이번 세션의 진짜 문양을 표시한다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class FactionSymbolManager : NetworkedManagerBase
{
    [Header("공식 기록 (세력 문양 세트 조회용)")]
    [SerializeField] private OfficialRecords m_officialRecords;

    private readonly NetworkList<byte> m_realIndices = new NetworkList<byte>();

    /// <summary>진짜 index가 정해지거나 바뀐 시점 — 본부 대조자료 뷰(#222)가 구독해 다시 그린다.</summary>
    public event Action OnRealIndicesChanged;

    public override void OnNetworkSpawn()
    {
        m_realIndices.OnListChanged += HandleListChanged;

        if (IsServer)
            RollRealIndices();

        // 늦게 접속한 클라도 이 시점엔 초기 동기화가 끝나 있다 — 최초 표시를 위해 한 번 알린다.
        OnRealIndicesChanged?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        m_realIndices.OnListChanged -= HandleListChanged;
    }

    private void RollRealIndices()
    {
        if (m_officialRecords == null)
        {
            Debug.LogWarning("[FactionSymbolManager] OfficialRecords가 없어 진짜 문양을 선정할 수 없다.", this);
            return;
        }

        m_realIndices.Clear();
        foreach (OfficialRecords.Faction faction in Enum.GetValues(typeof(OfficialRecords.Faction)))
        {
            int count = m_officialRecords.GetVariantsCount(faction);
            byte real = count > 0 ? (byte)Random.Range(0, count) : (byte)0;
            m_realIndices.Add(real);
        }
    }

    /// <summary>지정 세력의 이번 세션 진짜 문양 index. 미동기화·미선정이면 0. (#222)</summary>
    public int RealIndex(OfficialRecords.Faction faction)
    {
        int i = (int)faction;
        return i >= 0 && i < m_realIndices.Count ? m_realIndices[i] : 0;
    }

    private void HandleListChanged(NetworkListEvent<byte> _) => OnRealIndicesChanged?.Invoke();
}
