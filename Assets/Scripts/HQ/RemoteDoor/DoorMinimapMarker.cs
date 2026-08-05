using UnityEngine;

/// <summary>
/// 문 하나의 본부 미니맵 마커 — 문 오브젝트에 붙인다. (#489)
/// 열림/잠김/닫힘을 <b>색조</b>로, 콘솔에서 선택된 문인지를 <b>밝기</b>로 구분한다.
///
/// <see cref="MinimapViewer"/>가 매 프레임 갱신하는 것은 아이콘의 색과 위치뿐이고 스프라이트는
/// 생성 시점에 한 번만 읽는다 — 그래서 런타임에 쓸 수 있는 표현 채널이 색 하나이고, 두 정보를
/// 색조와 밝기로 갈라 담는다.
///
/// 선택 여부는 <see cref="RemoteDoorConsole"/>이 Apply에서 직접 밀어준다 — 마커가 콘솔을 찾지
/// 않는다(매니저·전역 검색 금지, R1). <see cref="CCTVNode"/>와 같은 구조다.
/// </summary>
public class DoorMinimapMarker : MonoBehaviour
{
    [Header("대상 (비우면 자동 탐색)")]
    [SerializeField]
    private InteractableDoor m_door;

    [Tooltip("비우면 같은 오브젝트에서 자동 탐색")]
    [SerializeField]
    private MinimapTarget m_marker;

    [Header("상태 색조")]
    [SerializeField]
    private Color m_openColor = new Color(0.2f, 1f, 0.45f);

    [SerializeField]
    private Color m_lockedColor = new Color(1f, 0.3f, 0.3f);

    [SerializeField]
    private Color m_closedColor = new Color(0.55f, 0.6f, 0.7f);

    [Tooltip("선택되지 않은 문의 밝기 배율 — 1이면 선택된 문과 구분되지 않는다")]
    [Range(0.1f, 1f)]
    [SerializeField]
    private float m_unselectedDim = 0.45f;

    private bool m_isSelected;

    private void Awake()
    {
        if (m_door == null)
            m_door = GetComponentInParent<InteractableDoor>();
        if (m_marker == null)
            m_marker = GetComponent<MinimapTarget>();
    }

    private void OnEnable()
    {
        if (m_door != null)
            m_door.OnOpenChanged += HandleOpenChanged;
        Apply(); // 콘솔의 첫 Apply가 오기 전, 그리고 다시 켜졌을 때 현재 상태로 맞춘다
    }

    private void OnDisable()
    {
        if (m_door != null)
            m_door.OnOpenChanged -= HandleOpenChanged;
    }

    private void HandleOpenChanged(bool open) => Apply();

    /// <summary>이 문이 지금 본부 콘솔에서 선택된 문인지 — 콘솔이 밀어준다.</summary>
    public void SetSelected(bool selected)
    {
        m_isSelected = selected;
        Apply(); // 같은 값으로 다시 불려도 색을 새로 계산한다 — 문 상태가 그동안 바뀌었을 수 있다
    }

    private void Apply()
    {
        if (m_marker == null)
            return;

        Color color = StateColor;
        m_marker.IconColor = m_isSelected
            ? color
            : new Color(
                color.r * m_unselectedDim,
                color.g * m_unselectedDim,
                color.b * m_unselectedDim,
                color.a
            );
    }

    // 닫혀 있을 때만 잠금이 의미가 있다 — 본부가 열어 둔 문은 잠금과 무관하게 '열림'이다
    // (RemoteDoorListView.DescribeState와 같은 기준을 유지할 것)
    private Color StateColor
    {
        get
        {
            if (m_door == null)
                return m_closedColor;
            if (m_door.IsOpen)
                return m_openColor;
            return m_door.IsLocked ? m_lockedColor : m_closedColor;
        }
    }
}
