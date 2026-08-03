using UnityEngine;

/// <summary>
/// 유치장 앞 보안 스캐너 — <b>검거 판정이 일어나는 게이트</b>다. (#492)
///
/// 책임은 하나: "이 지점이 게이트 안인가"(<see cref="Contains"/>). 판정 자체는 하지 않는다 —
/// 확보한 신병이 이 안에 들어섰는지를 <see cref="JailIntake"/>가 물어보고, 판정은 그쪽이 굴린다.
/// 나중에 통과 연출(라이트·사운드)이 붙을 자리도 여기다.
///
/// <b>왜 유치장 문턱이 아니라 문 앞인가.</b> 처음에는 Jail NavMesh 영역에 들어서는 순간 판정했다.
/// 그런데 유치장 <b>안</b>에서 오검거가 확정되면, 그 시민은 신병에서 빠지면서 Jail 통행을 잃는데
/// 자기가 딛고 선 폴리곤이 금지돼 경로가 아예 안 잡히고 그 자리에 굳는다. 판정을 문 밖으로 빼면
/// 오검거된 시민이 애초에 유치장에 발을 들이지 않아 그 사고가 구조적으로 사라진다.
/// 연출로도 이쪽이 맞다 — 감옥 벽이 사람을 분류하는 것보다 <b>검문 게이트를 통과하는 것</b>이 읽힌다.
///
/// <b>영역 판정을 Collider.bounds로 하지 않는 이유</b>는 두 가지다: bounds는 회전을 무시하는
/// 월드 AABB라 게이트를 비스듬히 놓으면 판정이 어긋나고, 트랜스폼을 옮긴 직후에는 물리 동기화
/// 전까지 낡은 값을 준다. 그래서 박스를 <b>로컬 공간에서 직접</b> 검사한다 — 물리 쿼리도 필요 없다.
///
/// 씬 배치: 스캐너 프롭에 이 컴포넌트와 <see cref="BoxCollider"/>를 함께 둔다. 박스는 아치 개구부에
/// 맞추고 <b>Is Trigger를 켜 둘 것</b> — 끄면 문짝처럼 플레이어를 막아 게이트를 지날 수 없다.
///
/// 장소 오브젝트라 App 파사드에 등록하지 않는다 — JailZone·JailLock·JailIntake와 같은 관례다.
/// </summary>
public class JailScanner : MonoBehaviour
{
    [Header("게이트 범위 (비우면 이 오브젝트·자식에서 자동 탐색)")]
    [Tooltip("판정이 일어나는 부피. 아치 개구부에 맞출 것 — 통과하다 놓치지 않게 두께를 넉넉히 준다")]
    [SerializeField] private BoxCollider m_zone;

    private void Awake()
    {
        if (m_zone == null)
            m_zone = GetComponentInChildren<BoxCollider>();

        if (m_zone == null)
        {
            Debug.LogWarning("JailScanner: 게이트 범위(BoxCollider)가 없다 — 판정이 일어나지 않는다", this);
            return;
        }

        // 트리거가 아니면 플레이어를 막아 버린다 — 신병을 끌고 지날 수가 없다
        if (!m_zone.isTrigger)
            Debug.LogWarning($"JailScanner: 게이트 범위({m_zone.name})의 Is Trigger가 꺼져 있다 — 플레이어가 막힌다", this);
    }

    /// <summary>
    /// 이 지점이 게이트 안인가 — 범위가 배선되지 않았으면 항상 false(판정이 일어나지 않는다).
    /// 서버·클라 구분 없는 순수 판정이라 어디서 불러도 안전하다.
    /// </summary>
    public bool Contains(Vector3 position)
    {
        if (m_zone == null)
            return false;

        // 로컬로 환산하면 회전·스케일이 함께 풀린다. BoxCollider.size/center는 로컬 단위다.
        Vector3 local = m_zone.transform.InverseTransformPoint(position) - m_zone.center;
        Vector3 half = m_zone.size * 0.5f;

        return Mathf.Abs(local.x) <= half.x
            && Mathf.Abs(local.y) <= half.y
            && Mathf.Abs(local.z) <= half.z;
    }
}
