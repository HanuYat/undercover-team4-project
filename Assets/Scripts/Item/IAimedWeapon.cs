using UnityEngine;

/// <summary>
/// 자기 사거리로 조준해 쏘거나 휘두르는 무기 — 조준 피드백을 <b>윤곽선이 아니라 크로스헤어</b>로 낸다. (#328/#363/#217)
///
/// 윤곽선의 기본 경로는 상호작용 레이(PlayerInteractor.Range, 3m)가 잡은 대상인데, 이 무기들은
/// 자기 사거리와 자기 판정이 따로 있어 그 기준과 어긋난다 — 테이저는 8m로 더 멀고, 진압봉은 2m로
/// 더 가깝다. 그대로 두면 "윤곽선은 떴는데 안 맞는"(진압봉) / "사거리 안인데 윤곽선이 없는"(테이저)
/// 상태가 생긴다. 그래서 NPC 윤곽선은 끄고, 명중 가능 여부를 크로스헤어 색으로만 알린다.
///
/// 겨냥한 몸이 통째로 빛나지 않는 편이 낫다는 판단도 있다 — 조준해서 맞히는 무기라 오조준의
/// 긴장이 남아야 한다 (#328).
///
/// <b>NPC 윤곽선만 끈다</b>는 점에 주의 (#363). 인명부·콘솔 같은 사격과 무관한 E 상호작용물은
/// 무기를 들고 있어도 평소대로 표시된다 — 예전에 통째로 끄던 것을 좁힌 것이 #363의 수정이다.
/// </summary>
public interface IAimedWeapon
{
    /// <summary>
    /// 지금 이 조준선으로 유효한 대상을 겨누고 있는가 — 오너 크로스헤어 색 예측용.
    /// </summary>
    /// <remarks>
    /// 구현은 <b>서버 판정과 같은 규칙</b>이어야 한다. 어긋나면 "크로스헤어는 켜졌는데 안 맞는"
    /// 상태가 되므로, 판정 함수를 따로 쓰지 말고 서버가 쓰는 것을 그대로 재사용할 것.
    ///
    /// 매 프레임 로컬 물리로 호출된다(InteractionFeedback.Update) — 부수효과 없는 순수 조회로
    /// 유지하고, 원점 검증·쿨다운처럼 서버에만 있는 상태는 보지 말 것. 서버 전용 상태를 섞으면
    /// 호스트와 원격 클라이언트의 크로스헤어가 서로 달라진다.
    /// </remarks>
    /// <param name="origin">조준 원점 — 보통 PlayerInteractor.AimOrigin.position (카메라).</param>
    /// <param name="direction">조준 방향 — 보통 PlayerInteractor.AimOrigin.forward.</param>
    bool HasValidAimTarget(Vector3 origin, Vector3 direction);
}
