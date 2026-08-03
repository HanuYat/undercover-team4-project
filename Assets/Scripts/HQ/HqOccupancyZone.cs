using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 본부 상주 감지 — 본부 구역 안에 플레이어가 몇 명 있는지, 비어 있다면 얼마나 오래 비었는지를 센다. (GDD 4-1, #231)
/// 콜라이더는 Is Trigger여야 한다 — 구역 안에 들어온 <b>플레이어</b>를 센다.
///
/// <b>이 컴포넌트는 무인일 때 무슨 일이 일어나는지 모른다</b> — 상태만 노출하고, 그 위에 얹히는 규칙
/// (범인 탈출 이벤트 #231, 제보 전화 등)은 각자 이 값을 읽어 스스로 판단한다.
/// 그래서 같은 "본부를 비우면 손해" 계열 기능이 늘어도 이 파일은 그대로다.
///
/// 서버 권위 — 판정이 서버(또는 오프라인)에서만 필요하므로 동기화하지 않는다.
/// (본부 UI가 상주 인원을 표시해야 할 때 NetworkVariable을 얹는다)
/// </summary>
[RequireComponent(typeof(Collider))]
public class HqOccupancyZone : MonoBehaviour
{
    // 구역 안의 플레이어 — 콜라이더가 여러 개인 리그에서도 중복 없이 세기 위해 집합으로 관리한다
    private readonly HashSet<PlayerHealth> m_occupants = new HashSet<PlayerHealth>();

    // 무인이 된 시각(Time.time). 유인이면 의미 없음 — UnmannedSeconds가 0을 돌려준다
    private float m_unmannedSince;

    /// <summary>현재 본부 구역 안의 플레이어 수.</summary>
    public int OccupantCount => m_occupants.Count;

    /// <summary>본부가 무인인가 — 구역 안에 플레이어가 한 명도 없다.</summary>
    public bool IsUnmanned => m_occupants.Count == 0;

    /// <summary>무인이 된 뒤 경과 시간(초). 유인 상태면 0.</summary>
    public float UnmannedSeconds => IsUnmanned ? Time.time - m_unmannedSince : 0f;

    /// <summary>상주 인원 변경 — 본부 UI 등이 구독할 훅.</summary>
    public event Action<int> OnOccupantCountChanged;

    private void Awake()
    {
        // 시작 시점엔 아무도 등록되지 않았다 — 라운드 시작 직후 곧바로 "오래 비어 있었다"가 되지 않게 지금부터 센다
        m_unmannedSince = Time.time;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsAuthority)
            return;

        PlayerHealth player = other.GetComponentInParent<PlayerHealth>();
        if (player == null)
            return;

        if (!m_occupants.Add(player))
            return; // 콜라이더가 여러 개인 리그에서의 중복 진입

        Debug.Log($"[본부] 상주 진입: {player.name} — 현재 {m_occupants.Count}명");
        OnOccupantCountChanged?.Invoke(m_occupants.Count);
    }

    private void OnTriggerExit(Collider other)
    {
        if (!IsAuthority)
            return;

        PlayerHealth player = other.GetComponentInParent<PlayerHealth>();
        if (player == null)
            return;

        if (!m_occupants.Remove(player))
            return;

        if (m_occupants.Count == 0)
            m_unmannedSince = Time.time;

        Debug.Log($"[본부] 상주 이탈: {player.name} — 현재 {m_occupants.Count}명");
        OnOccupantCountChanged?.Invoke(m_occupants.Count);
    }

    private void Update()
    {
        // 접속 종료·디스폰으로 파괴된 플레이어는 OnTriggerExit이 오지 않는다 — 그대로 두면
        // 아무도 없는 본부가 영원히 '유인'으로 남아 이벤트가 영영 발동하지 않는다.
        if (!IsAuthority || m_occupants.Count == 0)
            return;

        if (m_occupants.RemoveWhere(player => player == null) <= 0)
            return;

        if (m_occupants.Count == 0)
            m_unmannedSince = Time.time;

        Debug.Log($"[본부] 파괴된 상주자 정리 — 현재 {m_occupants.Count}명");
        OnOccupantCountChanged?.Invoke(m_occupants.Count);
    }

    // 서버(또는 오프라인)에서만 센다 — 구역 판정 공통 가드
    private static bool IsAuthority =>
        NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer;
}
