using System.Collections.Generic;
using UnityEngine;

// 미니맵에 표시할 대상에 붙인다. 활성화되면 스스로 레지스트리에 등록된다.
public class MinimapTarget : MonoBehaviour
{
    public static readonly List<MinimapTarget> ActiveTargets = new();

    [SerializeField] private Sprite m_iconSprite;   // 비우면 컨트롤러 기본 아이콘 사용
    [SerializeField] private Color m_iconColor = Color.blue;

    [Header("범위 오버레이 (#610)")]
    [Tooltip("이 대상이 덮는 월드 반경(m). 0이면 점만 찍는다 — 폭발 반경처럼 '얼마나 넓게'를 알려야 할 때만 채운다")]
    [Min(0f)]
    [SerializeField] private float m_areaRadius = 0f;

    [Tooltip("범위 오버레이 색 — 알파를 낮게 둬야 밑의 맵이 비친다")]
    [SerializeField] private Color m_areaColor = new Color(1f, 0.25f, 0.25f, 0.25f);

    public Sprite IconSprite => m_iconSprite;

    /// <summary>아이콘 색 — 런타임 변경 가능. MinimapViewer가 매 프레임 반영한다. (#362 CCTV 선택 하이라이트)</summary>
    public Color IconColor
    {
        get => m_iconColor;
        set => m_iconColor = value;
    }

    /// <summary>
    /// 범위 오버레이의 월드 반경(m) — 0이면 오버레이가 없다. (#610)
    ///
    /// <b>여기 두는 이유</b>: 읽는 쪽(MinimapViewer)이 각 피어에서 로컬로 도는데, 이벤트 구현체의 상태는
    /// 서버에만 있어 ISuddenEvent에서 꺼내면 호스트 화면에만 뜬다. 반경을 프리팹 직렬화값으로 두면
    /// 전 피어가 같은 값을 갖고, 위치는 스폰물의 NetworkTransform이 이미 복제하므로 복제가 공짜다.
    /// </summary>
    public float AreaRadius
    {
        get => m_areaRadius;
        set => m_areaRadius = value;
    }

    /// <summary>범위 오버레이 색 — IconColor와 같은 이유로 런타임 변경 가능.</summary>
    public Color AreaColor
    {
        get => m_areaColor;
        set => m_areaColor = value;
    }

    private void OnEnable() => ActiveTargets.Add(this);
    private void OnDisable() => ActiveTargets.Remove(this);
}
