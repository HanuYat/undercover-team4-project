using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// CCTV 카메라 한 대의 본부 식별 정보 — 설치 위치 이름과 미니맵 마커를 들고 있다. (#362)
/// 카메라 오브젝트에 붙인다. 선택 여부는 CCTVSwitcher가 Apply()에서 직접 밀어준다
/// — 노드가 스위처를 찾지 않는다(매니저·전역 검색 금지, R1).
/// </summary>
public class CCTVNode : MonoBehaviour
{
    [Header("식별")]
    [Tooltip("본부 라벨에 표시할 설치 위치 — WorldTable의 World.Cctv.* (예: 본부 앞)")]
    [SerializeField]
    private LocalizedString m_locationLabel;

    [Header("미니맵 마커")]
    [Tooltip("비우면 같은 오브젝트에서 자동 탐색")]
    [SerializeField]
    private MinimapTarget m_marker;

    [SerializeField]
    private Color m_idleColor = new Color(0.45f, 0.45f, 0.5f);

    [SerializeField]
    private Color m_selectedColor = new Color(0.2f, 1f, 0.45f);

    /// <summary>
    /// 지금 언어로 읽은 설치 위치. 비어 있으면(미배선) 빈 문자열 — 라벨 쪽이 "CH3 · "처럼
    /// 구분자만 남지 않게 이 값으로 분기한다. (#497)
    /// </summary>
    public string LocationLabel =>
        m_locationLabel == null || m_locationLabel.IsEmpty
            ? string.Empty
            : m_locationLabel.GetLocalizedString();

    private void Awake()
    {
        if (m_marker == null)
            m_marker = GetComponent<MinimapTarget>();
        SetSelected(false); // 스위처의 첫 Apply()가 오기 전까지 비선택
    }

    /// <summary>이 카메라가 지금 모니터에 송출 중인지 — CCTVSwitcher가 밀어준다.</summary>
    public void SetSelected(bool selected)
    {
        if (m_marker != null)
            m_marker.IconColor = selected ? m_selectedColor : m_idleColor;
    }

    /// <summary>미니맵에 찍을 채널 번호 — CCTVSwitcher가 배열 순서에서 정해 한 번 밀어준다.</summary>
    public void SetChannel(int channel)
    {
        if (m_marker != null)
            m_marker.IconLabel = channel.ToString();
    }
}
