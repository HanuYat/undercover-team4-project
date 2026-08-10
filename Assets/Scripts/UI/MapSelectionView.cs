using TMPro;
using UnityEngine;

/// <summary>
/// Shop 씬 맵 선택 콘솔의 표시 — 다음 라운드에 갈 맵 이름을 찍는다. (#578)
/// 홀더 상태를 구독만 하는 표시 전용이고 <b>전원에게 보인다</b>(고르는 건 호스트뿐이지만,
/// 어디로 출동하는지는 전원이 알아야 한다). RemoteDoorListView와 같은 구조. (#489)
///
/// 홀더는 런타임 스폰물이라 인스펙터로 못 잡는다 — App 파사드로 조회하고, 아직 스폰 전일 수 있어
/// 구독 시점을 Update에서 잡는다(LoadingScreen이 NGO SceneManager를 다시 거는 것과 같은 이유).
/// </summary>
public class MapSelectionView : MonoBehaviour
{
    [SerializeField]
    private TMP_Text m_label;

    [Tooltip("표시 형식 — {0}=맵 이름")]
    [SerializeField]
    private string m_format = "다음 출동지: {0}";

    // 지금 구독 중인 홀더 — 세션마다 새로 스폰되므로 참조가 바뀐 프레임에만 다시 건다
    private MapSelection m_bound;

    private void OnDisable() => Bind(null);

    private void Update()
    {
        MapSelection current = App.Game.MapSelection;
        if (!ReferenceEquals(current, m_bound))
            Bind(current);
    }

    private void Bind(MapSelection target)
    {
        if (m_bound != null)
            m_bound.OnSelectionChanged -= Refresh;

        m_bound = target;

        if (m_bound != null)
            m_bound.OnSelectionChanged += Refresh;

        Refresh();
    }

    private void Refresh()
    {
        if (m_label == null)
            return;

        // 세션 밖(씬 직접 Play)이거나 아직 스폰 전 — 표시할 상태가 없다. AppHelper가 기본 맵으로 간다.
        if (m_bound == null || !m_bound.IsSpawned)
        {
            m_label.text = string.Empty;
            return;
        }

        // 칸이 통째로 비어 있으면(씬 이름조차 없음) 배선 실수라 화면에 드러낸다 — RemoteDoorListView와 같다
        string name = m_bound.SelectedDisplayName;
        m_label.text = string.Format(m_format, name ?? "(미배선)");
    }
}
