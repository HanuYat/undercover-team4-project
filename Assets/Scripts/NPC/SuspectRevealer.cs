using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 제보 전화로 예비 용의자를 한 명씩 수배 공개한다. (#102)
///
/// <see cref="CriminalAssigner"/>에서 분리한 이유는 구동 주체가 다르기 때문이다 — 배정은 라운드
/// 시작에 스포너 완료 신호로 <b>한 번</b> 돌지만, 공개는 라운드 내내 <see cref="TipCallPhone"/>이
/// 전화를 받을 때마다 <b>한 명씩</b> 부른다. 소비자도 갈린다: 배정 결과는 외형·인명부·라운드가
/// 읽고, 공개 API는 전화기만 쓴다.
///
/// 예비 용의자 명단은 소유하지 않고 <b>빌려 본다</b> — 명단을 채우는 것은 배정 쪽 일이고, 여기서는
/// "다음에 누구를 올릴 수 있나"만 판단한다. 같은 List 인스턴스를 참조하므로 재배정도 그대로 보인다.
///
/// 현상금 총합도 소유하지 않는다 — 배정과 승격이 함께 더하는 값이라 소유자를 하나로 두고(<see
/// cref="CriminalAssigner.TotalAssignedBounty"/>) 여기서는 증감분만 돌려준다.
/// </summary>
public sealed class SuspectRevealer
{
    // 이번 라운드의 예비 용의자 — 배정 쪽이 채우고 여기서는 읽기만 한다(같은 인스턴스 참조).
    private readonly IReadOnlyList<NpcController> m_suspects;

    // 승격 시 진범 몫으로 다시 뽑을 규칙 — 대기 중에는 시민 가중치·오검거 금액이 들어 있다.
    private readonly float m_compliantWeight;
    private readonly float m_fleeWeight;
    private readonly float m_resistWeight;
    private readonly int m_bountyMin;
    private readonly int m_bountyMax;

    public SuspectRevealer(
        IReadOnlyList<NpcController> suspects,
        float compliantWeight,
        float fleeWeight,
        float resistWeight,
        int bountyMin,
        int bountyMax
    )
    {
        m_suspects = suspects;
        m_compliantWeight = compliantWeight;
        m_fleeWeight = fleeWeight;
        m_resistWeight = resistWeight;
        m_bountyMin = bountyMin;
        m_bountyMax = bountyMax;
    }

    /// <summary>
    /// 아직 공개되지 않은 예비 용의자가 남아 있는가 — 전화를 계속 걸지의 기준 (#102 설계 결정 5).
    /// 연행·끌기 중인 대상도 '남아 있다'로 센다: 곧 판정되면 IsDelivered로 자동으로 빠지고,
    /// 석방되면 다시 승격 후보가 된다. 여기서 빼면 마지막 예비 용의자를 끌고 가는 동안 울린
    /// 전화 하나 때문에 그 라운드 전화가 영영 끊긴다.
    /// </summary>
    public bool HasPending
    {
        get
        {
            for (int i = 0; i < m_suspects.Count; i++)
                if (IsPending(m_suspects[i]))
                    return true;
            return false;
        }
    }

    /// <summary>
    /// 대기 중인 예비 용의자 1명을 수배로 공개한다 — 제보 전화를 받았을 때 호출한다. (#102)
    /// 성공하면 true. 지금 승격 가능한 대상이 없으면 false — 그 전화 한 번을 놓친 것일 뿐이므로
    /// 호출자는 다음 수신을 그대로 예약하면 된다 (풀 소진 판정은 <see cref="HasPending"/>이 따로 한다).
    /// </summary>
    /// <param name="bountyDelta">
    /// 현상금 총합에 더해야 할 증감분 — 대기 시 금액을 빼고 진범 금액을 더한 값. 실패 시 0.
    /// 총합의 소유자가 반영한다(<see cref="CriminalAssigner"/>).
    /// </param>
    public bool TryPromoteNext(out int bountyDelta)
    {
        bountyDelta = 0;

        AppearanceAssigner appearance = App.Game.Appearance;
        if (appearance == null)
        {
            // 몽타주를 발행하지 못하면 수배 리스트에 뜨지 않는다 — IsCriminal만 켜면
            // 아무도 모르는 진범이 생기므로, 켜기 전에 막는다
            Debug.LogWarning("SuspectRevealer: AppearanceAssigner를 찾지 못해 승격할 수 없다");
            return false;
        }

        NpcController npc = FindNextPromotable();
        if (npc == null)
            return false;

        CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();
        identity.SetCriminal(true);

        // 오검거로 이미 한 번 판정된 대상일 수 있다 — 표식을 지워야 다시 잡아 인계했을 때
        // '첫 인계'로 잡혀 검거 수가 정상 누적된다(IsFirstDelivery, #358). 탈옥 재검거(#231)가
        // ClearDelivered를 부르는 것과 같은 이유다.
        npc.ClearDelivered();

        // 예비 용의자는 시민 가중치로 뽑혀 있다 — 범인 가중치로 다시 뽑는다.
        // Reaction은 서버 전용이라 바꿔도 플레이어에게 티가 나지 않는다 (#102 설계 결정 6).
        // 이미 반응 중이면 유형만 바뀌고 진행 중인 반응은 유지된다 — CanStartReaction이 막는다 (#400)
        identity.AssignReaction(ReactionRoll.Roll(m_compliantWeight, m_fleeWeight, m_resistWeight));

        // 현상금도 진범 몫으로 다시 배정한다 (#395) — 대기 중에는 오검거/위조 기준 금액이 들어 있어,
        // 그대로 두면 승격된 진범이 0원이나 소액으로 잡힌다. 재검거 리롤은 생기지 않는다: 승격 대상은
        // IsCriminal이 꺼진 개체뿐이라(FindNextPromotable) 한 번 승격된 NPC는 다시 이 경로를 타지 않는다.
        int promotedBounty = BountyRoll.Roll(m_bountyMin, m_bountyMax);
        bountyDelta = promotedBounty - identity.Bounty; // 대기 시 금액을 빼고 새 금액을 더한다
        identity.AssignBounty(promotedBounty);

        // 라운드 시작에 보관해 둔 몽타주를 그대로 발행한다 — WantedListManager가 이 이벤트로
        // NetworkList 추가와 TotalWanted++ 를 한다(기존 경로 재사용)
        appearance.RevealMontage(npc);

        CitizenProfile profile = identity.Profile;
        Debug.Log(
            $"[제보 전화] 수배 공개: {(profile != null ? profile.CitizenName : npc.name)} ({identity.Reaction}, 현상금 {promotedBounty}원)"
        );
        return true;
    }

    /// <summary>미공개 예비 용의자인가 — 살아 있고, 아직 공개 전이고, 지금 잡을 수 있다. (#102 · #392)</summary>
    private static bool IsPending(NpcController npc)
    {
        // 디스폰·파괴된 대상은 Unity null로 잡힌다 — IsSpawned는 오프라인에서 항상 false라 쓸 수 없다
        if (npc == null)
            return false;

        CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();
        if (identity == null || identity.IsCriminal)
            return false;

        // 유치장에 수감된 대상만 뺀다 — 미공개 상태에서 위조범으로 판정돼 갇힌 개체다. 수배로 올려도
        // 본부 안에 있어 찾을 것이 없고, 이미 위조 현상금으로 정산에 계상돼 있다.
        //
        // 오검거로 판정된 대상은 뺐다가 되살렸다 (#392). 예전에는 IsDelivered를 영구 제외했는데
        // 그 근거("판정이 끝난 대상은 아무도 못 잡는 유령 항목이 된다", #230)가 더 이상 맞지 않는다:
        // 오검거당한 시민은 죽지 않고 원한 구역(Detained)에서 대기하다 추격대(Chasing)로 나가며,
        // 재판정도 허용된다(#358 — 다시 끌어와 인계하면 판정된다). 즉 잡을 수 있는 대상인데 승격만
        // 막고 있었고, 그 탓에 미공개 용의자를 오검거로 태울 때마다 풀이 영구히 줄어 제보 전화가
        // 조용히 죽었다.
        return npc.CurrentState != NpcState.Jailed;
    }

    /// <summary>지금 당장 승격시킬 수 있는 첫 후보. 없으면 null. (#102 설계 §3 가드)</summary>
    private NpcController FindNextPromotable()
    {
        for (int i = 0; i < m_suspects.Count; i++)
        {
            NpcController npc = m_suspects[i];
            if (!IsPending(npc))
                continue;

            // 연행·끌기 중 — 곧 판정될 대상이라 등록 직후 사라진다. 건너뛰되 풀 소진으로는 세지 않는다
            // (FindEscorterOf는 연행과 밧줄 끌기를 둘 다 본다, #269)
            if (PlayerEscorter.FindEscorterOf(npc) != null)
                continue;

            return npc;
        }
        return null;
    }
}
