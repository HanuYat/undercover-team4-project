using UnityEngine;

/// <summary>
/// NPC 외형 조합(AppearanceProfile) 공급자 — 몽타주·매칭·수배UI 등 소비 측이
/// 생산 방식(프롭 조합 / 모델 카탈로그)을 몰라도 되게 하는 공통 접점. (#221)
/// </summary>
public interface IAppearanceProfileSource
{
    /// <summary>현재 적용된 외형 조합. 배정 전에는 AppearanceProfile.Unassigned.</summary>
    AppearanceProfile Profile { get; }
}
