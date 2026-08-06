using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public partial class NpcController
{
    // ---- 감옥 (#228/#537) ----

    /// <summary>
    /// 감옥 밖으로 내보낸다 — 퇴장 동행·탈옥 방출이 부른다. 서버(또는 오프라인) 전용. (#415/#537)
    ///
    /// 감옥은 도시와 이어진 NavMesh 경로가 없는 격리 공간이라(#537), 방출은 곧 순간이동이다.
    /// 워프가 실패하면(퇴장 지점이 NavMesh 밖) 경고만 남기고 제자리에 둔다 — 감옥 안에 남는 편이
    /// NavMesh 밖에 떨어져 굳는 것보다 낫다.
    ///
    /// <b>Jail 영역 통행 회수는 사라졌다</b> (#537). 감옥이 별도 NavMesh 섬이 되면서 시민이 걸어
    /// 들어올 경로 자체가 없어져, 영역 마스크로 막을 일이 없다.
    /// </summary>
    public void ServerExitJail(Vector3 exitPosition)
    {
        if (IsSpawned && !IsServer)
            return;

        if (m_agent == null)
            return;

        if (!TryWarpNear(exitPosition))
        {
            Debug.LogWarning(
                $"NpcController: 감옥 퇴장 지점으로 워프 실패 — 제자리에 둔다: {name}",
                this
            );
        }
    }

    /// <summary>
    /// 이 사람을 밧줄 없이 따라다니는 반출 수감자 전부 — 감옥 퇴장(<see cref="JailIntake"/>)이 동행을 찾는다.
    /// 서버(또는 오프라인) 전용. (#537)
    ///
    /// 반출 표식(<see cref="IsJailExtracted"/>)을 함께 보는 이유: 연행 중인 일반 신병까지 문 밖으로
    /// 딸려 나가면 안 된다. 감옥 안에서 따라다니는 것은 반출한 수감자뿐이다.
    /// </summary>
    public static List<NpcController> FindFollowersOf(Transform follower)
    {
        var found = new List<NpcController>();
        if (follower == null)
            return found;

        NpcController[] npcs = FindObjectsByType<NpcController>(FindObjectsSortMode.None);
        for (int i = 0; i < npcs.Length; i++)
        {
            NpcController npc = npcs[i];
            if (npc != null && npc.IsJailExtracted && npc.EscortTarget == follower)
                found.Add(npc);
        }

        return found;
    }

    // ---- 반출 표식 (#517) ----

    private bool m_jailExtracted;

    // 동기화 플래그 — 서버만 기록한다.
    // 클라도 읽어야 한다: E 조준 피드백(NpcSubdueInteractable.CanInteract)이 이 값으로 분기를 고른다.
    private readonly NetworkVariable<bool> m_jailExtractedSynced = new(false);

    /// <summary>
    /// 감옥에서 반출돼(#492) 밧줄 없이 데려가는 중인 수감자인가 — 거리 이탈로 멈춰 서도(Captured) 유지된다.
    /// 전 피어에서 유효. (#517)
    ///
    /// <b>왜 표식이 필요한가</b> — 멈추면 상태가 Captured가 되는데, 그것만으로는 "반출된 수감자"와
    /// "방금 제압한 신병"을 구분할 수 없다. 구분이 없으면 E가 밧줄 끌기로 새서 반출 흐름으로 되돌릴
    /// 입력이 사라진다. <see cref="IsDelivered"/>로는 못 가른다 — 문 앞 판정을 통과해 끌려가는 중인
    /// 대상도 그 값이 참이다.
    /// </summary>
    public bool IsJailExtracted =>
        IsSpawned && !IsServer ? m_jailExtractedSynced.Value : m_jailExtracted;

    /// <summary>반출 표식 지정 — 켜는 곳은 <see cref="JailIntake"/>의 반출 하나뿐이다. 서버(또는 오프라인). (#517)
    /// 끄는 곳은 셋이다: 커스터디 이탈(재수감·도주·석방), 밧줄에 묶임, 그리고 여기 직접 호출.</summary>
    public void SetJailExtracted(bool value)
    {
        if (IsSpawned && !IsServer)
            return;

        m_jailExtracted = value;
        if (IsSpawned && IsServer)
            m_jailExtractedSynced.Value = value;
    }

    /// <summary>
    /// 수감 — 판정에서 진범·경범죄로 확정된 NPC를 감옥 안 배치 지점으로 보낸다. (<see cref="JailIntake"/> 경유, GDD 7-2)
    /// 문 앞에서 E를 누르는 순간 불린다 (#537 — 끌고 들어가 좌석에 놓던 경로는 폐기).
    /// spot으로 <b>순간이동</b>해 그 자리에 선다. spot이 null이면 그 자리에서 수용된 것으로 처리한다.
    /// 배치 지점 배정은 보내는 쪽(JailZone.ReservePlacement)이 한다 — 자리 계산이 아니라 손으로 배치한 목록이다.
    /// </summary>
    public void SendToJail(Transform spot)
    {
        // FSM 전이는 서버 권위 — StartEscort와 동일하게 클라이언트 호출은 무시한다
        if (IsSpawned && !IsServer)
            return;

        EscortTarget = null; // 판정 시점에 연행은 이미 풀렸지만, 참조가 남아 있으면 여기서 끊는다
        SetJailExtracted(false); // 반출했다 되돌린 대상 — 다시 수감됐으므로 표식을 끈다 (#517)
        JailSpot = spot;
        m_stateMachine.ChangeState(NpcState.Jailed);
    }

    // 수갑 소모·반환(#229)은 밧줄이 소모형이 아니게 되면서 통째로 제거됐다 — 밧줄 검거엔 회수할 자원이 없다. (#369)
}
