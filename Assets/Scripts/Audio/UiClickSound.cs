using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// UI 버튼 클릭음 — 눌린 곳이 버튼이면 소리를 낸다. AppBootstrap 상주라 모든 씬에 걸린다.
///
/// <b>버튼마다 배선하지 않는 이유</b> — 버튼은 씬·프리팹에 흩어져 있고 로비 명단·인벤토리 슬롯처럼
/// 런타임에 생기는 것도 있다. onClick에 하나씩 다는 방식은 새 버튼이 늘 때마다 빠뜨린다.
/// 여기서는 눌린 좌표를 EventSystem에 한 번 물어 그 아래가 버튼인지만 본다 — 배선이 필요 없다.
///
/// <b>onClick(뗄 때)이 아니라 누를 때 낸다</b> — 눌린 즉시 나야 조작에 붙어 들린다.
/// </summary>
public class UiClickSound : MonoBehaviour
{
    [Tooltip("낼 소리. None이면 클릭음이 나지 않는다")]
    [SerializeField] private EAudioClip m_clip = EAudioClip.UiClick;

    private readonly List<RaycastResult> m_hits = new();

    private void Update()
    {
        if (m_clip == EAudioClip.None)
            return;

        // 마우스·터치를 함께 받는다. 커서가 잠긴 인게임에서도 눌리지만 조준점 아래에 버튼이 없어 조용하다.
        Pointer pointer = Pointer.current;
        if (pointer == null || !pointer.press.wasPressedThisFrame)
            return;

        EventSystem events = EventSystem.current;
        if (events == null)
            return;

        var data = new PointerEventData(events) { position = pointer.position.ReadValue() };
        m_hits.Clear();
        events.RaycastAll(data, m_hits);
        if (m_hits.Count == 0)
            return;

        // 맨 위에 걸린 것만 본다 — 그 아래는 어차피 클릭을 못 받는다(창이 덮고 있으면 창이 먹는다).
        // 버튼의 배경·글자가 걸리므로 부모 쪽으로 올라가며 버튼을 찾는다.
        Selectable target = m_hits[0].gameObject.GetComponentInParent<Selectable>();
        if (target == null || !target.IsInteractable())
            return;

        // 슬라이더·입력창을 잡는 것은 '누름'이 아니다 — 눌러서 끝나는 것만 소리를 낸다.
        if (target is not Button && target is not Toggle)
            return;

        App.Sound?.PlaySfx2D(m_clip);
    }
}
