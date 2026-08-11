using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 배경 그림을 부모 사각형에 꽉 채우고(cover), 남는 쪽을 <b>어디서 버릴지</b> 정한다. (#585)
///
/// <b>왜 AspectRatioFitter를 쓰지 않는가</b> — 그 컴포넌트의 EnvelopeParent 모드는 채우기는
/// 해주지만 <c>anchoredPosition</c>을 0으로 강제해 세로 정렬을 지정할 수 없다. 타이틀 배경은
/// 위쪽에 게임 로고가 박혀 있어서 중앙 정렬로 채우면 로고가 잘린다.
///
/// <b>왜 크기를 손으로 박으면 안 되는가</b> — 실제로 그렇게 했다가 회귀를 냈다. 1920x1080
/// 기준으로 계산한 사각형은 그 비율에서만 맞고, MPPM 가상 플레이어 창처럼 다른 비율에서는
/// 화면을 못 덮어 그 바깥으로 카메라 클리어 색이 새어 나온다. 채울 크기는 <b>실제 부모
/// 사각형</b>에서 매번 계산해야 한다.
/// </summary>
[RequireComponent(typeof(RectTransform))]
[ExecuteAlways]
public class BackdropCover : MonoBehaviour
{
    [Tooltip("비우면 Image의 스프라이트에서 자동으로 읽는다 — 그림을 갈아도 값이 어긋나지 않는다")]
    [SerializeField]
    private Image m_source;

    [Tooltip("그림의 가로/세로. m_source가 있으면 그쪽이 우선한다")]
    [SerializeField]
    private float m_aspect = 4f / 3f;

    [Tooltip("세로로 남는 부분을 어디서 버릴지 — 0이면 위아래 균등, 1이면 그림 위쪽을 화면 위에 붙인다(위를 최대한 살림)")]
    [Range(0f, 1f)]
    [SerializeField]
    private float m_topBias = 0.78f;

    private RectTransform m_rect;
    private Vector2 m_lastParentSize = new Vector2(float.NaN, float.NaN);
    private float m_lastAspect = float.NaN;
    private float m_lastBias = float.NaN;

    private void OnEnable()
    {
        m_rect = (RectTransform)transform;
        Invalidate();
        Apply();
    }

#if UNITY_EDITOR
    private void OnValidate() => Invalidate();
#endif

    private void Invalidate() => m_lastParentSize = new Vector2(float.NaN, float.NaN);

    // 부모 크기는 창 크기·해상도·CanvasScaler에 따라 언제든 바뀐다. 이벤트로 잡으려면
    // OnRectTransformDimensionsChange를 쓰게 되는데, 그 안에서 자기 사각형을 고치면 다시
    // 같은 콜백이 불려 재귀가 된다. 크기를 캐시해 두고 바뀐 프레임에만 다시 계산한다.
    private void Update() => Apply();

    private void Apply()
    {
        if (m_rect == null)
            m_rect = (RectTransform)transform;

        var parent = m_rect.parent as RectTransform;
        if (parent == null)
            return;

        float aspect = ResolveAspect();
        if (aspect <= 0f)
            return;

        Vector2 parentSize = parent.rect.size;
        if (parentSize.x <= 0f || parentSize.y <= 0f)
            return;

        if (
            parentSize == m_lastParentSize
            && Mathf.Approximately(aspect, m_lastAspect)
            && Mathf.Approximately(m_topBias, m_lastBias)
        )
            return;

        m_lastParentSize = parentSize;
        m_lastAspect = aspect;
        m_lastBias = m_topBias;

        // 폭을 맞췄을 때 높이가 부족하면 높이 기준으로 맞춘다 — 어느 쪽이든 넘치게(cover)
        float byWidth = parentSize.x / aspect;
        Vector2 size =
            byWidth >= parentSize.y
                ? new Vector2(parentSize.x, byWidth)
                : new Vector2(parentSize.y * aspect, parentSize.y);

        // 세로로 넘치는 만큼을 위/아래에 어떻게 나눌지. bias 1이면 넘친 전부를 아래에서 버린다.
        float excessY = Mathf.Max(0f, size.y - parentSize.y);

        m_rect.anchorMin = m_rect.anchorMax = new Vector2(0.5f, 0.5f);
        m_rect.pivot = new Vector2(0.5f, 0.5f);
        m_rect.sizeDelta = size;
        m_rect.anchoredPosition = new Vector2(0f, -excessY * 0.5f * m_topBias);
    }

    private float ResolveAspect()
    {
        Sprite sprite = m_source != null ? m_source.sprite : null;
        if (sprite != null && sprite.rect.height > 0f)
            return sprite.rect.width / sprite.rect.height;

        return m_aspect;
    }
}
