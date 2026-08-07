using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 머리 위 이모지 아이콘 — 감정표현 재생 중에만 뜬다. 전 피어에서 돈다. (#219)
///
/// 이름표(<see cref="PlayerNameTag"/>)와 같은 자리에 서지만 컴포넌트를 합치지 않았다 —
/// 이름표는 거리·음소거·발화를 보고 상시 떠 있고, 이모지는 재생 중에만 뜬다. 표시 조건이
/// 전혀 다른 둘을 한 컴포넌트에 두면 어느 조건이 어느 그림을 끄는지 읽기 어려워진다.
///
/// 빌보드는 매 프레임 카메라를 향하게 돌린다 — 월드 공간에 놓인 스프라이트라 그대로 두면
/// 옆에서 볼 때 납작해진다.
/// </summary>
public class EmoteBubbleView : MonoBehaviour
{
    [Tooltip("이모지를 그릴 Image — 감정표현이 없을 때는 이 오브젝트를 끈다")]
    [SerializeField]
    private Image m_icon;

    [Tooltip("카메라를 향해 돌릴 루트. 비우면 아이콘의 부모를 쓴다")]
    [SerializeField]
    private Transform m_billboardRoot;

    [SerializeField]
    private PlayerEmoteView m_emoteView;

    private Transform m_camera;

    private void Awake()
    {
        if (m_emoteView == null)
            m_emoteView = GetComponentInParent<PlayerEmoteView>();

        if (m_billboardRoot == null && m_icon != null)
            m_billboardRoot = m_icon.transform.parent;

        Hide();
    }

    private void OnEnable()
    {
        if (m_emoteView != null)
            m_emoteView.OnEmoteVisualChanged += HandleEmoteVisualChanged;
    }

    private void OnDisable()
    {
        if (m_emoteView != null)
            m_emoteView.OnEmoteVisualChanged -= HandleEmoteVisualChanged;

        Hide();
    }

    private void LateUpdate()
    {
        if (m_billboardRoot == null || m_icon == null || !m_icon.gameObject.activeSelf)
            return;

        // 카메라는 씬 로드·시점 전환으로 바뀔 수 있어 매번 확인한다 — 캐시만 하면 낡은 참조로 남는다.
        if (m_camera == null)
        {
            if (Camera.main == null)
                return;

            m_camera = Camera.main.transform;
        }

        m_billboardRoot.forward = m_camera.forward;
    }

    private void HandleEmoteVisualChanged(EmoteDefinition definition)
    {
        // 클립만 있고 이모지가 없는 감정표현(순수 댄스)은 머리 위에 아무것도 띄우지 않는다.
        if (definition == null || definition.BubbleSprite == null)
        {
            Hide();
            return;
        }

        if (m_icon == null)
            return;

        m_icon.sprite = definition.BubbleSprite;
        m_icon.gameObject.SetActive(true);
    }

    private void Hide()
    {
        if (m_icon != null)
            m_icon.gameObject.SetActive(false);
    }
}
