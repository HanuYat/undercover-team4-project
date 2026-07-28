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
}
