using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

/// <summary>
/// 화면 중앙 크로스헤어. HUD 프리팹에 부착되며 App.UI.Crosshair로 접근한다. (#184)
/// 레이캐스트가 카메라 정중앙에서 나가므로(PlayerInteractor) 화면 중앙 고정 = 조준점.
/// 네트워크 무관 — 각 클라이언트 로컬 UI.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class CrosshairUI : CommonManagerBase
{
    [SerializeField] private Image m_crosshairImage;
    [Tooltip("기본 크로스헤어 색")]
    [SerializeField] private Color m_defaultColor = Color.white;
    [Tooltip("상호작용 가능한 대상 조준 시 색")]
    [SerializeField] private Color m_interactableColor = new Color(1f, 0.85f, 0.2f);
    // 조준 무기(테이저·진압봉)가 공유하는 '명중 가능' 색. 무기별로 나누지 않는 이유는 HUD 언어를
    // 하나로 유지하기 위함이다 — 플레이어가 배워야 할 건 "이 색이면 맞는다" 하나면 된다.
    // FormerlySerializedAs: 이름을 무기 일반으로 바꾸면서(#217) HUD 프리팹에 저장된 값을 잇는다.
    [Tooltip("조준 무기(테이저·진압봉)로 명중 가능한 대상을 겨눴을 때 색 (#328/#217)")]
    [FormerlySerializedAs("m_taserTargetColor")]
    [SerializeField] private Color m_weaponTargetColor = new Color(1f, 0.25f, 0.2f);

    /// <summary>조준 대상의 상호작용 가능 여부에 따라 크로스헤어 색을 바꾼다.</summary>
    public void SetInteractable(bool interactable)
    {
        if (m_crosshairImage != null)
            m_crosshairImage.color = interactable ? m_interactableColor : m_defaultColor;
    }

    /// <summary>
    /// 조준 무기(<see cref="IAimedWeapon"/>) 사용 중, 명중 가능한 대상을 겨눴는지에 따라 색을 바꾼다. (#328/#217)
    /// 이 무기들은 NPC 윤곽선을 끄므로(InteractionFeedback) 크로스헤어가 유일한 조준 피드백이다.
    /// </summary>
    public void SetWeaponTargeting(bool onTarget)
    {
        if (m_crosshairImage != null)
            m_crosshairImage.color = onTarget ? m_weaponTargetColor : m_defaultColor;
    }

    // ---- 히트마커 (#478) ----

    [Header("히트마커")]
    // 단일 Image가 아니라 컨테이너를 켜고 끈다 — 히트마커는 보통 여러 조각(X자 4개 막대)으로 그려지고,
    // 크로스헤어와 겹치지 않으려면 가운데가 비어 있어야 한다. 조각 수를 표시 코드가 몰라도 되게 한다.
    [Tooltip("명중 순간 잠깐 켜지는 마커 컨테이너 — 하위 그래픽 전부에 색이 칠해진다. 비우면 히트마커가 뜨지 않는다")]
    [SerializeField] private RectTransform m_hitMarker;

    [Tooltip("유효타 히트마커 색")]
    [SerializeField] private Color m_hitColor = Color.white;

    [Tooltip("동료를 때렸을 때 히트마커 색 — 오사를 즉시 알아야 한다 (#461)")]
    [SerializeField] private Color m_friendlyFireColor = new Color(1f, 0.35f, 0.1f);

    [Tooltip("히트마커 표시 시간(초)")]
    [Min(0.02f)]
    [SerializeField] private float m_hitMarkerSeconds = 0.15f;

    // 표시가 끝나는 시각. 코루틴을 쓰지 않는 이유는 연타 시 앞선 코루틴을 취소해야 하는데,
    // 시각 하나를 덮어쓰면 그 문제가 아예 생기지 않기 때문이다(다음 명중이 표시를 연장한다).
    private float m_hitMarkerHideTime;

    /// <summary>
    /// 명중 순간 히트마커를 잠깐 띄운다 — 때린 사람에게만 보인다(오너 전용 호출). (#478)
    /// <b>유효타에만 부른다</b> — 이미 제압된 대상을 쳐서 피해가 들어가지 않았을 때는 부르지 않는다.
    /// 이 표시의 의미를 "데미지가 들어갔다" 하나로 고정하기 위해서다.
    /// </summary>
    /// <param name="friendlyFire">동료를 맞혔는가 — 색이 달라진다. 소리로는 구분되지 않는다(둘 다 로봇)</param>
    public void ShowHit(bool friendlyFire)
    {
        if (m_hitMarker == null)
            return;

        Color color = friendlyFire ? m_friendlyFireColor : m_hitColor;
        foreach (Graphic graphic in HitMarkerGraphics)
            graphic.color = color;

        m_hitMarker.gameObject.SetActive(true);
        m_hitMarkerHideTime = Time.time + m_hitMarkerSeconds;
    }

    // 조준 피드백(SetWeaponTargeting)이 매 프레임 크로스헤어 색을 덮어쓰므로 히트마커를 색으로 낼 수 없다 —
    // 별개 오브젝트를 켜고 끈다. 그래서 끄는 일도 여기서 직접 해야 한다.
    private void Update()
    {
        if (m_hitMarker != null && m_hitMarker.gameObject.activeSelf && Time.time >= m_hitMarkerHideTime)
            m_hitMarker.gameObject.SetActive(false);
    }

    // 꺼져 있는 자식도 잡아야 하므로 includeInactive로 찾는다(마커는 평소 꺼져 있다).
    // Awake가 아니라 첫 명중에 캐시하는 이유: HUD는 오너 스폰 시 런타임 생성되고,
    // 한 판에 한 번도 안 때리는 플레이어(본부)는 이 비용을 아예 치르지 않는다.
    private Graphic[] m_hitMarkerGraphics;

    private Graphic[] HitMarkerGraphics =>
        m_hitMarkerGraphics ??= m_hitMarker.GetComponentsInChildren<Graphic>(true);
}
