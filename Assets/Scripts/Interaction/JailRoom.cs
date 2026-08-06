using UnityEngine;

/// <summary>
/// "이 좌표가 감옥 방 안인가"를 답하는 정적 판정 유틸 — 옛 <c>JailArea</c>의 자리를 잇는다. (#415/#492/#537)
///
/// <b>기준이 NavMesh 영역에서 방 부피로 바뀌었다.</b> 예전에는 유치장 내부 폴리곤이 Jail 영역이라
/// 마스크로 판정했는데, 감옥이 도시에서 떨어진 별도 NavMesh 섬이 되면서(#537) 영역 게이팅 자체가
/// 없어졌다. 이제는 <see cref="JailZone"/>에 배선한 방 부피가 유일한 기준이다.
///
/// <b>씬 오브젝트를 캐시하지만 매니저가 아니다</b> — App 파사드에 올릴 대상이 아니고(장소 오브젝트),
/// 여기 있는 것은 "좌표 하나로 물어볼 창구"뿐이다. 참조가 죽으면(씬 전환) 다음 호출에서 다시 찾는다.
///
/// 감옥이 없는 프로젝트(단독 테스트 씬 등)에서는 항상 false다 — 호출부가 각자 폴백을 정한다.
/// </summary>
public static class JailRoom
{
    // 씬의 감옥 — 매번 찾지 않게 잡아 둔다. Unity의 가짜 null 비교가 파괴된 참조를 걸러 준다.
    private static JailZone s_zone;

    /// <summary>이 좌표가 감옥 방 안인가 — 감옥이 없거나 방 범위가 미배선이면 항상 false.</summary>
    public static bool Contains(Vector3 position)
    {
        if (s_zone == null)
            s_zone = Object.FindFirstObjectByType<JailZone>();

        return s_zone != null && s_zone.ContainsPoint(position);
    }
}
