using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 밧줄을 끄는 <b>대가</b> — 무게로 느려지고, 여럿이 함께 끌면 목줄에 걸려 못 간다. (#398)
///
/// <see cref="PlayerEscorter"/>에서 분리한 이유는 소비자가 다르기 때문이다: 연결 목록은 표시·검증·인계가
/// 읽지만, 여기 두 값은 <see cref="PlayerMovement"/>만 쓴다 — 이동 속도 배율과 속도 벡터 제한이다.
/// 밧줄이 "누구와 이어져 있나"의 문제라면, 이쪽은 "그래서 내가 얼마나 무겁고 얼마나 묶여 있나"다.
///
/// 연결 상태는 소유하지 않고 <b>빌려 읽는다</b> — 목록·끊김 거리·목줄 판정은 전부
/// <see cref="PlayerEscorter"/>가 단일 진실로 갖는다. 끊김 판정(서버)과 이동 제한(오너)이 같은 기준을
/// 봐야 하므로 <see cref="PlayerEscorter.IsLeashedTo"/>를 양쪽이 함께 쓴다.
///
/// 실행은 <see cref="PlayerEscorter"/>가 매 프레임 <see cref="ServerTickWeight"/>로 돌린다 —
/// 연결 정리가 먼저 끝나야 사라진 대상이 무게에 잡히지 않는다.
/// </summary>
[RequireComponent(typeof(PlayerEscorter))]
public class RopeDragLoad : NetworkBehaviour
{
    [Header("무게 페널티 — #398")]
    [Tooltip(
        "끌고 있는 무게 1당 깎이는 이동속도 비율 — 표준 무게(1.0) 1명을 혼자 끌면 이만큼 느려진다. 무게 자체는 NpcCommonConfig에서 추첨된다"
    )]
    [SerializeField]
    private float m_dragSlowPerWeight = 0.25f;

    [Tooltip(
        "무게 페널티 하한(배율) — 아무리 무거워도 이 아래로는 느려지지 않는다. 0으로 두면 이동이 완전히 막힐 수 있다"
    )]
    [SerializeField]
    private float m_minDragSpeedFactor = 0.35f;

    // 끌고 있는 무게로 깎인 이동속도 배율 — 서버(또는 오프라인) 진실. 매 프레임 다시 계산된다.
    private float m_dragSpeedFactor = 1f;

    // 위 값의 클라 사본 — 이동 권한이 오너라 배율도 오너가 적용해야 하고, 원격 피어의 걷기 애니메이션
    // 블렌드도 같은 기준 속도를 봐야 한다(PlayerAnimationDriver).
    // ⚠ 기본값 1 — 0으로 두면 세션 없는 오프라인 Play에서 배율 0이 되어 아예 못 움직인다.
    private readonly NetworkVariable<float> m_dragSpeedFactorSynced = new NetworkVariable<float>(
        1f
    );

    /// <summary>무게로 깎인 이동속도 배율(0~1) — 아무것도 끌지 않으면 1. 전 피어에서 유효. (#398)</summary>
    public float DragSpeedFactor =>
        IsSpawned && !IsServer ? m_dragSpeedFactorSynced.Value : m_dragSpeedFactor;

    private PlayerEscorter m_escorter;

    private PlayerEscorter Escorter
    {
        get
        {
            if (m_escorter == null)
                m_escorter = GetComponent<PlayerEscorter>();
            return m_escorter;
        }
    }

    /// <summary>
    /// 팽팽해진 밧줄이 허용하는 만큼으로 수평 입력 이동 속도(m/s)를 깎아 돌려준다 —
    /// <b>오너 로컬</b>, 매 프레임 <see cref="PlayerMovement"/>가 부른다. (#398)
    ///
    /// 반경은 <b>끊김 거리 ÷ 참가자 수</b>다. NPC가 합력으로 참가자들의 중점에 오므로
    /// (NpcController.TickRopeDrag) 각자의 거리가 곧 전체의 1/n이고, 각자 자기 반경만 지키면
    /// <b>합이 끊김 거리로 묶인다</b> — 2인이면 5m씩, 총 10m. 상대 위치를 몰라도 성립한다.
    /// </summary>
    public Vector3 ConstrainByTautRopes(Vector3 horizontalVelocity)
    {
        PlayerEscorter escorter = Escorter;
        if (escorter == null)
            return horizontalVelocity;

        int count = escorter.TetheredCount;
        for (int i = 0; i < count; i++)
        {
            NpcController npc = escorter.GetTetheredNpc(i);
            if (npc == null || !escorter.IsLeashedTo(npc))
                continue;

            Vector3 toNpc = npc.transform.position - transform.position;
            toNpc.y = 0f; // 끊김 판정·장력과 같은 기준 — 계단·경사에서 y차로 오작동하지 않게
            float distance = toNpc.magnitude;
            if (distance < 0.001f)
                continue;

            float radius = escorter.RopeBreakDistance / Mathf.Max(1, npc.DraggerCount);
            if (distance < radius)
                continue; // 아직 늘어져 있다 — 자유롭게 움직인다

            // 멀어지는 성분만 지운다. 안쪽·접선 방향은 열어 둬야 팽팽한 채로 원을 돌거나 되돌아올 수 있다.
            Vector3 outward = -toNpc / distance;
            float away = Vector3.Dot(horizontalVelocity, outward);
            if (away > 0f)
                horizontalVelocity -= outward * away;
        }

        return horizontalVelocity;
    }

    /// <summary>
    /// 끌고 있는 대상들에 자리 번호를 매기고, 같은 순회에서 무게 페널티를 계산한다. (#398)
    /// 서버(또는 오프라인) 전용 — <see cref="PlayerEscorter"/>가 연결 정리 <b>뒤에</b> 부른다.
    ///
    /// 자리 번호를 받은 NPC가 밧줄 방향에 수직으로 벌려 부채꼴이 된다. 없으면 전원이 내 발밑 한 점에
    /// 앵커돼 같은 지점으로 수렴하고, 벽 스윕이 사람은 장애물로 안 쳐서 (#313/#339) 서로 통과해
    /// 한 덩어리가 된다.
    /// 매 프레임 다시 도는 이유: 놓기·인계·끊김으로 구성이 수시로 바뀐다. n이 소지 밧줄 수(최대 3)라
    /// 비용이 없고, 참가자 이탈에 따른 페널티 재계산도 공짜로 따라온다.
    /// </summary>
    internal void ServerTickWeight()
    {
        IReadOnlyList<NpcController> tethered = Escorter.ServerTethered;

        int draggingCount = 0;
        for (int i = 0; i < tethered.Count; i++)
            if (tethered[i].IsDraggedBy(transform))
                draggingCount++;

        int slot = 0;
        float weightSum = 0f;
        for (int i = 0; i < tethered.Count; i++)
        {
            NpcController npc = tethered[i];
            if (!npc.IsDraggedBy(transform))
                continue; // 묶여만 있는(E로 놓아둔) 대상은 안 끌고 있으니 무게도 지지 않는다

            npc.SetDragSlot(slot, draggingCount);
            slot++;

            // 여러 명이 같은 대상을 함께 끌면 참가자 수로 나눠 진다(다인 완화식). 여러 명을 동시에
            // 끌 때의 무게 합산도 이 누적이 그대로 한다 — 상한이 슬롯이 아니라 무게 예산이 되는 지점.
            weightSum += npc.DragWeight / Mathf.Max(1, npc.DraggerCount);
        }

        SetDragSpeedFactor(
            Mathf.Clamp(1f - m_dragSlowPerWeight * weightSum, m_minDragSpeedFactor, 1f)
        );
    }

    // 배율과 동기화 값을 함께 갱신 — 서버(또는 오프라인)에서만 호출된다. (NpcController.SetRoped와 같은 관례)
    // 매 프레임 불려도 대역폭을 안 먹는다: NetworkVariable.Value 세터가 같은 값이면 스스로 조기 반환한다.
    private void SetDragSpeedFactor(float factor)
    {
        m_dragSpeedFactor = factor;
        if (IsSpawned && IsServer)
            m_dragSpeedFactorSynced.Value = factor;
    }
}
