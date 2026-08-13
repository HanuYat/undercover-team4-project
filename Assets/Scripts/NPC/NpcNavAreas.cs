using UnityEngine.AI;

/// <summary>
/// NPC 통행이 도로를 어떻게 다루는지 한 곳에 모은다 (#634).
///
/// <b>도로는 못 가는 곳이 아니라 비싼 곳이다.</b> 통행을 아예 끊으면(Not Walkable) 세로 도로가 맵을
/// 세로로, 가로 도로가 가로로 관통하고 있어 도시가 서로 못 닿는 블록 8개로 쪼개진다 — 시민이 스폰된
/// 구역에 갇히고, 침입자·납치범이 목적지에 닿지 못하고, 무엇보다 <b>플레이어가 도로 위에 서면
/// 아무도 쫓아오지 못한다</b>(추격은 NavMeshAgent로 움직인다). 차만 피하면 무적인 자리를 만들 수 없다.
///
/// 그래서 도로는 NavMesh에 남기고 둘만 한다:
///  · <b>비용을 높인다</b> (Navigation 설정의 Road 영역) — 경로가 도로를 따라 걷지 않고 가로질러
///    최단으로만 지난다. 횡단보도를 따로 그리지 않아도 "빠르게 가로지르는" 그림이 나온다.
///  · <b>배회 목적지에서 뺀다</b> (이 클래스) — 시민이 도로를 향해 걸어가거나 그 위에 멈춰 서지 않는다.
///
/// 남는 노출은 "건너는 동안"뿐이다. 쫓고 쫓기는 쪽(<see cref="NpcChaseState"/>·<see cref="NpcFleeState"/>)은
/// 에이전트의 전체 마스크를 그대로 쓴다.
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
}
