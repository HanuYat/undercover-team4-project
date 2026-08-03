using UnityEngine;

/// <summary>
/// 유치장 경보등 — 본부에 두는 물리 오브젝트로, 자물쇠에서 벌어지는 일을 색과 점멸로 알린다. (#311/#493)
///
/// 화면 텍스트 경보를 대신한다. 본부 담당자가 <b>화면이 아니라 공간</b>을 보고 알아채게 하는 것이
/// 목적이라, 어디를 보고 있든 뜨는 HUD와 달리 "경보등 쪽을 봐야 안다". 대신 무전으로 현장에
/// 알리는 행위가 필요해진다 — 본부/현장 분업이라는 게임의 축과 맞는다.
///
/// 상태는 세 가지다. 자물쇠가 열려 있으면(<see cref="EState.Opened"/>) 다시 잠길 때까지 계속 울리고,
/// 해제 시도(<see cref="EState.Attempt"/>)는 정해진 시간 뒤 저절로 가라앉는다.
/// 열림이 시도보다 강하다 — 이미 열린 뒤에 온 시도 알림이 경보를 약하게 만들면 안 된다.
///
/// 서버·클라이언트 구분이 없다: 구독하는 두 신호가 이미 전 피어에서 발생하므로
/// (<see cref="JailLock.OnLockChanged"/>는 NetworkVariable 콜백, <see cref="JailLock.OnUnlockAttempt"/>는
/// ClientRpc) 각 피어가 자기 화면의 경보등을 각자 켠다.
/// </summary>
public class JailAlarmBeacon : MonoBehaviour
{
    private enum EState
    {
        Idle,    // 평시
        Attempt, // 해제 시도 감지 — 시간이 지나면 저절로 Idle로
        Opened,  // 자물쇠가 열린 상태 — 다시 잠길 때까지 유지
    }

    [Header("대상 자물쇠 (비우면 씬에서 자동 탐색)")]
    [SerializeField]
    private JailLock m_jailLock;

    [Header("빛나는 부분")]
    [Tooltip("색을 바꿀 렌더러 — 경광등 유리/램프 부분. 여러 개면 전부 같이 바뀐다")]
    [SerializeField]
    private Renderer[] m_renderers;

    [Tooltip("같이 켤 실광원 (선택) — 없으면 렌더러 색만 바뀐다")]
    [SerializeField]
    private Light m_light;

    [Header("상태별 색")]
    [SerializeField]
    private Color m_idleColor = new Color(0.15f, 0.15f, 0.15f);

    [Tooltip("해제 시도 감지 — 주의")]
    [SerializeField]
    private Color m_attemptColor = new Color(1f, 0.7f, 0.1f);

    [Tooltip("자물쇠 열림 — 경보")]
    [SerializeField]
    private Color m_openedColor = new Color(1f, 0.15f, 0.1f);

    [Header("연출")]
    [Tooltip("초당 점멸 횟수")]
    [SerializeField]
    private float m_blinkPerSecond = 2f;

    [Tooltip("해제 시도 경보가 저절로 가라앉기까지의 시간(초)")]
    [SerializeField]
    private float m_attemptSeconds = 6f;

    [Tooltip("실광원의 최대 밝기 — 점멸에 따라 0~이 값 사이를 오간다")]
    [SerializeField]
    private float m_lightIntensity = 4f;

    // 색을 머티리얼에 직접 쓰면 인스턴스가 복제된다 — 프로퍼티 블록으로 렌더러에만 덮어쓴다
    private MaterialPropertyBlock m_block;
    private static readonly int s_baseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int s_emissionColorId = Shader.PropertyToID("_EmissionColor");

    private EState m_state = EState.Idle;
    private float m_attemptUntil;

    private void Awake()
    {
        m_block = new MaterialPropertyBlock();
    }

    private void Start()
    {
        // 비워두면 씬에서 찾는다 — 자물쇠는 App에 등록된 매니저가 아니라 씬 배치 오브젝트라
        // App 파사드 경로가 없다 (RoundFundHud가 JailZone을 찾는 것과 같은 이유)
        if (m_jailLock == null)
            m_jailLock = FindFirstObjectByType<JailLock>();

        if (m_jailLock == null)
        {
            Debug.LogWarning("JailAlarmBeacon: JailLock을 찾지 못해 경보등이 동작하지 않는다", this);
            enabled = false;
            return;
        }

        m_jailLock.OnLockChanged += HandleLockChanged;
        m_jailLock.OnUnlockAttempt += HandleUnlockAttempt;

        // 늦게 붙었을 때(이미 열린 채 진행 중) 현재 상태를 즉시 반영 — DeviceBlackoutView와 같은 관례
        HandleLockChanged(m_jailLock.IsLocked);
    }

    private void OnDestroy()
    {
        if (m_jailLock == null)
            return;

        m_jailLock.OnLockChanged -= HandleLockChanged;
        m_jailLock.OnUnlockAttempt -= HandleUnlockAttempt;
    }

    private void HandleLockChanged(bool locked)
    {
        // 열림은 시도를 덮어쓴다. 다시 잠기면 남은 시도 경보를 이어가지 않고 평시로 — 상황이 끝났다는 뜻이다.
        m_state = locked ? EState.Idle : EState.Opened;
        if (locked)
            m_attemptUntil = 0f;
    }

    private void HandleUnlockAttempt()
    {
        m_attemptUntil = Time.time + m_attemptSeconds;

        // 이미 열려 있으면 격하하지 않는다 — 열린 상태가 더 심각하다
        if (m_state != EState.Opened)
            m_state = EState.Attempt;
    }

    private void Update()
    {
        // 시도 경보만 시간이 지나면 가라앉는다 (열림은 자물쇠가 다시 잠겨야 풀린다)
        if (m_state == EState.Attempt && Time.time >= m_attemptUntil)
            m_state = EState.Idle;

        Apply();
    }

    private void Apply()
    {
        Color target = StateColor();

        // 평시엔 점멸하지 않는다 — 꺼진 램프가 곧 "이상 없음"이다
        float blink = m_state == EState.Idle
            ? 1f
            : Mathf.PingPong(Time.time * m_blinkPerSecond * 2f, 1f);

        Color lit = target * blink;

        if (m_renderers != null)
        {
            foreach (Renderer renderer in m_renderers)
            {
                if (renderer == null)
                    continue;

                renderer.GetPropertyBlock(m_block);
                m_block.SetColor(s_baseColorId, lit);
                m_block.SetColor(s_emissionColorId, lit);
                renderer.SetPropertyBlock(m_block);
            }
        }

        if (m_light != null)
        {
            m_light.color = target;
            m_light.intensity = m_state == EState.Idle ? 0f : m_lightIntensity * blink;
        }
    }

    private Color StateColor()
    {
        switch (m_state)
        {
            case EState.Opened:
                return m_openedColor;
            case EState.Attempt:
                return m_attemptColor;
            default:
                return m_idleColor;
        }
    }
}
