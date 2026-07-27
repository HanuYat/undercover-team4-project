using UnityEngine;

/// <summary>
/// 먹통 음성 왜곡 프로파일 (#372) — 왜곡음을 만드는 필터 조합과 수치를 한 에셋에 모은다.
/// <see cref="VivoxManager"/>는 "언제 왜곡할지"만 알고, "어떻게 망가뜨릴지"는 전부 여기서 정한다.
///
/// <b>왜 에셋으로 빼나</b> — 순수 튜닝 값이 스무 개 가까이 되어 매니저 인스펙터를 덮어버리고,
/// 무엇보다 프리셋을 갈아끼우며 A/B 할 수가 없다. 에셋이면 '기계음'·'고장난 무전' 같은 변형을
/// 여러 개 만들어 두고 바꿔 끼울 수 있다. (Npc*Config와 같은 관례)
///
/// DSP 계산 자체는 여기 없다 — 링 모듈레이션은 <see cref="VoiceRingModulator"/>가 갖는다.
/// 이 에셋이 담는 것은 "어떤 필터를 어떤 순서로 얹고 값을 뭘로 줄지"라는 조립 규칙뿐이다.
/// </summary>
[CreateAssetMenu(
    fileName = "VoiceDistortionProfile",
    menuName = "Undercover/Voice/Distortion Profile"
)]
public class VoiceDistortionProfile : ScriptableObject
{
    [Header("기본 음색")]
    [Tooltip("왜곡 기본 피치 — 1보다 낮으면 저음으로 뭉개진다 (고장난 음성 합성기 느낌)")]
    [SerializeField, Range(0.4f, 1.5f)]
    private float m_basePitch = 0.6f;

    [Tooltip("왜곡 강도 (AudioDistortionFilter) — 높을수록 지직거린다")]
    [SerializeField, Range(0f, 1f)]
    private float m_distortionLevel = 0.35f;

    [Tooltip(
        "저역 통과 차단 주파수(Hz) — 낮을수록 먹먹해진다. 링 모듈레이터를 쓸 때는 너무 낮추지 말 것: "
            + "기계음의 특징인 금속성 배음까지 깎여 그냥 먹먹한 소리가 된다"
    )]
    [SerializeField, Range(300f, 5000f)]
    private float m_lowPassHz = 2000f;

    // ---- 기계음(링 모듈레이션) ----
    // 로봇 목소리의 정체는 링 모듈레이션이다. Unity 내장 필터에는 없어 VoiceRingModulator로 직접 구현했다.
    // 코러스·왜곡이 '고장난 무전'이라면 이쪽은 '기계가 말하는' 소리 — 로봇 경찰이라는 설정에 더 맞는다.
    [Header("기계음 (링 모듈레이션)")]
    [Tooltip("링 모듈레이터(기계음) 사용 — 로봇이 말하는 듯한 음색. 기계음 연출의 핵심")]
    [SerializeField]
    private bool m_useRingMod = true;

    [Tooltip("링 모듈레이터 반송파 주파수(Hz). 30~80이 전형적인 로봇 음성, 높일수록 금속성 링잉에 가까워진다")]
    [SerializeField, Range(10f, 400f)]
    private float m_ringModCarrierHz = 55f;

    [Header("피치 글리치")]
    [Tooltip("피치가 튀는 간격(초) 최소/최대 — 짧을수록 자주 튀어 알아듣기 어려워진다")]
    [SerializeField]
    private float m_glitchIntervalMin = 0.22f;

    [SerializeField]
    private float m_glitchIntervalMax = 0.65f;

    // 오토튠이 오토튠처럼 들리는 이유는 음정 보정 자체가 아니라 피치가 '계단식'으로 끊기기 때문이다.
    // 연속 난수로 피치를 뽑으면 흐물거리며 미끄러지는데, 반음 단위로 스냅하면 기계가 음을 짚는 느낌이 난다.
    // (진짜 오토튠은 실시간 피치 검출 + 피치 시프트가 필요해 이 연출 하나에 쓸 비용이 아니다)
    [Tooltip("글리치 피치를 반음 단위로 스냅 — 오토튠 특유의 계단식 음정 변화를 만든다")]
    [SerializeField]
    private bool m_snapPitchToSemitones = true;

    [Tooltip("글리치 시 기본 피치에서 벗어나는 반음 범위 (-7 = 5도 아래, +7 = 5도 위)")]
    [SerializeField]
    private int m_glitchSemitoneMin = -7;

    [SerializeField]
    private int m_glitchSemitoneMax = 7;

    [Tooltip("반음 스냅을 끈 경우에 쓰는 연속 피치 범위")]
    [SerializeField]
    private float m_glitchPitchMin = 0.55f;

    [SerializeField]
    private float m_glitchPitchMax = 1.5f;

