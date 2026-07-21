using System;
using UnityEngine;

/// <summary>
/// SciFi 통짜 바디 모델별 고정 외형 조합 — 모델 인덱스 → AppearanceProfile.
/// Profile 인덱스는 공유 AppearanceDatabase의 옵션 목록을 가리킨다(혼합 라운드 매칭 일관성). (#221)
/// 배열 인덱스는 NpcCatalogAppearance의 바디 토글 순서(BodyVariants)와 일치해야 한다.
/// </summary>
[CreateAssetMenu(fileName = "AppearanceModelCatalog", menuName = "Scriptable Objects/AppearanceModelCatalog")]
public class AppearanceModelCatalog : ScriptableObject
{
    [Serializable]
    public class ModelEntry
    {
        [Tooltip("참고용 모델 이름 (바디 오브젝트명과 일치 권장 - 정렬 검증용")]
        public string ModelName;

        [Tooltip("이 모델의 6축 몽타주 값 - 공유 AppearanceDatabase 옵션 인덱스")]
        public AppearanceProfile Profile;
    }

    [Tooltip("배열 인덱스 = 바디 토글 인덱스(BodyVariants 순서)와 일치")]
    [SerializeField] private ModelEntry[] m_models;

    public int Count => m_models?.Length ?? 0;

    public AppearanceProfile GetProfile(int modelIndex)
    {
        if (m_models == null || modelIndex < 0 || modelIndex >= m_models.Length)
            return AppearanceProfile.Unassigned;
        return m_models[modelIndex].Profile;
    }

    public string GetModelName(int modelIndex) => 
        (m_models != null && modelIndex >= 0 && modelIndex < m_models.Length) 
        ? m_models[modelIndex].ModelName : null;
}
