using UnityEngine;

/// <summary>
/// 부활 키트 자가 부활 — Down·Die 중 소지자 본인이 E를 홀드해 스스로 일으키는 입력 훅. (#820)
/// 채널링·서버 판정은 <see cref="ReviveKit"/>(소지품 자신)이 담당한다 — 이 컴포넌트는 E 입력을
/// 지금 들고 있는 키트로 넘기기만 하는 얇은 다리다.
///
/// <b>plain MonoBehaviour인 이유.</b> Die 중에는 플레이어 오브젝트의 오너가 서버로 이관되지만
/// (PlayerIncapacitation, #763), <see cref="PlayerInputHandler"/>는 <b>스폰 시점에 굳힌 오너 판정</b>
/// (#774)으로 비오너에서 스스로 비활성화되어 이벤트를 아예 발행하지 않는다 — 그래서 이 컴포넌트는
/// IsOwner를 따로 물을 필요가 없다: 이벤트가 온다는 것 자체가 곧 "내 것"이라는 뜻이다.
/// </summary>
[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerSelfRevive : MonoBehaviour
{
    private PlayerInputHandler m_inputHandler;
    private PlayerLoadout m_loadout;
    private PlayerIncapacitation m_incapacitation;

    private void Awake()
    {
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_loadout = GetComponent<PlayerLoadout>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
    }

    private void OnEnable()
    {
        m_inputHandler.OnInteractStarted += HandleInteractStarted;
        m_inputHandler.OnInteractCanceled += HandleInteractCanceled;
    }

    private void OnDisable()
    {
        m_inputHandler.OnInteractStarted -= HandleInteractStarted;
        m_inputHandler.OnInteractCanceled -= HandleInteractCanceled;
    }

    /// <summary>지금 소지 중인 부활 키트(없으면 null) — HUD 안내가 "들고 있다"를 판정하는 데 쓴다. (#820)</summary>
    public ReviveKit HeldKit => m_loadout != null ? m_loadout.HeldReviveKit : null;

    /// <summary>지금 자가 부활을 시도할 수 있는가 — Down 또는 Die(몸이 회수 가능한 경우)이고 키트를 들고 있을 때. (#820)</summary>
    public bool CanSelfRevive =>
        m_incapacitation != null
        && (m_incapacitation.IsDowned || m_incapacitation.IsRevivable)
        && HeldKit != null;

    // E를 누르는 순간 — 커서가 풀려 있으면(정산·일시정지 화면 클릭) UI 것이다.
    // PlayerItemUser.HandleUseItem(#352)과 같은 게이트.
    private void HandleInteractStarted()
    {
        if (CursorLock.IsUnlocked)
            return;

        ReviveKit kit = HeldKit;
        if (kit == null || !CanSelfRevive)
            return;

        kit.RequestSelfRevive();
    }

    // E를 떼는 순간 — 채널링 중이 아니면 ReviveKit.RequestCancelSelfRevive가 무동작으로 넘어간다.
    private void HandleInteractCanceled()
    {
        HeldKit?.RequestCancelSelfRevive();
    }
}
