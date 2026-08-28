using UnityEngine;
using UnityEngine.Localization;

public enum WantedPageDirection
{
    Prev = -1,
    Next = 1,
}

/// <summary>
/// 수배 리스트 페이지 버튼 — 모니터와 분리된 설치물. (#917)
/// <see cref="CCTVSwitchButton"/>과 같은 구조다: 표시 전환이라 네트워크가 필요 없다.
/// </summary>
public class WantedListPageButton : MonoBehaviour, IInteractable
{
    [SerializeField]
    private WantedListView m_view;

    [SerializeField]
    private WantedPageDirection m_direction = WantedPageDirection.Next;

    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.WantedPage;

    public void Interact(GameObject interactor)
    {
        if (!CanInteract(interactor))
            return;
        m_view.ChangePage((int)m_direction);
    }

    // 한 페이지뿐이면 눌러도 안 바뀌므로 윤곽선도 뜨지 않는다
    public bool CanInteract(GameObject interactor) => m_view != null && m_view.HasMultiplePages;
}
