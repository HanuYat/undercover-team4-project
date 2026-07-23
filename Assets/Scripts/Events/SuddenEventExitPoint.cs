using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 돌발 이벤트 NPC의 '출구' 마커 (#310) — 이탈·방치된 이벤트 NPC가 여기까지 걸어와 소멸한다.
/// 맵 가장자리·골목 입구처럼 "이리로 나가면 자연스럽다" 싶은 곳에 배치한다. 여러 개 두면
/// NPC에서 가장 가까운 마커가 선택된다 (<see cref="SuddenEventUtil.FindExitPoint"/>).
/// 일반 NPC 스폰 포인트를 출구로 겸용하던 방식은 스폰 포인트가 액션 한복판이라 퇴장 그림이
/// 어색해서(#310 피드백) 전용 마커로 분리했다 — 마커가 없는 씬은 스폰 포인트 폴백으로 동작한다.
///
/// 장소 마커라 매니저 규약(App 등록) 대상이 아니다 — JailZone류와 같은 씬 배치물이며,
/// 검색 비용을 피해 OnEnable/OnDisable 정적 레지스트리로 자신을 등록한다.
/// </summary>
public class SuddenEventExitPoint : MonoBehaviour
{
    private static readonly List<SuddenEventExitPoint> s_active = new List<SuddenEventExitPoint>();

    /// <summary>현재 씬에서 활성화된 출구 마커 목록 — 서버·클라 구분 없이 로컬 씬 기준.</summary>
    public static IReadOnlyList<SuddenEventExitPoint> Active => s_active;

    private void OnEnable()
    {
        s_active.Add(this);
    }

    private void OnDisable()
    {
        s_active.Remove(this);
    }

#if UNITY_EDITOR
    // 씬 뷰 식별용 — 시안 구체 + 지면 표식. 게임 화면에는 아무것도 그리지 않는다.
    private void OnDrawGizmos()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position + Vector3.up * 1f, 0.4f);
        Gizmos.DrawLine(transform.position, transform.position + Vector3.up * 1f);
        Gizmos.DrawWireCube(transform.position, new Vector3(1f, 0.02f, 1f));
    }
#endif
}
