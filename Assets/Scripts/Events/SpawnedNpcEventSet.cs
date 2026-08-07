using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 스폰형 돌발 이벤트 모음 — <b>리스트 항목 하나가 이벤트 1종</b>이다. (GDD 6-4, #106)
/// 거리 난동자·나체 난동꾼처럼 "현장에 NPC를 스폰해 소란을 일으키는" 이벤트는 이름·행동 모드·프리팹만 다르고
/// 나머지 구조가 같다. 그래서 종류마다 컴포넌트를 붙이는 대신 이 리스트에 항목을 추가해 늘린다 —
/// 종류를 늘려도 코드는 그대로다.
///
/// 항목(<see cref="SpawnedNpcEvent"/>)은 MonoBehaviour가 아니라 데이터라 수명주기 훅이 없다.
/// 그래서 이 컴포넌트가 Awake/OnEnable/OnDisable을 항목들에 대신 돌려준다.
///
/// 이벤트는 매니저와 같은 오브젝트에 둔다 — <see cref="SuddenEventManager"/>가 자식을 훑지 않기 때문
/// (자식에 두면 그 자식에 두 번째 매니저가 자동 생성된다). RequireComponent가 이를 강제한다.
/// </summary>
[RequireComponent(typeof(SuddenEventManager))]
public class SpawnedNpcEventSet : MonoBehaviour, ISuddenEventProvider
{
    [Header("스폰형 이벤트 목록 — 항목을 추가해 종류를 늘린다")]
    [SerializeField] private List<SpawnedNpcEvent> m_events = new List<SpawnedNpcEvent>();

    // 모든 항목이 공유한다 — 인계 후 경범죄 판정 시점에 자기 스폰물을 정리하는 데 쓴다
    private ArrestJudge Judge => App.Game.ArrestJudge;

    private void Awake()
    {
        if (Judge == null)
            Debug.LogWarning("SpawnedNpcEventSet: ArrestJudge를 찾지 못해 인계 후 스폰물이 정리되지 않는다", this);

        for (int i = 0; i < m_events.Count; i++)
            m_events[i].Initialize(this, Judge);
    }

    private void OnEnable()
    {
        for (int i = 0; i < m_events.Count; i++)
            m_events[i].Subscribe();
    }

    private void OnDisable()
    {
        for (int i = 0; i < m_events.Count; i++)
            m_events[i].Unsubscribe();
    }

    /// <summary>리스트 항목을 이벤트 풀에 얹는다 — 비우지 않고 추가한다 (<see cref="ISuddenEventProvider"/> 계약).
    /// 꺼 둔 항목은 뺀다 — 매니저 쪽 토글은 이 Set 전체가 대상이라 한 종류만 켜지 못한다. (#303)</summary>
    public void CollectEvents(List<ISuddenEvent> into)
    {
        for (int i = 0; i < m_events.Count; i++)
        {
            if (m_events[i].Enabled)
                into.Add(m_events[i]);
        }
    }
}
