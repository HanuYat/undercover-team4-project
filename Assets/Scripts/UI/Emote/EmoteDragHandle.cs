using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 휠 구성 드래그 손잡이 — 카탈로그 항목과 휠 8칸 <b>양쪽</b>에 같은 것이 붙는다. (#219)
///
/// <b>런타임에 붙인다</b> (<see cref="Attach"/>) — 칸은 Lobby 씬에 놓여 있어 손으로 붙이면 씬에 diff가
/// 남는데, 씬은 사실상 머지가 안 되므로 같은 씬을 건드리는 다른 작업과 부딪힌다.
///
/// 출발지·도착지를 스스로 판단하지 않고 <see cref="EmoteLoadoutPanel"/>에 넘긴다 — 구성 규칙(넣기·
/// 자리 바꾸기·비우기)을 아는 것은 구성을 들고 있는 쪽이다.
/// </summary>
public class EmoteDragHandle
    : MonoBehaviour,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler,
        IDropHandler
{
    /// <summary>이 손잡이가 무엇에 붙어 있는가 — 카탈로그 항목인지 휠 칸인지.</summary>
    public enum EKind
    {
        Catalog,
        Slot,
    }

    private const float k_ghostAlpha = 0.75f;
    private const string k_dropAreaName = "~DropArea";

    // 끌고 있는 손잡이 — 받는 쪽(OnDrop)이 출발지를 알아야 한다. 포인터 하나에 드래그도 하나뿐이다.
    private static EmoteDragHandle s_dragging;

    private EmoteLoadoutPanel m_panel;
    private EKind m_kind;
    private int m_index;
    private RectTransform m_ghost;
    private bool m_handled; // 칸 위에 떨어졌는가 — 아니면 '바깥에 버렸다'로 간다

    public EKind Kind => m_kind;

    /// <summary>카탈로그 인덱스(<see cref="EKind.Catalog"/>) 또는 휠 칸 번호(<see cref="EKind.Slot"/>).</summary>
    public int Index => m_index;

    /// <summary>손잡이를 붙이거나 이미 붙은 것을 다시 배선한다 — 패널이 목록을 만들 때 부른다.</summary>
    public static void Attach(EmoteLoadoutPanel panel, GameObject target, EKind kind, int index)
    {
        if (target == null)
            return;

        EmoteDragHandle handle = target.GetComponent<EmoteDragHandle>();
        if (handle == null)
            handle = target.AddComponent<EmoteDragHandle>();

        handle.m_panel = panel;
        handle.m_kind = kind;
        handle.m_index = index;
        handle.EnsureDropArea();
    }

    // 빈 칸도 드롭을 받아야 한다 — 아이콘이 꺼져 있으면(EmoteWheelSlotView.Bind) 레이캐스트가 그대로
    // 통과해 그 칸에는 아무것도 떨어뜨릴 수 없다. 투명 판을 하나 깔아 둔다.
    private void EnsureDropArea()
    {
        if (transform.Find(k_dropAreaName) != null)
            return;

        var area = new GameObject(k_dropAreaName, typeof(RectTransform), typeof(Image));
        var rect = (RectTransform)area.transform;
        rect.SetParent(transform, false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        rect.SetAsFirstSibling(); // 아이콘·라벨 뒤에 깔린다

        Image image = area.GetComponent<Image>();
        image.color = new Color(0f, 0f, 0f, 0f);
        image.raycastTarget = true;
    }

    public void OnBeginDrag(PointerEventData eventData)
    {
        Sprite sprite = m_panel != null ? m_panel.ResolveIcon(this) : null;
        if (sprite == null)
            return; // 빈 칸 — 끌고 갈 것이 없다

        s_dragging = this;
        m_handled = false;
        CreateGhost(sprite);
        MoveGhost(eventData);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (s_dragging == this)
            MoveGhost(eventData);
    }

    public void OnDrop(PointerEventData eventData)
    {
        if (s_dragging == null)
            return;

        // 제자리에 도로 놓았다 — 아무 일도 없지만 '바깥에 버렸다'로 새면 그 칸이 지워진다
        if (s_dragging == this)
        {
            m_handled = true;
            return;
        }

        s_dragging.m_handled = true;
        if (m_panel != null)
            m_panel.HandleDrop(s_dragging, this);
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        if (s_dragging != this)
            return;

        DestroyGhost();
        s_dragging = null;

        // 손잡이 위에 떨어졌으면 OnDrop이 이미 처리했다(그쪽이 먼저 온다) — 여기 남는 것은 허공이다
        if (!m_handled && m_panel != null)
            m_panel.HandleDropOutside(this);

        m_handled = false;
    }

    private void OnDisable()
    {
        if (s_dragging == this)
            s_dragging = null;

        DestroyGhost();
    }

    // ---- 커서를 따라다니는 반투명 아이콘 ----

    private void CreateGhost(Sprite sprite)
    {
        Canvas canvas = GetComponentInParent<Canvas>();
        if (canvas == null)
            return;

        var ghost = new GameObject("~EmoteDragGhost", typeof(RectTransform), typeof(Image));
        m_ghost = (RectTransform)ghost.transform;
        m_ghost.SetParent(canvas.rootCanvas.transform, false);
        m_ghost.sizeDelta = ((RectTransform)transform).rect.size;
        m_ghost.SetAsLastSibling(); // 무엇보다 위에 뜬다

        Image image = ghost.GetComponent<Image>();
        image.sprite = sprite;
        image.raycastTarget = false; // 이걸 켜면 자기 자신이 드롭 대상을 가린다
        image.color = new Color(1f, 1f, 1f, k_ghostAlpha);
    }

    private void MoveGhost(PointerEventData eventData)
    {
        if (m_ghost == null)
            return;

        var parent = m_ghost.parent as RectTransform;
        Canvas canvas = m_ghost.GetComponentInParent<Canvas>();
        if (parent == null || canvas == null)
            return;

        Camera camera =
            canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        if (
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                parent,
                eventData.position,
                camera,
                out Vector2 local
            )
        )
            m_ghost.localPosition = local;
    }

    private void DestroyGhost()
    {
        if (m_ghost == null)
            return;

        Destroy(m_ghost.gameObject);
        m_ghost = null;
    }
}
