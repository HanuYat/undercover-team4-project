using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// CCTV 콘솔 키패드의 숫자 버튼 — 누르는 만큼 번호가 쌓이고 확인 버튼이 그 채널을 띄운다. (#362 연장)
/// 채널마다 버튼을 두지 않는다: 카메라가 수십 개로 늘어도 키패드는 11개로 고정이다.
/// </summary>
public class CCTVKeypadButton : MonoBehaviour, IInteractable
{
    [SerializeField]
    private CCTVSwitcher m_switcher;

    [Tooltip("이 버튼의 숫자")]
    [Range(0, 9)]
    [SerializeField]
    private int m_digit;

    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.CctvKeypad;

    public void Interact(GameObject interactor)
    {
        if (!CanInteract(interactor))
            return;
        m_switcher.RequestAppendDigitRpc(m_digit);
    }

    // RequestAppendDigitRpc의 서버 가드와 같은 기준
    public bool CanInteract(GameObject interactor) =>
        m_switcher != null
        && m_switcher.IsSpawned
        && m_switcher.IsPowered
        && !m_switcher.IsExternallyJammed;
}
