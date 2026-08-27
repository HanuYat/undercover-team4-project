using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 닫힌 문이 NavMesh를 끊는다 — <b>NPC에게도 문이 문이 되게 하는 부품.</b> (#838)
///
/// <b>왜 필요한가.</b> 문짝은 NavMesh 베이크에서 제외돼 있다(<see cref="DoubleDoor"/> 씬 배치 주석) —
/// 닫힌 문짝을 구우면 문턱 NavMesh가 끊겨 <b>열어도</b> 못 지나가기 때문이다. 그래서 구워진
/// NavMesh만 보면 문은 언제나 열려 있고, 문짝 콜라이더는 <c>CharacterController</c>(플레이어)만
/// 막을 뿐 <c>NavMeshAgent</c>는 그대로 통과시킨다. 즉 <b>NPC에게 이 문은 없는 것과 같았다</b>.
/// 그동안은 본부 실내를 통행 마스크에서 빼는 것(<c>HQ</c> 영역)이 그 구멍을 덮고 있었다.
///
/// <b>마스크를 걷어낸 대가로 이 부품이 생겼다</b> (#838). 마스크로 막으면 본부 안에 놓인 몸을
/// NavMesh에 다시 붙일 때 마스크 안쪽 폴리곤을 찾느라 벽을 뚫고 밖으로 끌려간다 — 실내가 좁아
/// 어디에 서든 바깥이 2m 안이라 거의 항상 그렇게 된다. 그래서 통제를 마스크에서 <b>문</b>으로 옮겼다:
/// 열려 있으면 통행, 닫혀 있으면 차단. 사람이 보는 그림과 NPC가 받는 규칙이 이제 같다.
///
/// <b>차단은 carving으로 한다.</b> 열고 닫을 때마다 NavMesh를 다시 굽는 것이 아니라
/// <see cref="NavMeshObstacle"/>이 그 자리 폴리곤을 도려낸다. 도려낸 자리는 경로 계산에서도 빠지므로
/// (<c>NpcMovePoint</c>가 <c>PathComplete</c>만 목적지로 고른다) NPC는 닫힌 문 너머를 <b>애초에
/// 목적지로 뽑지 않는다</b> — 문 앞까지 왔다가 되돌아가는 그림이 아니다.
///
/// <b>크기는 문짝에서 잡는다</b> — 문짝이 닫힌 채 채우고 있는 부피가 곧 문틀 구멍이다.
/// 인스펙터로 재는 것보다 정확하고, 문 종류가 늘어도 배치가 따라오지 않는다.
/// 재는 시점은 <see cref="Awake"/> 하나뿐이다: 그 뒤로는 문짝이 돌아 부피가 딴 데를 가리킨다
/// (<see cref="DoubleDoor"/>는 문짝을 <b>닫힌 자세로</b> 저장해 둘 것을 요구한다 — 같은 전제다).
///
/// <b>전 피어에서 돈다.</b> 경로를 계산하는 것은 서버뿐이지만 차단막은 NavMesh를 건드리는
/// 로컬 작업이고, 피어마다 다른 NavMesh를 들고 있을 이유가 없다. 비용도 문을 여닫는 순간의
/// 상자 하나뿐이다.
///
/// 씬 배치: <b><see cref="DoubleDoor"/>와 같은 GameObject에 붙인다</b> (아래 RequireComponent가 보장).
/// </summary>
[RequireComponent(typeof(DoubleDoor))]
public class DoorNavBlocker : MonoBehaviour
{
    [Header("차단막 크기 (문짝에서 잰 값에 얹는다)")]
    [Tooltip("문틀 좌우로 넓힐 여유(m). 문짝과 문틀 사이 틈으로 경로가 새지 않게 조금 넉넉히 잡는다")]
    [SerializeField] private float m_widthPadding = 0.2f;

    [Tooltip("차단막 두께(m) — 문짝은 얇아서 그대로 쓰면 carving이 폴리곤을 못 잘라내는 경우가 있다")]
    [SerializeField] private float m_minThickness = 0.5f;

    private DoubleDoor m_door;
    private NavMeshObstacle m_obstacle;

    private void Awake()
    {
        m_door = GetComponent<DoubleDoor>();
        m_obstacle = BuildObstacle();
    }

    // 구독은 Awake가 아니라 여기서 — 문짝 크기를 다 잰 뒤여야 첫 반영이 유효하다.
    private void OnEnable()
    {
        if (m_obstacle == null)
            return;

        m_door.OnOpenChanged += HandleOpenChanged;
        Apply(m_door.IsOpen); // 씬 시작 자세(대개 닫힘)를 첫 프레임부터 맞춰 둔다
    }

    private void OnDisable()
    {
        m_door.OnOpenChanged -= HandleOpenChanged;
    }

    private void HandleOpenChanged(bool open) => Apply(open);

    // 열려 있으면 통행, 닫혀 있으면 차단. carving은 컴포넌트를 껐다 켜는 것으로 갈린다.
    private void Apply(bool open)
    {
        if (m_obstacle != null)
            m_obstacle.enabled = !open;
    }

    /// <summary>
    /// 문짝이 닫힌 채 차지하는 부피만큼의 carving 상자를 만든다 — 문짝이 하나도 없으면 null.
    ///
    /// <b>부피는 문의 로컬 좌표로 잰다.</b> <see cref="Renderer.bounds"/>는 월드 축에 정렬된 상자라
    /// 비스듬히 놓인 문에서는 실제보다 크게 잡히는데, 그대로 쓰면 문 옆 바닥까지 도려낸다.
    /// </summary>
    private NavMeshObstacle BuildObstacle()
    {
        if (!TryMeasureLeaves(out Bounds local))
        {
            Debug.LogWarning($"DoorNavBlocker: 잴 문짝이 없어 차단막을 만들지 못했다: {name}", this);
            return null;
        }

        Vector3 size = local.size;
        size.x += m_widthPadding;
        size.z = Mathf.Max(size.z, m_minThickness);

        // 문의 자식으로 둔다 — 상자 방향이 문 방향을 따라와야 문틀에 맞고, 부모가 움직여도 따라온다.
        var holder = new GameObject("NavBlocker");
        holder.transform.SetParent(transform, false);
        holder.transform.localPosition = local.center;

        NavMeshObstacle obstacle = holder.AddComponent<NavMeshObstacle>();
        obstacle.shape = NavMeshObstacleShape.Box;
        obstacle.size = size;
        obstacle.center = Vector3.zero;

        // carving = NavMesh를 실제로 도려낸다. 이게 없으면 에이전트가 피해 갈 뿐 <b>경로는 그대로
        // 뚫려 있어</b> 닫힌 문 너머가 여전히 목적지로 뽑힌다 — 막으려던 것이 그것이다.
        obstacle.carving = true;

        // 이 상자는 문과 함께만 움직인다 — 매 프레임 이동을 감시할 이유가 없다.
        obstacle.carveOnlyStationary = true;

        return obstacle;
    }

    // 문짝 두 짝의 렌더러를 문 로컬 좌표에서 합친다 — 잰 것이 하나도 없으면 false.
    private bool TryMeasureLeaves(out Bounds local)
    {
        local = default;
        bool started = false;

        Encapsulate(m_door.LeafLeft, ref local, ref started);
        Encapsulate(m_door.LeafRight, ref local, ref started);

        return started;
    }

    // leaf 아래 렌더러 전부를 문 로컬 좌표로 옮겨 담는다. started는 "이미 담은 것이 있는가"다 —
    // 첫 점은 Encapsulate가 아니라 새 Bounds로 시작해야 원점(0,0,0)까지 함께 감싸이지 않는다.
    private void Encapsulate(Transform leaf, ref Bounds local, ref bool started)
    {
        if (leaf == null)
            return;

        Renderer[] renderers = leaf.GetComponentsInChildren<Renderer>();

        for (int i = 0; i < renderers.Length; i++)
        {
            Bounds world = renderers[i].bounds;

            // 월드 AABB의 여덟 꼭짓점을 문 로컬로 옮겨 다시 감싼다 — 중심만 옮기면 크기가 따라오지 않는다.
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 sign = new Vector3(
                    (corner & 1) == 0 ? -1f : 1f,
                    (corner & 2) == 0 ? -1f : 1f,
                    (corner & 4) == 0 ? -1f : 1f
                );

                Vector3 point = transform.InverseTransformPoint(
                    world.center + Vector3.Scale(world.extents, sign)
                );

                if (started)
                {
                    local.Encapsulate(point);
                }
                else
                {
                    local = new Bounds(point, Vector3.zero);
                    started = true;
                }
            }
        }
    }
}
