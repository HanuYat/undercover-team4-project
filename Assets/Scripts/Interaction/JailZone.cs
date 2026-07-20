using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 유치장 — 검거된 범인을 실제로 수용·관리하는 구역. (GDD 7-2, #228)
/// 판정 구역(HqDropoffZone/ArrestJudge)과 분리되어 있다: 판정은 "누가 범인인가"만,
/// 여기는 "어디에 가두고 몇 명이 있는가"만 안다. 둘을 잇는 건 CustodyRouter다.
///
/// 수용 인원은 서버 권위로 세어 NetworkVariable로 전 피어에 동기화한다 (#56 패턴) —
/// 본부 UI(별도 이슈)는 InmateCount/OnInmateCountChanged를 읽으면 된다.
/// 자물쇠·탈출(별도 이슈)은 ReleaseInmate로 이 카운트에서 빠져나간다.
/// </summary>
public class JailZone : NetworkBehaviour
{
    [Header("수용 지점 (비우면 유치장 자신의 위치)")]
    [Tooltip("수감된 NPC가 걸어가 서는 지점들. 순서대로 배정된다 — NavMesh 위에 둘 것")]
    [SerializeField] private Transform[] m_cellPoints;

    // 서버 권위 수용 인원 — 서버만 쓰고 모든 클라이언트가 읽는다 (#56)
    private readonly NetworkVariable<int> m_inmateCount = new NetworkVariable<int>(0);

    // 오프라인(네트워크 없이 Play) 폴백용 로컬 값 — NpcController의 게이지 이중 구조와 동일
    private int m_localInmateCount;

    // 이미 수용된 NPC — 중복 카운트 방어(도착 통보가 두 번 오거나 재수용되는 경우)
    private readonly HashSet<NpcController> m_inmates = new HashSet<NpcController>();

    // 수용 지점 순차 배정 커서 — 여러 명이 한 점에 겹쳐 서지 않게 돌려 쓴다
    private int m_nextCellIndex;

    /// <summary>현재 수용 인원. 네트워크 세션 중에는 동기화된 값이라 클라이언트에서도 읽을 수 있다.</summary>
    public int InmateCount => IsSpawned ? m_inmateCount.Value : m_localInmateCount;

    /// <summary>수용 인원 변경 — 서버·클라이언트 모든 피어에서 발생한다. 본부 UI(별도 이슈)가 구독.</summary>
    public event Action<int> OnInmateCountChanged;

    public override void OnNetworkSpawn()
    {
        m_inmateCount.OnValueChanged += HandleInmateCountChanged;
    }

    public override void OnNetworkDespawn()
    {
        m_inmateCount.OnValueChanged -= HandleInmateCountChanged;
    }

    private void HandleInmateCountChanged(int previous, int current)
    {
        OnInmateCountChanged?.Invoke(current);
    }

    /// <summary>
    /// 수용 지점 배정 — 수감 대상 1명이 걸어갈 지점을 내준다. 서버(또는 오프라인)에서 호출.
    /// 지점 수보다 많이 들어오면 앞에서부터 돌려 쓴다 — 겹친 NPC는 NavMesh 회피가 흩어 준다.
    /// </summary>
    public Transform ReserveCell()
    {
        if (m_cellPoints == null || m_cellPoints.Length == 0)
            return transform;

        // 인스펙터에서 비워 둔 슬롯은 건너뛴다
        for (int i = 0; i < m_cellPoints.Length; i++)
        {
            Transform cell = m_cellPoints[m_nextCellIndex % m_cellPoints.Length];
            m_nextCellIndex = (m_nextCellIndex + 1) % m_cellPoints.Length;

            if (cell != null)
                return cell;
        }

        return transform;
    }

    /// <summary>
    /// 수용 — NPC가 수용 지점에 도달했을 때 호출된다 (NpcController.OnJailed 구독).
    /// 판정 시점이 아니라 실제로 걸어 들어온 시점에 세므로, 이송 중 탈출(별도 이슈)이 카운트를 오염시키지 않는다.
    /// </summary>
    public void Admit(NpcController npc)
    {
        if (npc == null)
            return;

        // 카운트는 서버 권위 — 클라이언트에서 불려도 무시한다
        if (IsSpawned && !IsServer)
            return;

        if (!m_inmates.Add(npc))
            return; // 이미 수용됨 — 중복 통보 무시

        SetInmateCount(m_inmates.Count);
        Debug.Log($"[유치장] 수용: {npc.name} — 현재 {InmateCount}명");
    }

    /// <summary>
    /// 수용 해제 — 범인 탈출 이벤트(별도 이슈)가 호출할 접합점. 카운트에서 뺀다.
    /// 상태 전이(탈출 후 도주 등)는 호출자가 NpcController로 따로 처리한다.
    /// </summary>
    public void ReleaseInmate(NpcController npc)
    {
        if (npc == null)
            return;

        if (IsSpawned && !IsServer)
            return;

        if (!m_inmates.Remove(npc))
            return;

        SetInmateCount(m_inmates.Count);
        Debug.Log($"[유치장] 수용 해제: {npc.name} — 현재 {InmateCount}명");
    }

    // 서버 진실값과 동기화 변수에 함께 기록한다 — 오프라인에서는 NetworkVariable에 쓰지 않고
    // 이벤트를 직접 발행한다 (NpcController.SetSubdueGauge / HandleFsmStateChanged와 동일 구조)
    private void SetInmateCount(int value)
    {
        m_localInmateCount = value;

        if (IsSpawned && IsServer)
            m_inmateCount.Value = value; // OnValueChanged를 거쳐 모든 피어에서 이벤트 발생
        else if (!IsSpawned)
            OnInmateCountChanged?.Invoke(value);
    }
}
