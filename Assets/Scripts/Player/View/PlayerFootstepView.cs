using UnityEngine;

/// <summary>
/// 발소리·점프음 재생 (#483) — 걸음/뜀 한 걸음, 이륙, 착지.
///
/// <b>순수 표현이고 동기화가 없다.</b> 전 피어가 <b>모든 플레이어 인스턴스</b>에서 이 컴포넌트를 돌리고,
/// 각자 "이 몸이 지난 프레임 대비 얼마나 움직였나"만 보고 소리를 낸다 — 내 캐릭터든 남의 캐릭터든
/// 판단 재료가 같다. 원격 피어의 위치는 NetworkTransform이 실어다 주고 공중 여부는
/// <see cref="PlayerJump.IsAirborne"/>이 이미 서버 권위로 전파하므로, 발소리를 위해 RPC를 새로 뚫을
/// 이유가 없다 (<c>BombWheelSpin</c>이 바퀴를 굴리는 방식과 같은 결).
///
/// <b>거리 기준으로 센다.</b> 시간(초당 N걸음)으로 재면 속도가 바뀔 때 보폭이 따라오지 않아 걸음이
/// 미끄러지고, 밀려나거나 끌려갈 때 제자리에서 발소리가 난다. 실제로 간 거리로 세면 그 어긋남이 없다.
///
/// <b>3D로 낸다</b> — 남의 발소리가 어디서 나는지가 곧 정보다(뒤에 누가 붙었는지, 본부가 못 보는
/// 구역에서 누가 움직이는지). 그래서 <see cref="SoundManager.PlaySfx2D"/>가 아니라
/// <see cref="SoundManager.PlaySfxAt"/>를 쓴다.
/// </summary>
[RequireComponent(typeof(PlayerJump))]
public class PlayerFootstepView : MonoBehaviour
{
    [Header("보폭 (인스펙터 조절)")]
    [Tooltip("걸을 때 한 걸음의 거리(m) — 짧을수록 발소리가 잦아진다")]
    [Min(0.1f)]
    [SerializeField]
    private float m_walkStride = 2f;

    [Tooltip("뛸 때 한 걸음의 거리(m). 걷기보다 길게 두면 속도가 붙어도 소리가 덜 몰린다")]
    [Min(0.1f)]
    [SerializeField]
    private float m_runStride = 2.6f;

    [Tooltip("이 속도(m/s)를 넘으면 뛰는 것으로 보고 뜀 발소리를 낸다 — 걷기 5·달리기 8 사이에 둘 것")]
    [SerializeField]
    private float m_runSpeedThreshold = 6.5f;

    private PlayerJump m_jump;
    private PlayerHealth m_health;

    private Vector3 m_lastPosition;
    private float m_travelled; // 마지막 발소리 이후 실제로 간 수평 거리
    private bool m_wasAirborne;

    private void Awake()
    {
        m_jump = GetComponent<PlayerJump>();
        m_health = GetComponent<PlayerHealth>();
    }

    private void OnEnable()
    {
        m_lastPosition = transform.position;
        m_travelled = 0f;
        m_wasAirborne = m_jump.IsAirborne;
    }

    private void Update()
    {
        Vector3 delta = transform.position - m_lastPosition;
        m_lastPosition = transform.position;
        delta.y = 0f; // 오르내림은 보폭이 아니다 — 경사·계단에서 걸음이 빨라지지 않게 수평만 센다

        bool airborne = m_jump.IsAirborne;
        if (airborne != m_wasAirborne)
        {
            m_wasAirborne = airborne;
            m_travelled = 0f; // 뜬 순간과 디딘 순간 모두 보폭을 새로 시작한다
            Play(airborne ? EAudioClip.JumpTakeoff : EAudioClip.JumpLand);
            return;
        }

        if (airborne)
            return; // 공중에는 디딜 바닥이 없다

        // 다운·기절·매달린 상태에서 나는 움직임은 제 발로 걷는 것이 아니다(끌려가는 중 포함) —
        // 무력화 게이트는 저장소 관례대로 PlayerHealth.IsTargetable 하나로 본다.
        if (m_health != null && !m_health.IsTargetable)
        {
            m_travelled = 0f;
            return;
        }

        float distance = delta.magnitude;
        if (distance <= 0f)
            return;

        m_travelled += distance;

        // 속도는 이 프레임의 이동량에서 바로 얻는다 — 입력이 아니라 실제 이동이 기준이라
        // 밀려나거나 느려진 상태(부상·과적)에서도 소리가 실제 움직임을 따라간다.
        float speed = distance / Mathf.Max(Time.deltaTime, 0.0001f);
        bool running = speed > m_runSpeedThreshold;
        float stride = running ? m_runStride : m_walkStride;

        if (m_travelled < stride)
            return;

        m_travelled -= stride; // 넘친 만큼은 다음 걸음으로 넘긴다 — 빠를수록 걸음이 밀리지 않게
        Play(running ? EAudioClip.FootstepRun : EAudioClip.FootstepWalk);
    }

    // 매니저가 없는 구성(부트스트랩 없는 씬 직접 Play)에서는 조용히 넘어간다 (R8 관례).
    private void Play(EAudioClip id) => App.Sound?.PlaySfxAt(id, transform.position);
}
