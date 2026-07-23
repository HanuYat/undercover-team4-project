using Unity.Netcode;
using UnityEngine;

public partial class NpcController
{
    // ---- 유치장 (#228) ----

    /// <summary>
    /// 수감 — 인계존 판정에서 진범·경범죄로 확정된 NPC를 유치장으로 보낸다. (CustodyRouter 경유, GDD 7-2)
    /// cell(수용 지점)까지 스스로 걸어가 그 자리에 수용된다. cell이 null이면 그 자리에서 수용된 것으로 처리한다.
    /// </summary>
    public void SendToJail(Transform cell)
    {
        // FSM 전이는 서버 권위 — StartEscort와 동일하게 클라이언트 호출은 무시한다
        if (IsSpawned && !IsServer)
            return;

        EscortTarget = null; // 판정 시점에 연행은 이미 풀렸지만, 참조가 남아 있으면 여기서 끊는다
        JailCell = cell;
        m_stateMachine.ChangeState(NpcState.Jailed);
    }

    // ---- 수갑 소모·반환 (#229) ----

    /// <summary>
    /// 이 NPC에 수갑이 채워져 있는가 — 체포 성공 시 잡은 플레이어에게서 옮겨온 수갑(PlayerLoadout.ConsumeHandcuffsTo).
    /// 재연행 시 수갑을 또 소모하지 않도록 PlayerEscorter가 이걸로 첫 연행 여부를 가른다. 부착 자식 기준.
    /// </summary>
    public bool HasHandcuffs => FindHeldHandcuffs() != null;

    // 채워진 수갑을 찾는다 — 없으면 null. 소모 시 NPC 루트 직속 자식으로 붙으므로(ConsumeHandcuffsTo)
    // 깊은 캐릭터 리그를 통째로 훑는 GetComponentInChildren 대신 루트 직속 자식만 본다(PlayerLoadout과 같은 패턴).
    private Handcuffs FindHeldHandcuffs()
    {
        for (int i = 0; i < transform.childCount; i++)
        {
            if (transform.GetChild(i).TryGetComponent(out Handcuffs cuffs))
            {
                return cuffs;
            }
        }

        return null;
    }

    /// <summary>
    /// 채워진 수갑을 발밑 바닥에 떨어뜨려 반환한다 — 인계존 판정 후 ArrestJudge.Judge가 호출. (#229)
    /// 판정 위치(현재 NPC 위치)에 놓이며, 부모에서 분리되는 순간 WorldItemPickup이 다시 월드 표시·줍기를 켠다.
    /// 서버(또는 오프라인)에서만. 수갑이 없으면(오검거 아닌 직접 스폰 테스트 등) 무동작.
    /// </summary>
    public void DropHandcuffs()
    {
        if (IsSpawned && !IsServer)
        {
            return;
        }

        Handcuffs cuffs = FindHeldHandcuffs();
        if (cuffs == null)
        {
            return;
        }

        NetworkObject cuffsNetworkObject = cuffs.NetworkObject;
        if (cuffsNetworkObject == null)
        {
            return;
        }

        // 부모(NPC)에서 분리 — 소유권은 커스터디 진입 때 이미 서버로 돌아와 있어 월드 상태 그대로다.
        cuffsNetworkObject.TrySetParent((Transform)null, true);
        cuffsNetworkObject.transform.position = transform.position;
    }
}
