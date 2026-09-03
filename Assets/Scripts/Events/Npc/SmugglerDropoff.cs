using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 밀수 거래 지점 — 밀수 운반책(<see cref="SmugglerCourierEvent"/>)이 향하는 목적지 마커. (#991)
///
/// 씬 곳곳에 여러 개 두고, 이벤트가 <b>스폰 지점에서 가장 먼 것</b>을 고른다 — 이동 시간이 곧
/// 제한시간이라 가까운 지점이 뽑히면 잡을 창이 사라진다.
///
/// 빈 GameObject에 붙이기만 하면 된다(위치만 쓴다). NavMesh 위에 둘 것 — 경로가 안 잡히면
/// 운반책이 불발로 처리된다.
/// </summary>
public class SmugglerDropoff : MonoBehaviour
{
    // 씬에 살아 있는 지점들 — 매니저를 따로 두기엔 값이 위치 하나뿐이라 마커가 스스로 등록한다.
    private static readonly List<SmugglerDropoff> s_active = new List<SmugglerDropoff>();

    /// <summary>지금 씬에 있는 거래 지점들 — 읽기 전용.</summary>
    public static IReadOnlyList<SmugglerDropoff> Active => s_active;

    private void OnEnable() => s_active.Add(this);

    private void OnDisable() => s_active.Remove(this);

    /// <summary>origin에서 가장 먼 거래 지점 — 없으면 null. (#991)</summary>
    public static Transform FindFarthestFrom(Vector3 origin)
    {
        Transform farthest = null;
        float bestSqr = -1f;

        for (int i = 0; i < s_active.Count; i++)
        {
            float sqr = (s_active[i].transform.position - origin).sqrMagnitude;
            if (sqr <= bestSqr)
                continue;

            bestSqr = sqr;
            farthest = s_active[i].transform;
        }

        return farthest;
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(0.9f, 0.5f, 0.1f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, 1.5f);
    }
}
