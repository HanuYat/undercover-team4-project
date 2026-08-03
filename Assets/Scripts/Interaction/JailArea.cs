using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 유치장 내부(Jail) NavMesh 영역 판정 — 마스크 해석과 "이 지점이 유치장 안인가"를 한 곳에 모은다. (#415/#492)
///
/// 이 영역이 유치장 규칙의 단일 기준이다: 시민의 통행 차단(NpcController.SetJailAccess),
/// 문 개폐 판정, 수감 판정·착석(JailIntake)이 전부 같은 폴리곤을 본다.
/// 차단은 문이 아니라 NavMesh 영역이 한다 — 통행이 없으면 문이 열려 있어도 경로가 잡히지 않는다.
///
/// Jail 영역이 없는 프로젝트(단독 테스트 씬 등)에서는 <see cref="Mask"/>가 0이고
/// <see cref="Contains"/>는 항상 false다 — 호출부가 각자 폴백을 정한다.
/// </summary>
public static class JailArea
{
    // 영역 판정 허용치(m). 실측상 Jail 영역은 문짝(z 49.70)보다 0.2m 안쪽(z 49.90)에서 시작한다 —
    // 0.5m로 잡으면 문 밖 접근 지점(LockApproach)까지 '안'으로 걸리고, 0.02m는 유치장 안 좌석도 놓친다.
    private const float k_sampleRadius = 0.2f;

    // 이름으로 한 번만 해석해 캐시한다. -1은 '아직 안 봤다', 0은 '이 프로젝트엔 Jail 영역이 없다'.
    private static int s_mask = -1;

    /// <summary>유치장 내부(Jail) NavMesh 영역 마스크 — 없는 프로젝트면 0.</summary>
    public static int Mask
    {
        get
        {
            if (s_mask < 0)
            {
                int area = NavMesh.GetAreaFromName("Jail");
                s_mask = area >= 0 ? 1 << area : 0;
            }
            return s_mask;
        }
    }

    /// <summary>이 지점이 유치장 내부(Jail 영역) 위인가 — Jail 영역이 없는 프로젝트면 항상 false.</summary>
    public static bool Contains(Vector3 position)
    {
        int mask = Mask;
        if (mask == 0)
            return false;

        return NavMesh.SamplePosition(position, out NavMeshHit _, k_sampleRadius, mask);
    }
}
