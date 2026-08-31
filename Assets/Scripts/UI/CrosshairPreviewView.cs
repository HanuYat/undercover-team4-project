// Assets/Scripts/UI/CrosshairPreviewView.cs
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 크로스헤어 설정 패널의 실시간 미리보기 (#945) — 실제 게임 크로스헤어(HUD의 CrosshairUI)와
/// 같은 <see cref="CrosshairRenderer"/> 계산을 쓰므로 미리보기와 실제가 항상 같은 그림이 된다.
/// CommonManagerBase가 아닌 평범한 컴포넌트다 — 씬에 이미 있는 CrosshairUI와 별개로,
/// 패널이 열려 있는 동안만 켜지는 인스턴스라 매니저 등록이 필요 없다.
/// </summary>
public class CrosshairPreviewView : MonoBehaviour
{
    [SerializeField]
    private RectTransform m_lineUp;

    [SerializeField]
    private RectTransform m_lineDown;

    [SerializeField]
    private RectTransform m_lineLeft;

    [SerializeField]
    private RectTransform m_lineRight;

    [SerializeField]
    private RectTransform m_dot;

    [SerializeField]
    private RectTransform m_circleRing;

    [SerializeField]
    private Image m_circleImage;

    [SerializeField]
    private PlayerColorPalette m_colorPalette;

    private CrosshairVisualRefs VisualRefs =>
        new CrosshairVisualRefs
        {
            Up = m_lineUp,
            Down = m_lineDown,
            Left = m_lineLeft,
            Right = m_lineRight,
            Dot = m_dot,
            CircleRing = m_circleRing,
            CircleImage = m_circleImage,
        };

    /// <summary>패널이 열려 있는 동안 슬라이더·모양·색이 바뀔 때마다 부른다.</summary>
    public void Refresh(CrosshairSettings settings)
    {
        CrosshairRenderer.ApplyShape(VisualRefs, settings);
        CrosshairRenderer.ApplyColor(VisualRefs, settings, null, m_colorPalette);
    }
}
