using UnityEngine;

/// <summary>
/// NPC 타격 표현 — 맞은 순간 몸이 <b>남은 체력에 따라 다른 색으로</b> 번쩍인다. (#478)
///
/// <b>색이 곧 체력 게이지다.</b> NPC 체력은 UI로 표시하지 않기로 했으므로(GDD 261 — 지속형 체력이라
/// 바를 달면 배회 군중 위에 상시로 떠 있게 된다), 플레이어가 "한 대 더 때리면 넘어간다"를 읽을
/// 수단이 이 플래시뿐이다. 진압봉 3대면 기절이므로 흰 → 주황 → 적 세 단계로 나눈다.
///
/// <b>타수를 세지 않고 체력 비율로 고른다.</b> 폭발(<c>BombDevice</c>)처럼 다른 데미지원이 섞여도
/// 색이 맞아야 하고, 기절에서 깨어나며 체력이 회복되면(#366) 색도 함께 되돌아가야 한다.
///
/// 몸 색은 직접 칠하지 않고 <see cref="BodyTint"/>에 요청한다 — 감전 발광(<see cref="NpcShockView"/>, #477)과
/// 같은 프로퍼티를 다투지 않기 위해서다. 임팩트 링·스파크는 이 컴포넌트가 아니라 무기(<c>Baton</c>)가
/// 낸다: 맞은 <b>지점</b>은 무기만 알고, 여기는 "맞았다"만 안다.
/// </summary>
[RequireComponent(typeof(NpcController))]
[RequireComponent(typeof(BodyTint))]
public class NpcHitView : MonoBehaviour
{
    [Header("타격 플래시")]
    [Tooltip("여유 있는 체력에서 맞았을 때 (진압봉 1대 후)")]
    [SerializeField] private Color m_healthyColor = Color.white;

    [Tooltip("한 대 남았을 때 (진압봉 2대 후) — 다음 타격이 쓰러뜨린다는 예고")]
    [SerializeField] private Color m_woundedColor = new Color(1f, 0.55f, 0.15f, 1f);

    [Tooltip("이 타격으로 쓰러졌을 때")]
    [SerializeField] private Color m_downColor = new Color(1f, 0.2f, 0.15f, 1f);

    [Tooltip("플래시 유지 시간(초). 연타로 맞으므로 짧아야 한다 — 길면 계속 물들어 있다")]
    [SerializeField] private float m_flashSeconds = 0.09f;

    [Tooltip("이 체력 비율 이하면 '한 대 남음' 색을 쓴다. 진압봉 34뎀/최대 100 기준 2대 맞으면 0.32")]
    [SerializeField] private float m_woundedRatio = 0.5f;

    [Header("손상 잔류 (기본 꺼짐 — 팀 검토 필요)")]
    [Tooltip(
        "켜면 다친 몸에 색이 계속 남아 군중 속에서 '한 대 남았다'가 읽힌다. "
        + "다만 GDD 261이 같은 이유로 머리 위 체력 바를 떼어냈으므로(다친 NPC가 회복 전까지 표시를 "
        + "달고 다닌다) 그 결정과 충돌할 수 있다 — 켜보고 팀이 정할 것")]
    [SerializeField] private bool m_sustainedDamageTint;

    [Tooltip("손상 잔류 색 — 원래 색에 섞이는 정도라 옅게 잡을 것")]
    [SerializeField] private Color m_sustainedColor = new Color(1f, 0.75f, 0.7f, 1f);

    private NpcController m_controller;
    private BodyTint m_tint;
    private float m_flashUntil;

    private void Awake()
    {
        m_controller = GetComponent<NpcController>();
        m_tint = GetComponent<BodyTint>();
    }

    private void OnEnable() => m_controller.OnDamaged += HandleDamaged;

    private void OnDisable()
    {
        m_controller.OnDamaged -= HandleDamaged;
        m_flashUntil = 0f;
        m_tint.ClearFlash();
        m_tint.ClearSustained();
    }

    // DamageHit의 가해자 정보는 쓰지 않는다 — 방향 표시는 맞은 사람 화면의 몫이고(#476),
    // NPC 몸은 어느 쪽에서 맞았든 같은 색으로 번쩍인다.
    private void HandleDamaged(DamageHit hit)
    {
        m_tint.SetFlash(ResolveFlashColor());
        m_flashUntil = Time.time + m_flashSeconds;
    }

    /// <summary>
    /// 맞은 <b>직후</b> 체력으로 색을 고른다 — 이벤트가 HP 반영 뒤에 발행되므로 여기서 읽으면 결과값이다.
    /// 쓰러진 순간(0)은 적색, 한 대 남았으면 주황, 그 외엔 흰색.
    /// </summary>
    private Color ResolveFlashColor()
    {
        int maxHp = Mathf.Max(1, m_controller.MaxHp);
        float ratio = (float)m_controller.CurrentHp / maxHp;

        if (ratio <= 0f)
            return m_downColor;

        return ratio <= m_woundedRatio ? m_woundedColor : m_healthyColor;
    }

    private void Update()
    {
        if (m_flashUntil > 0f && Time.time >= m_flashUntil)
        {
            m_flashUntil = 0f;
            m_tint.ClearFlash(); // 잔류가 걸려 있으면 BodyTint가 그쪽을 다시 드러낸다
        }

        RefreshSustained();
    }

    // 지속 표시는 상태이므로 폴링한다 — 기절에서 깨어나며 체력이 회복되는 지점(#366)에
    // 이벤트가 없어서, 이벤트만 보면 회복된 몸에 손상 색이 영영 남는다.
    private void RefreshSustained()
    {
        if (!m_sustainedDamageTint)
            return;

        int maxHp = Mathf.Max(1, m_controller.MaxHp);
        float ratio = (float)m_controller.CurrentHp / maxHp;

        if (ratio > m_woundedRatio || ratio <= 0f)
            m_tint.ClearSustained(); // 멀쩡하거나, 이미 쓰러져 기절 표현이 맡는 구간
        else
            m_tint.SetSustained(m_sustainedColor);
    }
}
