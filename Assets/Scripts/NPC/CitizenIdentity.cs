using UnityEngine;

/// <summary>
/// NPC 한 명의 신원 — 런타임에 배정되는 시민 프로필과 범인 여부를 들고 있다. (이슈 #38)
/// 배정은 CriminalAssigner가 담당하고, 스캐너 UI(#39)·진범 판정(#41) 등은 이 컴포넌트를 읽는다.
/// </summary>
public class CitizenIdentity : MonoBehaviour
{
    /// <summary>이 NPC의 시민 프로필. 스폰 직후 CriminalAssigner가 채워준다.</summary>
    public CitizenProfile Profile { get; private set; }

    /// <summary>실제 범인 여부 — 진범 판정(#41)의 정답 기준.</summary>
    public bool IsCriminal { get; private set; }

    /// <summary>
    /// 거수자 여부 — 위조·기타 혐의가 있는 무고 시민. 도주/저항한 채 검거되면 경범죄(공무집행방해)로 성립한다. (#78)
    /// 혐의 없는 일반 시민은 도주/저항하더라도 이 플래그가 false라 경범죄가 아닌 오검거로 판정된다.
    /// </summary>
    public bool IsSuspicious { get; private set; }

    /// <summary>외형 특징 조합(#74) — 몽타주 부합 판정의 기준. AppearanceAssigner가 채워준다.</summary>
    public AppearanceProfile Appearance { get; private set; } = AppearanceProfile.Unassigned;

    /// <summary>검거 반응 유형(#76) — 수갑 채널링 성공 순간의 반응. CriminalAssigner가 배정한다.</summary>
    public ReactionType Reaction { get; private set; } = ReactionType.Compliant;

    /// <summary>프로필과 범인 여부를 배정한다. CriminalAssigner 전용.</summary>
    public void AssignProfile(CitizenProfile profile, bool isCriminal)
    {
        Profile = profile;
        IsCriminal = isCriminal;
    }

    /// <summary>외형 특징 조합을 배정한다. AppearanceAssigner 전용.</summary>
    public void AssignAppearance(AppearanceProfile appearance)
    {
        Appearance = appearance;
    }

    /// <summary>검거 반응 유형을 배정한다. CriminalAssigner 전용.</summary>
    public void AssignReaction(ReactionType reaction)
    {
        Reaction = reaction;
    }

    /// <summary>거수자 여부를 배정한다. CriminalAssigner 전용. (#78)</summary>
    public void AssignSuspicious(bool isSuspicious)
    {
        IsSuspicious = isSuspicious;
    }
}
