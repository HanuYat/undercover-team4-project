using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 상점의 치장 락커 (#818) — E를 누르면 로비와 같은 커스터마이징 창을 연다.
///
/// 창을 새로 만들지 않는다: 같은 <see cref="PlayerColorPanel"/> 프리팹이 이 씬에도 놓여 있고,
/// 패널은 <c>PanelBase</c>가 스스로 등록하므로 여기서는 열기만 한다. 색·치장 모두 여기서 바꾸며,
/// 바뀐 값은 살아 있는 내 로봇에 바로 반영된다(PlayerCosmetics·PlayerAccessories의 오너 경로).
///
/// <b>순수 로컬 동작이다</b> — 서버에 아무것도 묻지 않는다. 값 전파는 이미 있는 길을 탄다.
/// </summary>
public class CosmeticLocker : MonoBehaviour, IInteractable
{
    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.Cosmetics;

    public bool CanInteract(GameObject interactor) => FindPanel() != null;

    public void Interact(GameObject interactor)
    {
        PlayerColorPanel panel = FindPanel();
        if (panel == null)
        {
            Debug.LogWarning($"[{nameof(CosmeticLocker)}] 이 씬에 커스터마이징 창이 없습니다 (#818)", this);
            return;
        }

        panel.OpenPanel();
    }

    // 패널은 스스로 UI 매니저에 등록한다 — 락커가 인스펙터로 물고 있지 않는 이유다
    // (씬에 락커가 여럿이라 배선을 여러 벌 유지해야 한다).
    private static PlayerColorPanel FindPanel() =>
        App.UI.Current != null && App.UI.Current.TryGetPanel(out PlayerColorPanel panel) ? panel : null;
}
