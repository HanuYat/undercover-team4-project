using UnityEngine;
using UnityEngine.AI;

public partial class NpcController
{
    // ---- 밧줄 끌기 (#269) ----

    private bool m_roped;

    /// <summary>밧줄로 묶여 끌리는 중인가 — 서버 권위. 묶인 동안 기절 타이머가 정지된다(NpcStunnedState).</summary>
    public bool IsRoped => m_roped;

    /// <summary>밧줄 끌기 시작 — 기절한 대상을 PlayerEscorter가 서버에서 호출. 위치를 끄는 플레이어가 직접 제어하므로
    /// NavMeshAgent를 끈다(켜져 있으면 에이전트가 위치를 도로 잡아당긴다). 상태는 Stunned를 그대로 유지한다.
    /// 끈 플레이어를 위협으로 기억한다 — 놓아준 뒤 깨어나면 그 플레이어에게서 도망친다.</summary>
    public void StartRopeDrag(Transform dragger = null)
    {
        if (IsSpawned && !IsServer)
            return;

        if (dragger != null)
            ThreatTarget = dragger;
        m_roped = true;
        if (m_agent != null && m_agent.enabled)
            m_agent.enabled = false;
    }

    /// <summary>밧줄 끌기 해제 — 에이전트를 되살려 NavMesh로 복귀(Warp)시킨다. 안 하면 이후 이동·상태가 깨진다.
    /// NPC는 기절 상태를 이어가다 스스로 깨어난다.</summary>
    public void StopRopeDrag()
    {
        if (IsSpawned && !IsServer)
            return;

        m_roped = false;
        if (m_agent != null)
        {
            m_agent.enabled = true;
            if (UnityEngine.AI.NavMesh.SamplePosition(transform.position, out UnityEngine.AI.NavMeshHit hit, 2f, UnityEngine.AI.NavMesh.AllAreas))
                m_agent.Warp(hit.position);
        }
    }

    /// <summary>끌리는 동안 위치·회전을 설정한다 — 끄는 플레이어(PlayerEscorter)가 매 서버 프레임 호출.
    /// NetworkTransform이 전 클라에 복제하므로 원격 피어에서도 끌리는 위치가 맞는다.</summary>
    public void ServerDragTo(Vector3 position, Quaternion rotation)
    {
        if (IsSpawned && !IsServer)
            return;
        if (!m_roped)
            return;

        transform.SetPositionAndRotation(position, rotation);
    }

}
