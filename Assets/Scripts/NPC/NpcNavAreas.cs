using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// NPC 통행이 도로를 어떻게 다루는지 한 곳에 모은다 (#634 → #634 후속).
///
/// <b>도로는 상태에 따라 갈린다.</b> 평소(배회)에는 아예 못 가는 곳이고, 쫓고 쫓기는 동안에는
/// 그냥 비싼 곳이다. 이 둘을 가르는 것이 <see cref="AllowsRoad"/>이고, 실제 전환은
/// <c>NpcController</c>가 상태 전이 때 <see cref="NavMeshAgent.areaMask"/>에 건다.
///
/// <b>왜 비용 조정으로는 안 되는가.</b> 도로를 빼면 도시가 서로 못 닿는 블록 8개로 갈린다 —
/// 세로 도로가 맵을 세로로, 가로 도로 3개가 가로로 관통하고 맵 가장자리에 우회로가 없다
/// (실측: 블록 간 <c>CalculatePath</c>가 전부 <c>PathPartial</c>). 즉 <b>블록을 잇는 경로는
/// 도로뿐</b>이라, Road 비용을 아무리 올려도 그게 유일한 경로라 그대로 건넌다. 통행을 실제로
/// 막는 방법은 마스크에서 빼는 것 하나뿐이다.
///
/// 그 대신 도로를 못 밟게 하는 상태는 최소로 둔다 (<see cref="AllowsRoad"/> 주석 참고) —
/// 맵을 가로질러야 하는 상태에서 도로를 끊으면 목적지에 영영 닿지 못하고, 무엇보다
/// <b>플레이어가 도로 위에 서면 아무도 쫓아오지 못하는</b> 안전지대가 생긴다.
/// </summary>
public static class NpcNavAreas
{
    /// <summary>도로 영역 이름 — Navigation 설정 Areas 탭의 문자열과 같아야 한다.</summary>
    public const string k_roadAreaName = "Road";

    private static int s_roadMask = -1; // -1 = 아직 조회 전

    /// <summary>도로 영역 비트마스크. 프로젝트 설정에 그 영역이 없으면 0이라 아래가 전부 무동작이 된다.</summary>
    public static int RoadMask
    {
        get
        {
            if (s_roadMask < 0)
            {
                int area = NavMesh.GetAreaFromName(k_roadAreaName);
                s_roadMask = area >= 0 ? 1 << area : 0;
            }
            return s_roadMask;
        }
    }

    /// <summary>
    /// 도로를 뺀 통행 마스크 — <b>목적지·스폰 지점을 고를 때만</b> 쓴다.
    /// 에이전트 자신의 <see cref="NavMeshAgent.areaMask"/>는 건드리지 않는다: 그걸 줄이면 경로가
    /// 도로를 건너지 못해 위 클래스 주석의 분단이 그대로 재현된다.
    /// </summary>
    public static int ExcludeRoad(int areaMask)
    {
        int masked = areaMask & ~RoadMask;

        // 통행 가능한 곳이 도로뿐인 구성(테스트 씬 등) — 빈 마스크로 샘플하면 아무 데도 못 뽑아
        // NPC가 그 자리에 굳는다. 그럴 땐 도로라도 쓰게 원래 마스크를 돌려준다.
        return masked != 0 ? masked : areaMask;
    }

    /// <summary>
    /// 이 상태에서 도로를 <b>통행해도 되는가</b> — 아니면 에이전트 마스크에서 Road를 뺀다.
    ///
    /// <b>제외 목록으로 쓰는 이유(화이트리스트가 아니라).</b> 여기 빠뜨린 상태는 "도로를 써도 되는
    /// 쪽"으로 떨어진다 — 새 상태가 추가됐을 때 최악이 <b>도로를 밟는 것</b>이지 <b>목적지에 닿지
    /// 못해 굳는 것</b>이 아니게 하려는 방향이다. 반대로 두면 새 상태가 조용히 블록에 갇힌다.
    ///
    /// 그래서 막는 것은 <b>평소 시민 생활</b>뿐이다. 나머지는 전부 맵을 가로지르는 목적이 있다 —
    /// 추격·도주는 물론이고 침입(유치장 자물쇠)·반출 보행·원한 구역 수용·호송·연행이 그렇다.
    /// </summary>
    public static bool AllowsRoad(NpcState state)
    {
        return state != NpcState.Idle && state != NpcState.Walk;
    }

    // "지금 도로 위인가" 판정 반경(m) — 발밑을 묻는 것이라 좁게 잡는다.
    private const float k_onRoadProbeRadius = 0.5f;

    /// <summary>
    /// 지금 도로 위에 서 있는가 — <b>마스크를 좁혀도 되는지</b>를 가른다.
    ///
    /// 도로 위에서 Road를 빼면 서 있는 폴리곤 자체가 마스크 밖이 되어 경로 계산이 통째로 실패한다
    /// (실측: <c>CalculatePath</c> → <c>PathInvalid</c>, 반환값도 false). 그 자리가 하필 차도
    /// 한복판이라, 좁히는 쪽은 반드시 이걸 먼저 물어야 한다.
    /// </summary>
    public static bool IsOnRoad(Vector3 position)
    {
        if (RoadMask == 0)
            return false;

        // 영역은 마스크가 아니라 <b>맞은 폴리곤</b>에서 읽는다 — Road 마스크로 직접 샘플하면
        // 반경 안에 도로가 있기만 해도 참이 되어, 인도에 선 NPC가 도로 위로 잘못 판정된다.
        return NavMesh.SamplePosition(position, out NavMeshHit hit, k_onRoadProbeRadius, NavMesh.AllAreas)
            && (hit.mask & RoadMask) != 0;
    }
}
