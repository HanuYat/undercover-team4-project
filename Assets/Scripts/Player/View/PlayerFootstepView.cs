using UnityEngine;

/// <summary>
/// 발소리·점프음 재생 (#483) — 이동 중 계속 도는 발소리 루프 + 이륙·착지 원샷.
///
/// <b>순수 표현이고 동기화가 없다.</b> 전 피어가 <b>모든 플레이어 인스턴스</b>에서 이 컴포넌트를 돌리고,
/// 각자 "이 몸이 지난 프레임 대비 얼마나 움직였나"만 보고 소리를 낸다 — 내 캐릭터든 남의 캐릭터든
/// 판단 재료가 같다. 원격 피어의 위치는 NetworkTransform이 실어다 주고 공중 여부는
/// <see cref="PlayerJump.IsAirborne"/>이 이미 서버 권위로 전파하므로, 발소리를 위해 RPC를 새로 뚫을
/// 이유가 없다 (<c>BombWheelSpin</c>이 바퀴를 굴리는 방식과 같은 결).
///
/// <b>걸음마다 원샷을 내지 않고 루프를 켜고 끈다.</b> 쓰는 음원이 한 걸음이 아니라 <b>연속 보행음</b>
/// (걷기 50초·뜀 5초)이기 때문이다. 이런 클립을 보폭마다 재생하면 멈춘 뒤에도 남은 길이만큼 계속 걷는
/// 소리가 나고, 걸음이 겹쳐 쌓인다. 그래서 "움직이는 동안 켜 두고 멈추면 끄는" 방식으로 다룬다 —
/// 발이 닿는 타이밍은 클립 안에 이미 들어 있다.
///
/// <b>루프는 풀에서 빌리지 않고 자기 <see cref="AudioSource"/>로 낸다.</b> <see cref="SoundManager"/>의
/// 풀은 원샷 전용이라 소리가 몰리면 오래된 것을 뺏어 가고, 무엇보다 소스가 제자리에 서 있어 움직이는
/// 몸을 따라오지 못한다. 자식 오브젝트로 달아 두면 위치가 저절로 따라온다.
///
/// 이륙·착지는 짧은 단발음이라 그대로 풀에 맡긴다.
/// </summary>
[RequireComponent(typeof(PlayerJump))]
public class PlayerFootstepView : MonoBehaviour
{
    [Header("이동 판정 (인스펙터 조절)")]
    [Tooltip("이 속도(m/s) 아래면 멈춘 것으로 보고 발소리를 끈다 — 경사에서 미끄러지는 정도는 걷는 것이 아니다")]
    [SerializeField]
    private float m_moveSpeedThreshold = 0.6f;

    [Tooltip("이 속도(m/s)를 넘으면 뛰는 것으로 보고 뜀 발소리로 바꾼다 — 걷기 5·달리기 8 사이에 둘 것")]
    [SerializeField]
    private float m_runSpeedThreshold = 6.5f;

    [Tooltip("속도를 얼마나 부드럽게 볼지(초). 0에 가까울수록 즉각 반응하지만 한 프레임 튐에도 소리가 깜빡인다")]
    [Min(0f)]
    [SerializeField]
    private float m_speedSmoothing = 0.12f;

    private PlayerJump m_jump;
    private PlayerHealth m_health;
    private AudioSource m_loopSource;

    private Vector3 m_lastPosition;
    private float m_speed; // 평활화한 수평 속도
    private bool m_wasAirborne;
    private EAudioClip m_loopId = EAudioClip.None;

    private void Awake()
    {
        m_jump = GetComponent<PlayerJump>();
        m_health = GetComponent<PlayerHealth>();

        // 자식으로 다는 이유는 위치 때문이다 — 몸에 붙어 있어야 남의 발소리가 그 사람 자리에서 난다.
        var host = new GameObject("FootstepLoop");
        host.transform.SetParent(transform, false);

        m_loopSource = host.AddComponent<AudioSource>();
        m_loopSource.playOnAwake = false;
        m_loopSource.loop = true;
        m_loopSource.spatialBlend = 1f; // 3D — 어디서 나는 발소리인지가 곧 정보다
        m_loopSource.rolloffMode = AudioRolloffMode.Linear;
    }

    private void OnEnable()
    {
        m_lastPosition = transform.position;
        m_speed = 0f;
        m_wasAirborne = m_jump.IsAirborne;
    }

    private void OnDisable() => StopLoop();

    private void Update()
    {
        Vector3 delta = transform.position - m_lastPosition;
        m_lastPosition = transform.position;
        delta.y = 0f; // 오르내림은 걸음이 아니다 — 경사·계단에서 속도가 부풀지 않게 수평만 본다

        // 순간 속도는 한 프레임 튐(스폰 직후 순간이동·NetworkTransform 보정)에 크게 흔들린다.
        // 그대로 쓰면 서 있는데도 발소리가 깜빡이므로 평활화해서 본다.
        float instant = delta.magnitude / Mathf.Max(Time.deltaTime, 0.0001f);
        m_speed = m_speedSmoothing > 0f
            ? Mathf.Lerp(m_speed, instant, Time.deltaTime / m_speedSmoothing)
            : instant;

        bool airborne = m_jump.IsAirborne;
        if (airborne != m_wasAirborne)
        {
            m_wasAirborne = airborne;
            PlayOneShot(airborne ? EAudioClip.JumpTakeoff : EAudioClip.JumpLand);
        }

        if (airborne)
        {
            StopLoop(); // 공중에는 디딜 바닥이 없다
            return;
        }

        // 다운·기절·매달린 상태에서 나는 움직임은 제 발로 걷는 것이 아니다(끌려가는 중 포함) —
        // 무력화 게이트는 저장소 관례대로 PlayerHealth.IsTargetable 하나로 본다.
        if (m_health != null && !m_health.IsTargetable)
        {
            StopLoop();
            return;
        }

        if (m_speed < m_moveSpeedThreshold)
        {
            StopLoop();
            return;
        }

        SetLoop(m_speed > m_runSpeedThreshold ? EAudioClip.FootstepRun : EAudioClip.FootstepWalk);
    }

    // 같은 소리가 이미 돌고 있으면 건드리지 않는다 — 매 프레임 Play를 다시 부르면 클립이 계속
    // 처음으로 되감겨 발소리가 아니라 첫 프레임만 반복하는 잡음이 된다.
    private void SetLoop(EAudioClip id)
    {
        if (m_loopId == id)
            return;

        AudioLibrary.Entry entry = App.Sound?.GetSfxEntry(id);
        if (entry?.Clip == null)
        {
            StopLoop(); // 카탈로그 미배정 — 조용히 무음
            return;
        }

        m_loopSource.clip = entry.Clip;
        m_loopSource.volume = entry.Volume;
        m_loopSource.minDistance = entry.MinDistance;
        m_loopSource.maxDistance = Mathf.Max(entry.MaxDistance, entry.MinDistance + 0.1f);
        m_loopSource.Play();
        m_loopId = id;
    }

    private void StopLoop()
    {
        if (m_loopId == EAudioClip.None)
            return;

        m_loopSource.Stop();
        m_loopId = EAudioClip.None;
    }

    // 매니저가 없는 구성(부트스트랩 없는 씬 직접 Play)에서는 조용히 넘어간다 (R8 관례).
    private void PlayOneShot(EAudioClip id) => App.Sound?.PlaySfxAt(id, transform.position);
}
