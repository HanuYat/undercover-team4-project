using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 로비에서 자기 로봇 색을 고르는 창 (#432) — 감정표현 휠 구성과 같은 자리·같은 방식이다.
/// 칸을 그리는 일은 부위마다 <see cref="PlayerColorPickerView"/>가 맡고, 창은 열고 닫기와
/// 미리보기만 맡는다.
/// </summary>
public class PlayerColorPanel : PanelBase
{
    [Tooltip("내 로봇 전신 미리보기 — 비워 두면 미리보기 없이 팔레트만 보인다")]
    [SerializeField] private RawImage m_preview;

    [Tooltip("얼굴을 굽는 무대 — 로비 카드와 같은 것을 물린다")]
    [SerializeField] private LobbyPortraitStage m_portraitStage;

    [SerializeField] private Button m_closeButton;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    protected override void Awake()
    {
        base.Awake();

        if (m_closeButton != null)
            m_closeButton.onClick.AddListener(ClosePanel);
    }

    public override void OpenPanel()
    {
        base.OpenPanel();
        SetBlocked(true);
        RefreshPreview();
    }

    /// <summary>닫기 — ESC·닫기 버튼·씬 정리가 모두 여기로 모인다. 커서를 반드시 여기서 되돌린다.</summary>
    public override void ClosePanel()
    {
        if (!IsOpened)
            return; // 중복 호출로 CursorLock 참조 수가 어긋나지 않게

        base.ClosePanel();
        SetBlocked(false);
    }

    // 창이 열린 채 사라지면(씬 전환) 커서 해제 요청을 되돌릴 주체가 없어진다 — LootPanel과 같은 사정
    private void OnDisable()
    {
        HandleDisabled();
        SetBlocked(false);
    }

    // 커서를 푼다 — 로비는 원래 풀려 있지만 상점 락커(#818)로 열 때는 잠긴 상태에서 들어온다.
    // 래치 덕에 몇 번 불려도 Push/Pop은 1:1로 유지된다.
    private void SetBlocked(bool blocked)
    {
        if (m_blocked == blocked)
            return;

        m_blocked = blocked;

        if (blocked)
            CursorLock.PushUnlock();
        else
            CursorLock.PopUnlock();

        // 커서를 푼 채 WASD가 이동으로 새지 않게 — 상점에서는 내 로봇이 살아 있다
        PlayerInputHandler input = FindLocalInput();
        if (input != null)
            input.SetSuspended(blocked);
    }

    private static PlayerInputHandler FindLocalInput()
    {
        Unity.Netcode.NetworkManager manager = Unity.Netcode.NetworkManager.Singleton;
        Unity.Netcode.NetworkObject player =
            manager != null && manager.IsListening ? manager.LocalClient.PlayerObject : null;

        return player != null ? player.GetComponent<PlayerInputHandler>() : null;
    }

    private bool m_blocked;

    private void OnEnable()
    {
        CosmeticLoadout.OnPlayerColorChanged += HandleColorChanged;
        CosmeticLoadout.OnAccessoryChanged += HandleAccessoryChanged;
    }

    private void HandleDisabled()
    {
        CosmeticLoadout.OnPlayerColorChanged -= HandleColorChanged;
        CosmeticLoadout.OnAccessoryChanged -= HandleAccessoryChanged;
    }

    private void HandleColorChanged(EBodyPart _) => RefreshPreview();

    // 치장도 같은 자리에서 되그린다 — 무대가 전신 미리보기에만 태운다 (#818)
    private void HandleAccessoryChanged(EAccessorySlot _) => RefreshPreview();

    // 그림은 무대가 그린다 — 창은 어느 것을 볼지만 정한다
    private void RefreshPreview()
    {
        if (m_preview == null || m_portraitStage == null)
            return;

        m_preview.texture = m_portraitStage.BodyPreview;
    }
}
