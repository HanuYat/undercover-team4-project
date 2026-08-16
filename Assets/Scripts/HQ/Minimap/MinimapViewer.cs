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

    private readonly Dictionary<MinimapTarget, Image> m_targetIcons = new();
    private readonly Dictionary<MinimapTarget, Image> m_targetAreas = new();
    private readonly List<MinimapTarget> m_removeBuffer = new();

    private RectTransform AreaParent => m_areaContainer != null ? m_areaContainer : m_iconContainer;

    private void LateUpdate()
    {
        SyncIcons();
        UpdatePositions();
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