    // 코러스는 발음 윤곽을 흐려 말을 뭉갠다. 다만 링 모듈레이터와 겹치면 명료도가 과하게 떨어져
    // 아무 말도 못 알아듣게 되므로, 기계음 프리셋에서는 기본으로 꺼 둔다 (토글로 A/B 가능).
    [Header("코러스 (겹침 흔들림)")]
    [Tooltip("코러스 사용 — 말을 뭉갠다. 링 모듈레이터와 함께 켜면 과해지기 쉽다")]
    [SerializeField]
    private bool m_useChorus;

    [Tooltip("코러스 깊이 — 높을수록 흔들림이 커진다")]
    [SerializeField, Range(0f, 1f)]
    private float m_chorusDepth = 0.7f;

    [Tooltip("코러스 속도(Hz) — 흔들리는 빠르기")]
    [SerializeField, Range(0f, 20f)]
    private float m_chorusRate = 1.2f;

    [Tooltip("코러스 혼합량 — 원음 대비 흔들린 복사본의 비중")]
    [SerializeField, Range(0f, 1f)]
    private float m_chorusMix = 0.6f;

    [Header("에코")]
    [Tooltip("짧은 에코 사용 — 소리를 번지게 해 뭉개짐을 더한다")]
    [SerializeField]
    private bool m_useEcho = true;

    [Tooltip("에코 딜레이(ms) — 짧을수록 금속성으로 번진다")]
    [SerializeField, Range(10f, 500f)]
    private float m_echoDelayMs = 45f;

    [Tooltip("에코 감쇠율 — 높을수록 오래 번진다")]
    [SerializeField, Range(0f, 1f)]
    private float m_echoDecay = 0.3f;

    [Tooltip("에코 혼합량")]
    [SerializeField, Range(0f, 1f)]
    private float m_echoMix = 0.35f;

    /// <summary>
    /// 오디오 탭에 왜곡 필터 묶음을 얹는다 — 호출부(<see cref="VivoxManager"/>)는 순서도 값도 모른다.
    ///
    /// 필터 순서: 링모드(기계음) → 코러스(윤곽 흐리기) → 왜곡(지직) → 에코(번짐) → 저역통과(먹먹).
    /// 링 모듈레이터가 맨 앞이라야 원음의 배음을 옮긴 결과에 나머지 색이 입혀진다 — 뒤에 두면
    /// 이미 뭉개진 소리를 다시 곱하는 꼴이라 기계음 특유의 음정감이 흐려진다.
    /// 저역통과는 마지막 — 앞 단계가 만든 고역 잡음까지 함께 깎아야 정리된 소리가 된다.
    /// </summary>
    /// <param name="tapObject">필터를 얹을 오디오 탭 GameObject</param>
    /// <param name="source">그 탭의 재생 AudioSource (피치를 여기에 건다)</param>
    public void Apply(GameObject tapObject, AudioSource source)
    {
        if (tapObject == null)
            return;

        if (source != null)
            source.pitch = m_basePitch;

        if (m_useRingMod)
            tapObject.AddComponent<VoiceRingModulator>().Configure(m_ringModCarrierHz);

        if (m_useChorus)
        {
            AudioChorusFilter chorus = tapObject.AddComponent<AudioChorusFilter>();
            chorus.depth = m_chorusDepth;
            chorus.rate = m_chorusRate;
            chorus.wetMix1 = m_chorusMix;
            chorus.wetMix2 = m_chorusMix * 0.7f; // 2·3번 탭을 조금씩 낮춰 겹침이 뭉치지 않게
            chorus.wetMix3 = m_chorusMix * 0.4f;
        }

        AudioDistortionFilter distortion = tapObject.AddComponent<AudioDistortionFilter>();
        distortion.distortionLevel = m_distortionLevel;

        if (m_useEcho)
        {
            AudioEchoFilter echo = tapObject.AddComponent<AudioEchoFilter>();
            echo.delay = m_echoDelayMs;
            echo.decayRatio = m_echoDecay;
            echo.wetMix = m_echoMix;
            echo.dryMix = 1f;
        }

        AudioLowPassFilter lowPass = tapObject.AddComponent<AudioLowPassFilter>();
        lowPass.cutoffFrequency = m_lowPassHz;
    }

    /// <summary>다음 글리치까지의 대기 시간(초).</summary>
    public float NextGlitchInterval() => Random.Range(m_glitchIntervalMin, m_glitchIntervalMax);

    /// <summary>
    /// 다음 글리치 피치를 뽑는다. 반음 스냅을 켜면 기본 피치에서 반음 단위로만 벗어나
    /// 오토튠처럼 계단식으로 음이 바뀐다 — 연속 난수는 음이 미끄러져 '고장'에 가깝게 들린다.
    /// </summary>
    public float NextGlitchPitch()
    {
        if (!m_snapPitchToSemitones)
            return Random.Range(m_glitchPitchMin, m_glitchPitchMax);

        // Random.Range(int)는 상한이 배타적이라 +1 — max 반음까지 포함시킨다
        int semitone = Random.Range(m_glitchSemitoneMin, m_glitchSemitoneMax + 1);
        return m_basePitch * Mathf.Pow(2f, semitone / 12f); // 반음 = 2^(1/12)배
    }
}
