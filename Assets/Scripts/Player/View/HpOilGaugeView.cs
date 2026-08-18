using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 기름통 HP 게이지 — 채움·숫자·누유 줄기·바닥 고임을 그린다. (#691)
/// 좌하단 <c>HpPanel</c>에 붙으며, 값을 넣어 주는 쪽은 <see cref="PlayerHpUI"/>다.
///
/// <b>순수 표현 컴포넌트로 유지할 것.</b> <see cref="DamageVignetteUI"/>와 같은 방침이다 —
/// "누가 언제 맞았나"·"내가 오너인가"는 전부 <see cref="PlayerHpUI"/>가 알고, 여기는 받은
/// 비율과 피해량으로 그림만 그린다. 그래야 이 뷰가 플레이어 오브젝트의 구조를 몰라도 된다.
///
/// 로봇이라 피 대신 <b>시스템 손상</b> 언어를 쓴다 (GDD 7-5 — HP 0은 사망이 아니라 기능 정지,
/// 10-1은 HP를 '내구도'로 정의한다). 누유는 새 언어가 아니라 <see cref="DamageVignetteUI"/>가
/// 이미 세운 그 언어를 게이지 쪽으로 옮긴 것이다.
/// </summary>
public class HpOilGaugeView : MonoBehaviour
{
    [Header("통")]
    [Tooltip(
        "기름 채움 — Image Type은 Filled, Fill Method는 Horizontal, Fill Origin은 Left. "
            + "[주의] Sprite가 비어 있으면 Unity가 Filled 타입을 무시하고 단순 사각형을 그려 "
            + "채움이 전혀 동작하지 않는다 (NpcHealthBarView와 같은 함정)"
    )]
    [SerializeField]
    private Image m_gauge;

    [Tooltip("통 안에 겹쳐 놓는 현재/최대 수치. 게이지로 바꿔도 숫자는 남긴다 — 정확한 값이 필요한 판단(진압봉 몇 대를 더 버티나)이 있다")]
    [SerializeField]
    private TextMeshProUGUI m_hpText;

    [Header("누유 — 줄기")]
    [Tooltip("통 아래에 걸리는 기름 줄기. 알파를 코드가 구동하므로 씬에서는 비활성으로 둬도 된다")]
    [SerializeField]
    private Image m_leak;

    [Tooltip("피격 직후 왈칵 새는 연출이 완전히 멎을 때까지의 시간(초)")]
    [SerializeField]
    private float m_burstSeconds = 2.5f;

    // 진압봉 1대(Baton.m_damage 기본 34)에서 최대 강도가 나오도록 잡은 기준값.
    // DamageVignetteUI.m_damageForMaxAlpha와 같은 숫자지만 참조하지 않고 따로 든다 —
    // 저건 화면 비네트가 포화되는 지점이고 이건 통이 터지는 지점이라, 한쪽을 튜닝하려고
    // 다른 쪽까지 끌려가면 안 된다. (그 필드의 주석과 같은 이유)
    [Tooltip("이 피해량 이상이면 누유가 최대 강도로 터진다. 그 미만은 비례해서 약해진다")]
    [SerializeField]
    private int m_damageForMaxBurst = 34;

    [Header("누유 — 상시")]
    [Tooltip("이 체력 비율 이하부터 상시로 기름이 샌다. 참고 아트가 75/100에서 이미 한 방울 흘리므로 기본값도 그에 맞춘다")]
    [SerializeField]
    private float m_steadyThreshold = 0.8f;

    [Tooltip("상시 누유가 가장 심할 때(체력 바닥)의 알파. 왈칵보다 낮게 — 상시는 상태 표시지 타격 표현이 아니다")]
    [SerializeField]
    private float m_steadyMaxAlpha = 0.75f;

    [Tooltip("상시 누유가 목표 세기에 도달하기까지의 시간(초). 클수록 천천히 배어 나온다")]
    [SerializeField]
    private float m_steadyFadeSeconds = 0.8f;

    [Header("누유 — 바닥 고임")]
    [Tooltip("통 아래 고이는 기름. 누유가 이어질수록 짙어지고 넓어진다")]
    [SerializeField]
    private Image m_puddle;

    [SerializeField]
    private float m_puddleMaxAlpha = 0.85f;

    [Tooltip("고임이 가장 클 때의 가로 배율 — 1이면 스프라이트 원본 크기")]
    [SerializeField]
    private float m_puddleMaxScale = 1f;

    [Tooltip("고임이 짙어지는 속도(초당 알파). 고이는 건 눈에 띄게")]
    [SerializeField]
    private float m_puddleFillPerSecond = 0.5f;

    [Tooltip("회복 후 고임이 마르는 속도(초당 알파). 고이는 것보다 느려야 '아직 흔적이 남았다'가 읽힌다")]
    [SerializeField]
    private float m_puddleDryPerSecond = 0.2f;

    // 흐르는 기색을 내는 느린 사인. 알파가 <b>0으로 떨어지지 않게</b> 좁은 폭으로만 흔들고,
    // 줄기를 몇 픽셀 아래위로 함께 민다 — 기름이 배어 나오는 것처럼 보이게 하는 장치다.
    //
    // 처음에는 저체력 글리치(0.5~2초)를 따라 <b>간헐적으로 켰다 끄는</b> 방울 리듬이었는데,
    // 정적인 스프라이트의 알파를 토글하는 방식이라 화면에서는 '뚝뚝'이 아니라 그냥 <b>점멸</b>로
    // 읽혔다 (실제 플레이에서 확인). 글리치는 전자적 오작동이라 켜고 꺼지는 게 맞지만 기름은
    // 흘러내리는 물질이라 같은 문법을 쓸 수 없다 — 신호의 성격이 달라 리듬을 공유하지 않는다.
    private const float k_flowPeriodSeconds = 2.6f;
    private const float k_flowAlphaWobble = 0.18f; // 이 비율만큼만 옅어졌다 짙어진다
    private const float k_flowBobPixels = 4f; // 줄기가 아래로 밀렸다 돌아오는 폭

    private float m_burstElapsed = -1f; // 음수 = 진행 중 아님 (DamageVignetteUI.m_vignetteElapsed 관례)
    private float m_burstStartAlpha;

    private float m_healthRatio = 1f;
    private float m_steadyAlpha;
    private Vector2 m_leakBasePosition;

    // 숫자로 찍어 둔 마지막 값 — 같은 값이면 문자열을 다시 만들지 않는다.
    // 첫 호출이 반드시 통과하도록 실제 HP가 될 수 없는 값으로 시작한다.
    private int m_shownHp = int.MinValue;
    private int m_shownMaxHp = int.MinValue;

    private float m_puddleAlpha;
    private Vector3 m_puddleBaseScale = Vector3.one;

    private void Awake()
    {
        if (m_puddle != null)
            m_puddleBaseScale = m_puddle.rectTransform.localScale;

        if (m_leak != null)
            m_leakBasePosition = m_leak.rectTransform.anchoredPosition;

        OverlayImage.SetAlpha(m_leak, 0f);
        OverlayImage.SetAlpha(m_puddle, 0f);
    }

    /// <summary>
    /// 체력 표시를 갱신한다 — 채움·숫자·상시 누유의 세기가 여기서 정해진다. 오너가 매 프레임 알린다.
    /// </summary>
    /// <remarks>
    /// 임계값(<see cref="m_steadyThreshold"/>)을 여기서 들고 판단하는 이유는
    /// <see cref="DamageVignetteUI.UpdateHealthState"/>와 같다 — 연출 튜닝값이라 표현 쪽에 있어야 한다.
    /// </remarks>
    public void SetHealth(int current, int max)
    {
        int safeMax = Mathf.Max(1, max);
        m_healthRatio = Mathf.Clamp01((float)current / safeMax);

        if (m_gauge != null)
            m_gauge.fillAmount = m_healthRatio;

        // 숫자는 값이 바뀔 때만 갱신한다 — 오너가 매 프레임 부르는 경로라, 그냥 대입하면
        // 같은 값이어도 프레임마다 문자열을 새로 만들고 TMP 메시를 더티로 만든다.
        // 채움(fillAmount)은 float 대입이라 그런 비용이 없어 매 프레임 그대로 둔다.
        if (current == m_shownHp && max == m_shownMaxHp)
            return;

        m_shownHp = current;
        m_shownMaxHp = max;

        if (m_hpText != null)
            m_hpText.text = $"{current} / {max}";
    }

    /// <summary>
    /// 피격 순간 기름이 왈칵 샌다. (#691)
    /// </summary>
    /// <param name="damage">
    /// <b>실제로 깎인</b> 체력 — 요청한 피해량이 아니다. 이미 0인 HP에 들어온 추가 피해는 0으로
    /// 들어와 연출이 나가지 않는다 (<see cref="PlayerHealth.TakeDamage"/>가 그렇게 실어 보낸다).
    /// </param>
    public void PlayLeakBurst(int damage)
    {
        if (damage <= 0)
            return;

        float alpha = Mathf.Clamp01((float)damage / Mathf.Max(1, m_damageForMaxBurst));

        // 연속 피격이 서로를 약화시키지 않게 한다 — 진행 중인 강한 누유 위로 약한 피격이 들어와도
        // 그림이 오히려 옅어지면 안 된다. (DamageVignetteUI.PlayHit와 같은 처리)
        m_burstStartAlpha = Mathf.Max(alpha, CurrentBurstAlpha());
        m_burstElapsed = 0f;
    }

    /// <summary>연출을 즉시 걷어낸다 — 디스폰·라운드 사이 리셋 지점에서 부른다.</summary>
    public void ClearAll()
    {
        m_burstElapsed = -1f;
        m_steadyAlpha = 0f;
        m_puddleAlpha = 0f;

        ApplyLeakOffset(0f);
        OverlayImage.SetAlpha(m_leak, 0f);
        ApplyPuddle();
    }

    private void Update()
    {
        TickLeak();
        TickPuddle();
    }

    // 왈칵(순간)과 뚝뚝(상태)은 서로 다른 정보를 말하므로 따로 계산해 <b>센 쪽</b>을 그린다.
    // 합산하지 않는 이유: 저체력에서 맞으면 둘이 더해져 알파가 포화돼, 정작 큰 피해를 입은
    // 순간이 평소와 구분되지 않는다.
    private void TickLeak()
    {
        float burst = TickBurst();
        float steady = TickSteadyDrip();

        OverlayImage.SetAlpha(m_leak, Mathf.Max(burst, steady));
    }

    private float TickBurst()
    {
        if (m_burstElapsed < 0f)
            return 0f;

        m_burstElapsed += Time.deltaTime;
        if (m_burstElapsed >= m_burstSeconds)
        {
            m_burstElapsed = -1f;
            return 0f;
        }

        return CurrentBurstAlpha();
    }

    // 들어올 때는 즉발, 빠질 때만 시간을 쓴다 — 페이드 인을 주면 맞은 순간이 흐려진다.
    private float CurrentBurstAlpha()
    {
        if (m_burstElapsed < 0f || m_burstSeconds <= 0f)
            return 0f;

        return m_burstStartAlpha * (1f - m_burstElapsed / m_burstSeconds);
    }

    private float TickSteadyDrip()
    {
        // 통이 완전히 비면(HP 0) 샐 기름도 없다 — 흐름을 거두고 고임만 남긴다.
        // 이게 없으면 기능 정지로 쓰러져 있는 내내 빈 통에서 기름이 계속 흐른다.
        bool leaking = m_healthRatio > 0f && m_healthRatio <= m_steadyThreshold;

        // 임계값 바로 아래는 거의 안 새고, 바닥에 가까울수록 짙어진다
        float severity = leaking ? Mathf.InverseLerp(m_steadyThreshold, 0f, m_healthRatio) : 0f;

        // 목표 세기로 <b>서서히</b> 밀어 넣는다. 체력이 계단식으로 떨어져도(진압봉 34씩) 누유는
        // 이어서 짙어지고, 회복하면 뚝 끊기지 않고 잦아든다.
        float step = m_steadyMaxAlpha / Mathf.Max(0.01f, m_steadyFadeSeconds) * Time.deltaTime;
        m_steadyAlpha = Mathf.MoveTowards(m_steadyAlpha, m_steadyMaxAlpha * severity, step);

        if (m_steadyAlpha <= 0.001f)
        {
            ApplyLeakOffset(0f);
            return 0f;
        }

        // 흐르는 기색 — 알파와 위치를 같은 느린 사인으로 함께 흔든다. 사인이라 끊기는 지점이 없고,
        // 알파가 0까지 내려가지 않아 점멸로 보이지 않는다.
        float wobble = (Mathf.Sin(Time.time * Mathf.PI * 2f / k_flowPeriodSeconds) + 1f) * 0.5f;
        ApplyLeakOffset(-k_flowBobPixels * wobble * severity);

        return m_steadyAlpha * Mathf.Lerp(1f - k_flowAlphaWobble, 1f, wobble);
    }

    private void ApplyLeakOffset(float deltaY)
    {
        if (m_leak != null)
            m_leak.rectTransform.anchoredPosition = m_leakBasePosition + new Vector2(0f, deltaY);
    }

    private void TickPuddle()
    {
        if (m_puddle == null)
            return;

        // 목표치는 '얼마나 망가졌나'로 정한다 — 방울이 떨어지는 순간에만 고이면, 간헐적인 리듬이
        // 그대로 고임에 옮아 웅덩이가 깜빡인다. 고임은 누적이라 리듬을 타면 안 된다.
        float target = m_healthRatio > m_steadyThreshold
            ? 0f
            : m_puddleMaxAlpha * Mathf.InverseLerp(m_steadyThreshold, 0f, m_healthRatio);

        float speed = target > m_puddleAlpha ? m_puddleFillPerSecond : m_puddleDryPerSecond;
        m_puddleAlpha = Mathf.MoveTowards(m_puddleAlpha, target, speed * Time.deltaTime);

        ApplyPuddle();
    }

    private void ApplyPuddle()
    {
        if (m_puddle == null)
            return;

        OverlayImage.SetAlpha(m_puddle, m_puddleAlpha);

        // 짙어지는 것과 넓어지는 것을 함께 몬다 — 알파만 올리면 웅덩이가 커지는 게 아니라
        // 조명이 밝아지는 것처럼 보인다.
        float scale = m_puddleMaxAlpha > 0f
            ? Mathf.Lerp(0.4f, m_puddleMaxScale, m_puddleAlpha / m_puddleMaxAlpha)
            : m_puddleMaxScale;

        m_puddle.rectTransform.localScale = new Vector3(
            m_puddleBaseScale.x * scale,
            m_puddleBaseScale.y * scale,
            m_puddleBaseScale.z
        );
    }
}
