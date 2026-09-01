using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

// 미니맵에 표시할 대상에 붙인다. 활성화되면 스스로 레지스트리에 등록된다.
public class MinimapTarget : MonoBehaviour
{
    public static readonly List<MinimapTarget> ActiveTargets = new();

    [Tooltip("휴대용 미니맵의 카테고리 필터가 이 값으로 대상을 거른다 (#835)")]
    [SerializeField] private EMinimapMarker m_category = EMinimapMarker.None;

    [SerializeField] private Sprite m_iconSprite;   // 비우면 컨트롤러 기본 아이콘 사용
    [SerializeField] private Color m_iconColor = Color.blue;

    [Tooltip("아이콘 한 변의 크기(px). 0이면 아이콘 프리팹 크기를 그대로 쓴다")]
    [Min(0f)]
    [SerializeField] private float m_iconSize = 0f;

    [Tooltip("아이콘 회전(도, 반시계). 대상의 월드 회전과 무관한 고정 각도")]
    [SerializeField] private float m_iconAngle = 0f;

    [Tooltip("켜면 대상이 바라보는 방향으로 아이콘이 돌아간다 — 위 각도는 스프라이트가 위를 보게 맞추는 보정으로 쓰인다")]
    [SerializeField] private bool m_iconFollowsFacing = false;

    [Header("범위 오버레이 (#610)")]
    [Tooltip("이 대상이 덮는 월드 반경(m). 0이면 점만 찍는다 — 폭발 반경처럼 '얼마나 넓게'를 알려야 할 때만 채운다")]
    [Min(0f)]
    [SerializeField] private float m_areaRadius = 0f;

    [Tooltip("범위 오버레이 색 — 알파를 낮게 둬야 밑의 맵이 비친다")]
    [SerializeField] private Color m_areaColor = new Color(1f, 0.25f, 0.25f, 0.25f);

    public EMinimapMarker Category => m_category;

    public Sprite IconSprite => m_iconSprite;

    /// <summary>아이콘 색 — 런타임 변경 가능. MinimapViewer가 매 프레임 반영한다. (#362 CCTV 선택 하이라이트)</summary>
    public Color IconColor
    {
        get => m_iconColor;
        set => m_iconColor = value;
    }

    /// <summary>아이콘 크기(px) — 0이면 프리팹 크기. IconColor와 같이 매 프레임 반영된다.</summary>
    public float IconSize
    {
        get => m_iconSize;
        set => m_iconSize = value;
    }

    /// <summary>아이콘 회전(도) — FollowsFacing이 켜져 있으면 대상 방향에 더해지는 보정각이 된다.</summary>
    public float IconAngle
    {
        get => m_iconAngle;
        set => m_iconAngle = value;
    }

    /// <summary>아이콘이 대상의 바라보는 방향(월드 yaw)을 따라가는지.</summary>
    public bool IconFollowsFacing => m_iconFollowsFacing;

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

    private NetworkObject m_networkObject;

    // 네트워크 오브젝트가 없거나 아직 스폰 전이면(호스트 없이 씬을 바로 Play하는 오프라인 테스트)
    // true로 둔다 — 플레이어가 하나뿐이라 내 아이콘이 사라지는 편이 더 나쁘다. 스폰 전엔
    // IsOwner가 신뢰할 수 없어(AreaScanner.Now 등과 같은 관례) IsSpawned로 먼저 가른다.
    public bool IsLocalPlayer =>
        m_networkObject == null || !m_networkObject.IsSpawned || m_networkObject.IsOwner;

    private void OnEnable()
    {
        m_networkObject = GetComponentInParent<NetworkObject>();
        ActiveTargets.Add(this);
    }

    private void OnDisable() => ActiveTargets.Remove(this);
}
