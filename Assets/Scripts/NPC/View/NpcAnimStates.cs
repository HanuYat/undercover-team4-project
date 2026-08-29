using UnityEngine;

/// <summary>
/// NPC Animator의 <b>번호 대장</b> — State(int) 파라미터에 실리는 값과 파라미터 이름을 모아 둔다. (#502)
///
/// <b>왜 한 곳에 모으는가.</b> 이 번호들은 두 곳이 함께 본다: 모션을 넣는 런타임(부품들)과
/// 컨트롤러를 만드는 에디터(<c>NpcAnimatorControllerBuilder</c>)다. 흩어지면 컨트롤러와 코드가
/// 어긋나고, 그 어긋남은 컴파일이 아니라 <b>화면에서 엉뚱한 모션</b>으로만 드러난다.
///
/// <b>번호 규칙.</b> 0~99는 <see cref="NpcState"/> enum 값이 그대로 Animator 상태 번호다
/// (Idle=0, Walk=1...). 100번대는 FSM 상태가 아닌 모션 전용 번호다 — 해제·기상·제압 전환처럼
/// "한 FSM 상태 안의 한 페이즈"라 상태를 늘릴 수 없는 것들이다. 그 페이즈 구분은 서버 FSM
/// 내부값이라 클라이언트가 모르므로, enum이 앞으로 자라도 겹치지 않을 만큼 떨어뜨려 둔다.
///
/// <b>번호를 새로 고를 때:</b> 같은 컨트롤러의 Any State 조건은 번호가 겹치면 우선순위에 밀려
/// 엉뚱한 모션이 재생된다 — 반드시 여기 없는 번호를 쓸 것.
/// </summary>
public static class NpcAnimStates
{
    /// <summary>모션을 고르는 Animator 파라미터 이름 — Any State 전이 조건이 전부 이 값을 본다.</summary>
    public const string k_stateParam = "State";

    /// <summary>
    /// 스윙 1회에 재생할 단발 클립 번호를 고르는 Animator 파라미터 이름. (#220)
    /// Attack 상태의 블렌드 트리가 이 값으로 클립을 고른다.
    /// </summary>
    public const string k_swingVariantParam = "SwingVariant";

    public static readonly int s_stateHash = Animator.StringToHash(k_stateParam);
    public static readonly int s_swingVariantHash = Animator.StringToHash(k_swingVariantParam);

    /// <summary>
    /// 자물쇠 해제 시작(Begin) 모션. (#261)
    /// 해제는 FSM 상태가 아니라 침입(Intruding) 안의 한 페이즈라 전용 번호를 쓴다.
    /// </summary>
    public const int k_unlockingBegin = 100;

    /// <summary>
    /// 자물쇠 해제 반복(Loop) 모션. (#261)
    /// Begin과 번호를 나눠 갖는 것이 핵심이다 — 하나로 두면 Loop에 들어간 뒤에도 Any State 조건이
    /// 계속 참이라 매번 Begin으로 되돌아가 동작이 무한히 다시 시작된다
    /// (canTransitionToSelf는 자기 자신으로의 재진입만 막는다).
    /// </summary>
    public const int k_unlockingLoop = 101;

    /// <summary>
    /// 기절에서 일어나는(StandUp) 모션. (#269)
    /// 일어나는 동안에도 FSM 상태는 여전히 Stunned라 전용 번호가 필요하다.
    /// </summary>
    public const int k_standUp = 102;

    /// <summary>
    /// 저항형 제압 전환(그로기) 모션. (#332)
    /// 두들겨 맞아 제압된 저항형은 곧바로 고개 숙인 대기 자세로 스냅되는 대신 서서 헤롱거리다 가라앉는다.
    /// (103은 과거 괴한 윈드업이 쓰다 제거돼 현재는 빈 번호)
    /// </summary>
    public const int k_subdueGroggy = 105;

    /// <summary>
    /// 도주형 제압 전환(구르기) 모션. (#332)
    /// 달리다 붙잡힌 관성을 표현한다 — 태클당해 구르고 스스로 일어난 뒤 대기 자세로 이어진다
    /// (클립 끝 프레임이 완전 기립이라 Captured 자세와 크로스페이드로 자연 연결).
    /// </summary>
    public const int k_subdueRoll = 106;

    /// <summary>
    /// 좌석에 앉기 시작(Begin) 모션. (#462)
    /// <b>현재 아무도 지정하지 않는다</b> — 좌석이 폐기되면서(#537) 수감자는 배치 지점에 서 있는다.
    /// 컨트롤러에는 상태가 남아 있어 번호를 비워 두지 않는다(다른 모션이 이 번호를 재사용하면
    /// 죽은 전이가 되살아난다).
    /// </summary>
    public const int k_sitBegin = 107;

    /// <summary>앉은 자세(Loop) 모션. (#462) <see cref="k_sitBegin"/>과 같이 현재 미사용.</summary>
    public const int k_sitLoop = 108;
}
