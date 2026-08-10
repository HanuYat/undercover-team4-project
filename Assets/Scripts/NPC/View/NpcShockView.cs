using UnityEngine;

/// <summary>
/// NPC 감전 표현 — 테이저에 맞아 기절해 있는 동안 몸이 시안으로 물들고 전기 아크가 튄다. (#477)
///
/// <b>테이저 기절에만 붙는다.</b> 진압봉 KO(체력 0, #366)·폭발 넉백 KO는 같은 무력화지만 전기가
/// 아니므로 제외된다 — 3부작 색 언어에서 시안은 테이저 전용이고, "시안이 튀면 저건 쓰러져 있고
/// 지금 밧줄로 묶을 수 있다"가 한눈에 읽혀야 하기 때문이다. 그 구분은
/// <see cref="NpcStun.OnTaserStunStarted"/>가 해 준다(<see cref="NpcStunCause"/> 참고).
///
/// 아크 방출 자체는 <see cref="ShockArcEmitter"/>가 맡는다 — 플레이어 감전(<see cref="PlayerHitView"/>)과
/// 공유하는 부품이다. 몸 발광은 <see cref="BodyTint"/>에 요청한다 — 타격 플래시(#478)와 같은
/// 프로퍼티를 다투지 않도록 색 오버라이드 소유권을 그쪽으로 모았다.
/// 이 컴포넌트는 <b>언제 켜고 끌지</b>만 담당한다.
/// </summary>
[RequireComponent(typeof(NpcController))]
[RequireComponent(typeof(ShockArcEmitter))]
[RequireComponent(typeof(BodyTint))]
public class NpcShockView : MonoBehaviour
{
    [Header("명중 순간 발광")]
    [Tooltip("맞은 순간 몸이 물드는 색 — 셰이더에 _BaseColor가 있을 때만 적용된다")]
    [SerializeField]
    private Color m_flashColor = new Color(0.3f, 0.95f, 1f, 1f);

    [SerializeField]
    private float m_flashSeconds = 0.08f;

    private NpcController m_controller;
    private ShockArcEmitter m_emitter;
    private BodyTint m_tint;

    private bool m_active; // 감전 연출 진행 중 — 기절이 풀리면 내려간다
    private float m_calmFromTime; // 이 시각부터 아크가 잦아든다
    private float m_flashUntil;

    private void Awake()
    {
        m_controller = GetComponent<NpcController>();
        m_emitter = GetComponent<ShockArcEmitter>();
        m_tint = GetComponent<BodyTint>();
    }

    private void OnEnable()
    {
        m_controller.Stun.OnTaserStunStarted += HandleTaserStunStarted;
        m_controller.Stun.OnStunnedChanged += HandleStunnedChanged;
    }

    private void OnDisable()
    {
        m_controller.Stun.OnTaserStunStarted -= HandleTaserStunStarted;
        m_controller.Stun.OnStunnedChanged -= HandleStunnedChanged;
        Stop();
    }

    private void HandleTaserStunStarted(float seconds)
    {
        m_active = true;
        m_flashUntil = Time.time + m_flashSeconds;

        // 지속 시간은 '언제부터 잦아들지'의 힌트로만 쓴다 — 기절이 밖에서 먼저 풀리는 경로가 있어
        // (밧줄 묶기·수감·넉백) 이 시각으로 종료를 판단하면 어긋난다. 종료는 IsStunned가 알린다.
        // 꼬리 길이는 ShockArcEmitter가 갖는다 — 플레이어 감전(PlayerHitView)과 같은 값이어야
        // 같은 테이저에 맞고 잦아드는 시점이 갈리지 않는다.
        m_calmFromTime = Time.time + Mathf.Max(0f, seconds - ShockArcEmitter.k_calmTailSeconds);

        m_emitter.SetCalm(false);
        m_emitter.SetEmitting(true);
    }

    private void HandleStunnedChanged(bool stunned)
    {
        if (!stunned)
            Stop();
    }

    private void Stop()
    {
        if (!m_active)
            return;

        m_active = false;
        m_flashUntil = 0f;
        m_emitter.SetEmitting(false);
        m_tint.ClearFlash();
    }

    private void Update()
    {
        if (!m_active)
            return;

        // 수갑 채포 등 기절이 바깥에서 풀리는 경로가 있어 이벤트만 믿지 않는다 —
        // 늦게 오거나 유실된 알림이 있어도 여기서 스스로 멎는다.
        if (!m_controller.Stun.IsStunned)
        {
            Stop();
            return;
        }

        if (Time.time >= m_calmFromTime)
            m_emitter.SetCalm(true);

        TickFlash();
    }

    private void TickFlash()
    {
        if (m_flashUntil <= 0f)
            return;

        if (Time.time >= m_flashUntil)
        {
            m_flashUntil = 0f;
            m_tint.ClearFlash();
            return;
        }

        // 남은 비율만큼 물든다 — 맞은 순간이 가장 밝고 곧 원래 색으로 돌아온다
        float t = (m_flashUntil - Time.time) / Mathf.Max(0.0001f, m_flashSeconds);
        m_tint.SetFlash(Color.Lerp(Color.white, m_flashColor, t));
    }
}
