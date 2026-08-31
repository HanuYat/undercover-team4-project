// Assets/Scripts/UI/Panels/CrosshairSettingsPanel.cs
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 크로스헤어 커스터마이징 창 (#945) — 설정 창 Look 탭 버튼으로 연다. 모양 4종 토글, 색 스와치,
/// 크기·굵기 슬라이더를 <see cref="CosmeticLoadout"/>에 즉시 쓰고(설정 창의 "즉시 적용" 관례,
/// docs/design/settings-ui.md), 같은 값으로 미리보기(<see cref="CrosshairPreviewView"/>)를 다시 그린다.
/// </summary>
public class CrosshairSettingsPanel : PanelBase
{
    [Header("모양")]
    [SerializeField] private Toggle m_shapeCrossToggle;
    [SerializeField] private Toggle m_shapeDotToggle;
    [SerializeField] private Toggle m_shapeCrossDotToggle;
    [SerializeField] private Toggle m_shapeCircleToggle;

    [Header("색")]
    [SerializeField] private RectTransform m_swatchContainer;
    [SerializeField] private PlayerColorSwatchView m_swatchPrefab;
    [SerializeField] private PlayerColorPalette m_colorPalette;

    [Header("크기·굵기")]
    [SerializeField] private Slider m_sizeSlider;
    [SerializeField] private Slider m_thicknessSlider;

    [Header("미리보기")]
    [SerializeField] private CrosshairPreviewView m_preview;

    [SerializeField] private Button m_closeButton;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    private readonly System.Collections.Generic.List<PlayerColorSwatchView> m_swatches =
        new System.Collections.Generic.List<PlayerColorSwatchView>();

    protected override void Awake()
    {
        base.Awake();

        if (m_closeButton != null)
            m_closeButton.onClick.AddListener(ClosePanel);

        m_sizeSlider.minValue = CrosshairRenderer.k_minSize;
        m_sizeSlider.maxValue = CrosshairRenderer.k_maxSize;
        m_sizeSlider.wholeNumbers = false;
        m_sizeSlider.onValueChanged.AddListener(HandleSizeChanged);

        m_thicknessSlider.minValue = CrosshairRenderer.k_minThickness;
        m_thicknessSlider.maxValue = CrosshairRenderer.k_maxThickness;
        m_thicknessSlider.wholeNumbers = false;
        m_thicknessSlider.onValueChanged.AddListener(HandleThicknessChanged);

        m_shapeCrossToggle.onValueChanged.AddListener(isOn => { if (isOn) HandleShapeChanged(ECrosshairShape.Cross); });
        m_shapeDotToggle.onValueChanged.AddListener(isOn => { if (isOn) HandleShapeChanged(ECrosshairShape.Dot); });
        m_shapeCrossDotToggle.onValueChanged.AddListener(isOn => { if (isOn) HandleShapeChanged(ECrosshairShape.CrossDot); });
        m_shapeCircleToggle.onValueChanged.AddListener(isOn => { if (isOn) HandleShapeChanged(ECrosshairShape.Circle); });

        BuildSwatches();
    }

    private void BuildSwatches()
    {
        for (int i = 0; i < m_colorPalette.Count; i++)
        {
            int index = i;
            PlayerColorSwatchView swatch = Instantiate(m_swatchPrefab, m_swatchContainer);
            swatch.name = $"Swatch {index}";
            swatch.Bind(m_colorPalette.Get(index), () => HandleColorChanged(index));
            m_swatches.Add(swatch);
        }
    }

    public override void OpenPanel()
    {
        base.OpenPanel();
        SyncFromSettings();
    }

    // 설정 창 관례(SettingsPanel.SyncFromSettings)와 같다 — WithoutNotify로 넣어야 되먹임이 안 생긴다.
    private void SyncFromSettings()
    {
        CrosshairSettings settings = CosmeticLoadout.GetCrosshairSettings();

        m_shapeCrossToggle.SetIsOnWithoutNotify(settings.Shape == ECrosshairShape.Cross);
        m_shapeDotToggle.SetIsOnWithoutNotify(settings.Shape == ECrosshairShape.Dot);
        m_shapeCrossDotToggle.SetIsOnWithoutNotify(settings.Shape == ECrosshairShape.CrossDot);
        m_shapeCircleToggle.SetIsOnWithoutNotify(settings.Shape == ECrosshairShape.Circle);

        m_sizeSlider.SetValueWithoutNotify(settings.Size);
        m_thicknessSlider.SetValueWithoutNotify(settings.Thickness);

        for (int i = 0; i < m_swatches.Count; i++)
            m_swatches[i].SetSelected(i == settings.ColorIndex);

        RefreshPreview(settings);
    }

    private void HandleShapeChanged(ECrosshairShape shape) => ApplyChange(s => s.Shape = shape);
    private void HandleColorChanged(int index) => ApplyChange(s => s.ColorIndex = index);
    private void HandleSizeChanged(float value) => ApplyChange(s => s.Size = value);
    private void HandleThicknessChanged(float value) => ApplyChange(s => s.Thickness = value);

    // 현재 값을 복사해 한 필드만 바꾸고 그대로 저장한다 — CosmeticLoadout 쪽 값 자체를 직접
    // 변형하지 않는 이유는 참조 공유로 인한 사고를 막기 위해서다(§2 GetCrosshairSettings 주석 참고).
    private void ApplyChange(System.Action<CrosshairSettings> mutate)
    {
        CrosshairSettings settings = CosmeticLoadout.GetCrosshairSettings();
        CrosshairSettings next = new CrosshairSettings
        {
            Shape = settings.Shape,
            ColorIndex = settings.ColorIndex,
            Size = settings.Size,
            Thickness = settings.Thickness,
        };
        mutate(next);

        CosmeticLoadout.SetCrosshairSettings(next);

        for (int i = 0; i < m_swatches.Count; i++)
            m_swatches[i].SetSelected(i == next.ColorIndex);

        RefreshPreview(next);
    }

    private void RefreshPreview(CrosshairSettings settings)
    {
        if (m_preview != null)
            m_preview.Refresh(settings);
    }
}
