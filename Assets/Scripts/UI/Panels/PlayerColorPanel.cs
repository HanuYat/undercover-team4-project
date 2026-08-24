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
        RefreshPreview();
    }

    private void OnEnable()
    {
        GameSettings.OnPlayerColorChanged += HandleColorChanged;
        GameSettings.OnAccessoryChanged += HandleAccessoryChanged;
    }

    private void OnDisable()
    {
        GameSettings.OnPlayerColorChanged -= HandleColorChanged;
        GameSettings.OnAccessoryChanged -= HandleAccessoryChanged;
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
