/// <summary>
/// 모션 우선순위 — 값이 클수록 이긴다. (#502)
///
/// <b>왜 값으로 두는가.</b> 예전에는 이 순서가 <c>Update</c> 안 블록 배치와 조기 반환으로만
/// 표현돼 있었다. 코드에 "이 블록은 끌림 조기 반환보다 앞이어야 한다"는 주석이 붙어 있었을 만큼
/// 위태로웠고, 새 모션을 붙이는 사람은 어디에 끼워야 하는지 627줄을 읽어야 알 수 있었다.
/// 부품으로 나누면서 각자 Animator에 쓰게 두면 <b>마지막에 쓴 쪽이 이기는</b> 구조가 되어
/// 순서가 아예 우연에 맡겨진다 — 그래서 순서를 값으로 꺼내고 결정은 한 곳에서 한다
/// (<see cref="NpcAnimationDriver"/>가 매 프레임 하나를 골라 쓰는 유일한 지점이다).
/// </summary>
public enum ENpcMotionPriority
{
    /// <summary>
    /// 속도 기반 로코모션 — 연행 걷기·저항 추격·해제·오검거 페널티. (#97/#254/#261/#277~)
    /// 기준 상태(base)만으로는 못 가르는 "한 상태 안의 이동/정지"를 실제 속도로 나눈다.
    /// </summary>
    Locomotion = 1,

    /// <summary>
    /// 단발 펄스 — 스윙·기상·제압 전환. (#220/#269/#332)
    /// 로코모션보다 위다: 스윙 중에 걷기 판정이 바뀌어도 스윙 클립이 끊기면 안 된다.
    /// </summary>
    OneShot = 2,
}

/// <summary>
/// "지금 이 번호를 이 우선순위로 원한다"만 보고하는 부품. (#502)
/// 부품은 Animator를 직접 만지지 않는다 — 쓰는 것은 <see cref="NpcAnimationDriver"/> 한 곳뿐이다.
/// </summary>
public interface INpcMotionSource
{
    /// <summary>이 부품이 내는 제안의 우선순위 — 드라이버가 이 값 순으로 물어본다.</summary>
    ENpcMotionPriority Priority { get; }

    /// <summary>
    /// 원하는 Animator 상태 번호. 원하는 것이 없으면 false — 그러면 더 낮은 우선순위
    /// (없으면 기준 상태 모션)로 내려간다.
    /// </summary>
    bool TryGetMotion(out int animState);
}
