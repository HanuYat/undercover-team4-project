using UnityEngine;

/// <summary>
/// 누운 자세(기절)일 때 몸통 캡슐을 함께 눕힌다. (#363)
///
/// 프리팹의 CapsuleCollider는 서 있는 몸(세로 2m)에 맞춰져 있고 코드가 건드리는 곳이 없었다 —
/// 테이저에 맞아 누우면 모델만 바닥에 깔리고 판정은 원래 서 있던 허공에 남아, <b>보이는 몸을 겨냥하면
/// 기하학적으로 빗나갔다.</b> 상호작용 레이(PlayerInteractor)·테이저 조준(Taser.EvaluateAim)·
/// 윤곽선(InteractionFeedback)이 모두 이 콜라이더를 맞히는 것으로 대상을 정하므로,
/// 수갑 채우기·밧줄 묶기가 통째로 어긋나 있었다.
///
/// 상태(NpcState.Stunned)가 아니라 <see cref="NpcAnimationDriver.IsProne"/>(표현 계층의 실제 자세)를
/// 따라간다 — 일어나는 모션(#269) 동안에도 FSM 상태는 Stunned라, 상태를 기준으로 삼으면 이미 일어선
/// 몸에 누운 콜라이더가 남는다. 밧줄에 다시 묶여 도로 눕는 경우(#269)도 드라이버가 함께 되돌린다.
///
/// 네트워크 무관 — 구독하는 이벤트가 동기화를 거쳐 모든 피어에서 발생하므로(#56) 서버·클라 어디서든
/// 같은 순간에 같은 모양이 된다. 클라이언트가 자기 물리로 조준을 판정해도 서버 판정과 어긋나지 않는다.
/// </summary>
[RequireComponent(typeof(CapsuleCollider))]
[RequireComponent(typeof(NpcAnimationDriver))]
public class NpcProneCollider : MonoBehaviour
{
    // 누운 캡슐이 향하는 축 — 로컬 Z(CapsuleCollider.direction 규약: 0=X, 1=Y, 2=Z).
    // 뒤로 넘어지는 클립이라 몸이 정확히 앞뒤 축에 눕는다(머리가 -Z). 루트가 회전해도 로컬 축이라 함께 돈다.
    private const int k_proneDirection = 2;

    // 아래 기본값은 실측이다 — 기절 모션(HumanM@Knockdown01 - Ground)을 NPC 프리팹에 샘플링해
    // 스킨 메시 AABB를 루트 로컬로 잰 결과: z -0.94~+0.74(길이 1.68, 머리가 -Z), y 0~0.47, x ±0.83.
    // 벌어진 팔(x ±0.83)까지 덮지 않는 것은 서 있는 캡슐(r=0.4)이 T포즈 팔을 덮지 않는 것과 같은 기준이다 —
    // 캡슐은 '몸통'이고, 팔까지 넣으면 옆 사람을 겨눠도 이 NPC가 잡힌다.
    [Header("누운 자세 캡슐 (#363 — 기본값은 기절 모션 실측)")]
    [Tooltip("누웠을 때 캡슐 반지름(m) — 바닥에 닿게 중심 y와 같은 값을 쓴다")]
    [SerializeField]
    private float m_proneRadius = 0.3f;

    [Tooltip("누웠을 때 캡슐 길이(m) — 머리끝~발끝. 실측 1.68m에 머리·발끝 여유를 조금 더한 값")]
    [SerializeField]
    private float m_proneHeight = 1.8f;

    [Tooltip("누웠을 때 캡슐 중심(로컬) — 머리가 -Z라 몸 중심이 뒤로 밀린다")]
    [SerializeField]
    private Vector3 m_proneCenter = new Vector3(0f, 0.3f, -0.1f);

    private CapsuleCollider m_capsule;
    private NpcAnimationDriver m_driver;

    // 서 있을 때 값 — 프리팹 초기값에서 캡처해 그대로 되돌린다 (PlayerCrouch가 서기 높이를 캡처하는 것과 같은 관례).
    // 상수로 박지 않는 이유: 프리팹마다 몸집이 달라질 수 있고, 그때 복원값만 조용히 어긋나면 찾기 어렵다.
    private float m_standRadius;
    private float m_standHeight;
    private int m_standDirection;
    private Vector3 m_standCenter;

    private void Awake()
    {
        m_capsule = GetComponent<CapsuleCollider>();
        m_driver = GetComponent<NpcAnimationDriver>();

        m_standRadius = m_capsule.radius;
        m_standHeight = m_capsule.height;
        m_standDirection = m_capsule.direction;
        m_standCenter = m_capsule.center;
    }

    private void OnEnable()
    {
        m_driver.OnProneChanged += Apply;
        // 늦게 접속한 클라: 이미 누워 있는 NPC의 캡슐을 즉시 맞춘다.
        // (이벤트는 '변화' 시에만 오므로 구독 시점에 한 번 맞춰줘야 한다 — PlayerCrouch.OnNetworkSpawn과 같은 이유)
        Apply(m_driver.IsProne);
    }

    private void OnDisable()
    {
        m_driver.OnProneChanged -= Apply;
    }

    // 눕기/서기는 즉시 전환한다. CapsuleCollider.direction은 축 하나를 고르는 값이라 중간 각도가 없어
    // 애초에 블렌딩할 수 없고(PlayerCrouch의 높이 블렌딩과 다른 점), 눕는 순간은 모션도 스냅이라 맞는다.
    // 일어나는 구간(약 0.6초)만 콜라이더가 모션보다 앞서 서지만, 곧 도주로 넘어가는 짧은 구간이라 둔다.
    private void Apply(bool prone)
    {
        m_capsule.radius = prone ? m_proneRadius : m_standRadius;
        m_capsule.height = prone ? m_proneHeight : m_standHeight;
        m_capsule.direction = prone ? k_proneDirection : m_standDirection;
        m_capsule.center = prone ? m_proneCenter : m_standCenter;
    }
}
