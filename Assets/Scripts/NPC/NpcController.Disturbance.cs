using UnityEngine;

public partial class NpcController
{
    // ---- 패닉 (#81) ----

    // 소란 전파용 공유 버퍼 — 서버(또는 오프라인)에서만 쓰므로 공유해도 안전하다
    private static readonly Collider[] s_disturbanceBuffer = new Collider[64];

    private float m_nextDisturbancePulseTime;

    /// <summary>
    /// 소란 발생 — position 반경 radius 안의 배회 NPC를 전부 패닉시킨다. (#81)
    /// 저항·도주 NPC의 펄스가 호출하며, 돌발 이벤트(GDD 6-4 후속 이슈)도 이 API로 소란을 일으킨다.
    /// 서버(또는 오프라인)에서 호출할 것 — 클라이언트에서 불려도 각 NPC의 EnterPanic이 무시한다.
    /// </summary>
    public static void BroadcastDisturbance(Vector3 position, float radius)
    {
        int hitCount = Physics.OverlapSphereNonAlloc(position, radius, s_disturbanceBuffer);
        for (int i = 0; i < hitCount; i++)
        {
            NpcController npc = s_disturbanceBuffer[i].GetComponentInParent<NpcController>();
            if (npc != null)
                npc.EnterPanic(position);
        }
    }

    /// <summary>
    /// 패닉 진입/갱신 — 배회 중(Idle/Walk)일 때만 전이한다.
    /// 검거(채널링·체포·연행)는 소란이 아니므로 이 메서드를 부르지 않고,
    /// 체포·연행·기절·반응 중(도주/저항)인 NPC는 여기서 걸러진다.
    /// 이미 패닉 중이면 소란 지점·진정 타이머만 갱신한다 (도주 방향은 다음 지점 갱신 때 반영).
    /// </summary>
    public void EnterPanic(Vector3 disturbancePosition)
    {
        if (IsSpawned && !IsServer)
            return;

        NpcState state = m_stateMachine.CurrentState; // 서버 진실값 — 동기화 지연 없이 판정
        if (state == NpcState.Panic)
        {
            PanicSource = disturbancePosition;
            LastDisturbedTime = Time.time;
            return;
        }

        if (state != NpcState.Idle && state != NpcState.Walk)
            return;

        PanicSource = disturbancePosition;
        LastDisturbedTime = Time.time;
        m_stateMachine.ChangeState(NpcState.Panic);
    }

}
