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

    /// <summary>외형 특징 조합(#74) — 몽타주 부합 판정의 기준. AppearanceAssigner가 채워준다.</summary>
    public AppearanceProfile Appearance { get; private set; } = AppearanceProfile.Unassigned;

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
}
