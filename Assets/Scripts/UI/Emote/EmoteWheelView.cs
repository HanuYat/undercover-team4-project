using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 감정표현 휠 — 8칸 방사형. 홀드 중에만 떠 있다. (#219)
///
/// <b>PanelBase가 아니다.</b> 패널은 ESC 스택에 쌓이고 열고 닫기가 App.UI를 거치는 '창'인데,
/// 이 휠은 키를 누르고 있는 동안만 떠 있는 조준 보조 표시라 ESC로 닫을 것도, 다른 창과 겹칠
/// 일도 없다. 스택에 넣으면 홀드를 뗀 순간의 닫기와 ESC 처리가 서로를 밟는다.
///
/// 칸 배치 각도를 <see cref="EmoteWheelGeometry"/>에서 받아 오는 이유는 <b>그림과 판정이
/// 같은 값을 봐야</b> 하기 때문이다. 각자 계산하면 한쪽만 고쳤을 때 "가리킨 것과 다른 게
/// 나오는" 어긋남이 조용히 생긴다.
/// </summary>
public class EmoteWheelView : MonoBehaviour
{
    [Tooltip("휠 전체 루트 — 닫을 때 끈다")]
    [SerializeField]
    private GameObject m_root;

    [Tooltip("칸 8개. 인덱스 = 슬롯 번호(0 = 12시, 시계방향)")]
    [SerializeField]
    private EmoteWheelSlotView[] m_slotViews = new EmoteWheelSlotView[EmoteWheelGeometry.k_slotCount];

    [Tooltip("칸 중심이 놓일 반경(px)")]
    [SerializeField]
    private float m_radius = 160f;

    private PlayerEmoteInput m_input;

    private void Awake()
    {
        LayOutSlots();
        Close();
    }

    /// <summary>휠을 띄우고 현재 구성을 채운다. 홀드가 시작될 때 PlayerEmoteInput이 부른다.</summary>
    public void Open(EmoteLoadout slots, EmoteCatalog catalog, PlayerEmoteInput input)
    {
        m_input = input;

        for (int slot = 0; slot < m_slotViews.Length; slot++)
        {
            if (m_slotViews[slot] == null)
                continue;

            // 저장된 id가 지금 카탈로그에 없으면 빈 칸으로 그린다 — EmoteLoadout은 카탈로그를
            // 모르므로(순수 로직) 유효성 판정은 읽는 쪽인 여기 몫이다.
            EmoteDefinition definition = null;
            if (catalog != null)
            {
                int index = catalog.IndexOf(slots?.GetSlot(slot));
                definition = catalog.Get(index);
            }

            m_slotViews[slot].Bind(definition);
            m_slotViews[slot].SetHighlighted(false);
        }

        if (m_root != null)
            m_root.SetActive(true);
    }

    /// <summary>휠을 내린다.</summary>
    public void Close()
    {
        m_input = null;

        if (m_root != null)
            m_root.SetActive(false);
    }

    private void Update()
    {
        if (m_input == null || m_root == null || !m_root.activeSelf)
            return;

        int highlighted = EmoteWheelGeometry.SlotFromDirection(m_input.WheelDirection);
        for (int slot = 0; slot < m_slotViews.Length; slot++)
        {
            if (m_slotViews[slot] != null)
                m_slotViews[slot].SetHighlighted(slot == highlighted);
        }
    }

    // 칸을 반경 위에 균등 배치한다 — 인스펙터에서 손으로 놓으면 판정 각도와 어긋나기 쉽다.
    private void LayOutSlots()
    {
        for (int slot = 0; slot < m_slotViews.Length; slot++)
        {
            if (m_slotViews[slot] == null)
                continue;

            float radians = EmoteWheelGeometry.SlotCenterDegrees(slot) * Mathf.Deg2Rad;
            var position = new Vector2(Mathf.Sin(radians), Mathf.Cos(radians)) * m_radius;
            m_slotViews[slot].SetAnchoredPosition(position);
        }
    }
}
