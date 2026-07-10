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

    private readonly Dictionary<MinimapTarget, Image> m_targetIcons = new();
    private readonly List<MinimapTarget> m_removeBuffer = new();

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
        }
    }

    private void UpdatePositions()  // 미니맵 아이콘 위치 갱신
    {
        foreach (var pair in m_targetIcons)
        {
            pair.Value.rectTransform.anchoredPosition = WorldToMap(pair.Key.transform.position);
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
}
