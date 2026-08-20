using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// 미니맵을 찍어주는 컴포넌트.
public class MinimapViewer : MonoBehaviour
{
    [Header("맵이 덮는 월드 영역")]
    [SerializeField] private float m_worldCenterX = 0f;
    [SerializeField] private float m_worldCenterZ = 0f;
    [SerializeField] private float m_worldSizeX = 100f;
    [SerializeField] private float m_worldSizeZ = 100f;

    [Header("UI 참조")]
    [SerializeField] private RectTransform m_mapRect;       // 정적 맵 이미지의 RectTransform
    [SerializeField] private RectTransform m_iconContainer; // 아이콘 부모
    [SerializeField] private Image m_iconPrefab;

    [Header("범위 오버레이 (#610)")]
    [Tooltip("이벤트 범위를 그릴 반투명 원 Image. 비우면 범위는 그리지 않는다 — 점 아이콘만 찍힌다")]
    [SerializeField] private Image m_areaPrefab;

    [Tooltip("범위 오버레이 부모. 비우면 아이콘 부모를 쓰되 맨 뒤로 보내 아이콘에 깔린다")]
    [SerializeField] private RectTransform m_areaContainer;

    [Header("먹통 차단 (#762)")]
    [Tooltip("먹통 중 지도를 덮는 판. 비우면 런타임에 검은 판을 만든다 — 프리팹 배선을 잊어도 동작한다")]
    [SerializeField] private Image m_blackoutCover;

    private DeviceBlackoutEvent m_blackout;
    private bool m_covered;

    private readonly Dictionary<MinimapTarget, Image> m_targetIcons = new();
    private readonly Dictionary<MinimapTarget, Image> m_targetAreas = new();
    private readonly List<MinimapTarget> m_removeBuffer = new();

    private RectTransform AreaParent => m_areaContainer != null ? m_areaContainer : m_iconContainer;

    // 배선된 덮개가 켜진 채 저장돼 있으면 첫 먹통 전까지 지도가 검다 — 시작 상태를 여기서 맞춘다.
    private void Awake()
    {
        if (m_blackoutCover != null)
            m_blackoutCover.enabled = false;
    }

    private void LateUpdate()
    {
        ApplyBlackout(IsBlackout());

        // 먹통 중에는 아이콘을 돌리지 않는다 — 덮여서 보이지도 않고, 해제되는 프레임의 SyncIcons가
        // 그동안의 스폰·디스폰을 한 번에 맞춘다.
        if (m_covered)
            return;

        SyncIcons();
        UpdatePositions();
    }

    // ---- 먹통 차단 (#762) ----

    // 먹통 플래그를 구독하지 않고 매 프레임 묻는다 (JailSirenButton과 같은 방식) — 미니맵은 씬에 놓인
    // 프리팹이고 SuddenEventManager는 세션 스폰이라, 구독하려면 스폰을 기다리는 배선이 따로 필요하다.
    // 여기는 이미 LateUpdate가 도는 자리라 묻는 편이 싸고, 복구·이벤트 도중 입장이 배선 없이 따라온다.
    private bool IsBlackout()
    {
        DeviceBlackoutEvent blackout = ResolveBlackout();
        return blackout != null && blackout.IsCommsBlackout;
    }

    // ??= 대신 Unity의 == 오버로드로 확인한다 — 파괴된 참조(fake null)를 통과시키면 다음 라운드에서
    // 죽은 컴포넌트를 계속 붙들고 묻는다. (HqPanelView의 '?. 금지' 주석과 같은 이유)
    private DeviceBlackoutEvent ResolveBlackout()
    {
        if (m_blackout != null)
            return m_blackout;

        SuddenEventManager manager = App.Game.SuddenEvent;
        m_blackout = manager != null ? manager.GetEvent<DeviceBlackoutEvent>() : null;
        return m_blackout;
    }

    // CCTV가 먹통에 모니터를 검게 지우는 것과 같은 언어다(CCTVSwitcher.ClearMonitor) — 지도와 아이콘을
    // 통째로 덮는다. 지형까지 안 보이는 것이 의도다.
    private void ApplyBlackout(bool blackout)
    {
        if (blackout == m_covered)
            return;

        m_covered = blackout;

        Image cover = EnsureCover();
        if (cover == null)
            return;

        // 켤 때마다 맨 앞으로 올린다 — 아이콘은 지도의 자식이라 나중에 만들어진 것이 위에 그려진다.
        if (blackout)
            cover.rectTransform.SetAsLastSibling();

        cover.enabled = blackout;
    }

