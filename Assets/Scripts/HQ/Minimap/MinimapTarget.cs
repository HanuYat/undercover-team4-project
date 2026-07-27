using System.Collections.Generic;
using UnityEngine;

// 미니맵에 표시할 대상에 붙인다. 활성화되면 스스로 레지스트리에 등록된다.
public class MinimapTarget : MonoBehaviour
{
    public static readonly List<MinimapTarget> ActiveTargets = new();

    [SerializeField] private Sprite m_iconSprite;   // 비우면 컨트롤러 기본 아이콘 사용
    [SerializeField] private Color m_iconColor = Color.blue;

    public Sprite IconSprite => m_iconSprite;

    /// <summary>아이콘 색 — 런타임 변경 가능. MinimapViewer가 매 프레임 반영한다. (#362 CCTV 선택 하이라이트)</summary>
    public Color IconColor
    {
        get => m_iconColor;
        set => m_iconColor = value;
    }

    private void OnEnable() => ActiveTargets.Add(this);
    private void OnDisable() => ActiveTargets.Remove(this);
}
