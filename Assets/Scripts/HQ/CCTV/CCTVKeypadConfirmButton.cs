using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// CCTV 키패드의 확인 버튼 — 쌓인 번호로 채널을 옮긴다. 없는 번호를 넣었으면 입력만 비운다. (#362 연장)
/// </summary>
public class CCTVKeypadConfirmButton : MonoBehaviour, IInteractable
{
    [SerializeField]
    private CCTVSwitcher m_switcher;

    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.CctvKeypadConfirm;

    public void Interact(GameObject interactor)
    {
        if (!CanInteract(interactor))
            return;
        m_switcher.RequestConfirmEntryRpc();
    }

    // 입력이 없으면 확인할 것이 없다 — 그때는 윤곽선도 뜨지 않는다
    public bool CanInteract(GameObject interactor) =>
        m_switcher != null
        && m_switcher.IsSpawned
        && m_switcher.PendingEntry >= 0
        && m_switcher.IsPowered
        && !m_switcher.IsExternallyJammed;
}