    // 배선을 잊어도 동작하게 런타임에 만든다 (RopeDragView.Build와 같은 방침). 지도를 부모로 잡고
    // 네 변을 붙여 늘리므로 지도 크기가 바뀌어도 따라간다.
    private Image EnsureCover()
    {
        if (m_blackoutCover != null)
            return m_blackoutCover;

        if (m_mapRect == null)
        {
            enabled = false; // 지도 참조가 없으면 가릴 대상도 없다 — 매 프레임 헛돌지 않게 스스로 꺼진다
            Debug.LogWarning($"MinimapViewer: m_mapRect가 없어 먹통 차단을 만들 수 없다. {name} 프리팹에 지정할 것", this);
            return null;
        }

        var built = new GameObject("MinimapBlackoutCover", typeof(RectTransform), typeof(Image));
        RectTransform rect = built.GetComponent<RectTransform>();
        rect.SetParent(m_mapRect, false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        m_blackoutCover = built.GetComponent<Image>();
        m_blackoutCover.color = Color.black;
        m_blackoutCover.raycastTarget = false; // 지도 위 클릭을 먹지 않는다
        m_blackoutCover.enabled = false;
        return m_blackoutCover;
    }

    private void SyncIcons()    // 레지스트리와 아이콘 개수 맞추기 (스폰/디스폰 대응)
    {
        m_removeBuffer.Clear();
        foreach (var pair in m_targetIcons)
        {
            if (pair.Key == null || !MinimapTarget.ActiveTargets.Contains(pair.Key))    // 레지스트리의 타겟이 없어짐
                m_removeBuffer.Add(pair.Key);   // 제거할 타겟 목록에 추가
        }
        foreach (var target in m_removeBuffer) 
        {
            if (m_targetIcons[target] != null)  // 아이콘이 존재하면 제거
                Destroy(m_targetIcons[target].gameObject);
            m_targetIcons.Remove(target);

            // 범위 오버레이도 같은 수명 — 납치가 끝나 디스폰되면 아이콘과 함께 사라진다
            if (m_targetAreas.TryGetValue(target, out Image area))
            {
                if (area != null)
                    Destroy(area.gameObject);
                m_targetAreas.Remove(target);
            }
        }
        foreach (var target in MinimapTarget.ActiveTargets) // 레지스트리 타겟 순회
        {
            if (target == null || m_targetIcons.ContainsKey(target))    // 이미 아이콘이 존재하면 스킵
                continue;

            // ▼ 아이콘 생성, 초기화
            Image icon = Instantiate(m_iconPrefab, m_iconContainer);

            if (target.IconSprite != null)
                icon.sprite = target.IconSprite;

            icon.color = target.IconColor;
            m_targetIcons.Add(target, icon);

            // ▼ 범위 오버레이 — 반경이 있는 대상에만 만든다.
            // "범위가 있는가"는 프리팹 값이라 여기서 한 번만 가르고, 크기·색은 매 프레임 갱신한다.
            if (m_areaPrefab != null && target.AreaRadius > 0f)
            {
                Image area = Instantiate(m_areaPrefab, AreaParent);
                area.rectTransform.SetAsFirstSibling(); // 아이콘에 깔린다 (부모를 공유할 때를 대비)
                m_targetAreas.Add(target, area);
            }
        }
    }

    private void UpdatePositions()  // 아이콘 위치, 색 갱신
    {
        foreach (var pair in m_targetIcons)
        {
            pair.Value.rectTransform.anchoredPosition = WorldToMap(pair.Key.transform.position);
            pair.Value.color = pair.Key.IconColor;
        }

        foreach (var pair in m_targetAreas)
        {
            RectTransform rect = pair.Value.rectTransform;
            rect.anchoredPosition = WorldToMap(pair.Key.transform.position);
            rect.sizeDelta = WorldRadiusToMapSize(pair.Key.AreaRadius);
            pair.Value.color = pair.Key.AreaColor;
        }
    }

    // 월드 XZ -> 맵 좌표
    private Vector2 WorldToMap(Vector3 worldPos)
    {
        float u = (worldPos.x - m_worldCenterX) / m_worldSizeX;
        float v = (worldPos.z - m_worldCenterZ) / m_worldSizeZ;

        Vector2 mapSize = m_mapRect.rect.size;
        return new Vector2(u * mapSize.x, v * mapSize.y);
    }

    // 월드 반경(m) -> 오버레이 지름(px). 축마다 따로 재는 이유는 WorldToMap과 같다 —
    // 맵이 XZ로 다르게 눌려 있으면 원도 같이 눌려야 실제 범위와 겹친다.
    private Vector2 WorldRadiusToMapSize(float radius)
    {
        Vector2 mapSize = m_mapRect.rect.size;
        return new Vector2(
            2f * radius / m_worldSizeX * mapSize.x,
            2f * radius / m_worldSizeZ * mapSize.y);
    }
}
