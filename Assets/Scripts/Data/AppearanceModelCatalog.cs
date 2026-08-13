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

        [Tooltip("그림 몽타주의 인간 두상으로 안 읽히는 모델(에일리언 등). 체크하면 범인·디코이에서 빠지고 일반 시민으로만 나온다")]
        public bool NonHumanoid;
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

    /// <summary>
    /// 이 모델을 그림 몽타주로 그릴 수 있는가 — 범인·디코이 후보를 거르는 기준 (AppearanceDatabase.CanDepict와 같은 취지).
    /// 포트레이트가 가진 두상은 인간형 하나뿐이라, 그 두상으로 안 읽히는 모델이 범인이면 그림이 화면과 어긋나고
    /// 디코이면 현장에서 후보로 안 보여 몽타주 부합 인원 k의 보장이 실질적으로 깨진다.
    /// 일반 시민으로는 계속 나온다 — 도시 다양성은 몽타주가 서술하지 않는 플레이버다 (appearance-montage.md §3).
    /// </summary>
    public bool CanDepict(int modelIndex)
    {
        if (m_models == null || modelIndex < 0 || modelIndex >= m_models.Length)
            return false;
        return !m_models[modelIndex].NonHumanoid;
    }

    public string GetModelName(int modelIndex) =>
        (m_models != null && modelIndex >= 0 && modelIndex < m_models.Length) 
        ? m_models[modelIndex].ModelName : null;
}
