using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 감옥 — 검거된 범인을 실제로 수용·관리하는 방. (GDD 7-2, #228/#537)
/// 판정(ArrestJudge)과 분리되어 있다: 판정은 "누가 범인인가"만, 여기는 "어디에 가두고 몇 명이
/// 있는가"만 안다. 둘을 잇고 출입을 관리하는 건 <see cref="JailIntake"/>다 (#492).
///
/// <b>이 방은 도시 맵에서 떨어진 격리 공간이다</b> (#537). 걸어서 닿는 경로가 없고 출입은
/// <see cref="JailDoor"/>의 순간이동뿐이라, 안에 필요한 지점이 셋이다 — 수감자가 설 배치 지점,
/// 플레이어가 들어올 입장 지점, 그리고 도시 쪽 문 밖의 퇴장 지점.
///
/// <b>좌석은 폐기됐다</b> (#537). 순간이동으로 배치되므로 걸어가 앉는 구간 자체가 없어졌고,
/// 배치 지점은 "어디에 세울 것인가"만 답한다 — 겹쳐 서지 않게 하나씩 나눠 준다.
///
/// 수용 인원은 서버 권위로 세어 NetworkVariable로 전 피어에 동기화한다 (#56 패턴) —
/// 본부 UI는 InmateCount/OnInmateCountChanged를 읽으면 된다.
/// 자물쇠·탈출(#231)은 ReleaseInmate로 이 카운트에서 빠져나간다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class JailZone : NetworkedManagerBase
{
    [Header("수감자 배치 지점 (비우면 감옥 자신의 위치)")]
    [Tooltip(
        "수감자를 세울 지점들. 빈 자리를 앞에서부터 배정한다 — 감옥 방 NavMesh 위, 문 앞 동선을 비켜 둘 것. "
            + "Z축(파랑 화살표)이 서서 바라보는 방향이다.\n\n"
            + "순간이동으로 배치되므로 걸어갈 경로는 필요 없지만, 배회·도주가 이 자리에서 이어지려면 "
            + "NavMesh 위여야 한다"
    )]
    [SerializeField] private Transform[] m_inmatePoints;

    [Header("플레이어 입장 지점 (비우면 감옥 자신의 위치)")]
    [Tooltip("문에 E를 눌러 들어온 플레이어가 서는 자리 — 감옥 방 안. 배치 지점과 겹치지 않게 둘 것")]
    [SerializeField] private Transform m_playerEntryPoint;

    [Header("퇴장 지점 (비우면 감옥 자신의 위치)")]
    [Tooltip(
        "셀에서 나오는 플레이어·반출 대상이 서는 자리 — <b>철창문 안쪽 본관 실내</b>의 NavMesh(HQ 영역) "
            + "위에 둘 것 (#744). 탈옥으로 방출된 수감자도 여기로 나온 뒤 본부를 가로질러 도시로 걸어 나간다.\n\n"
            + "Z축(파랑 화살표)이 <b>본관 안쪽</b>을 보게 둘 것 — 여럿이 한 번에 나올 때 그 방향으로 "
            + "줄이 늘어난다 (ExitSlot). 반대로 두면 자리가 철창 너머로 파고든다"
    )]
    [SerializeField] private Transform m_exitPoint;

    [Header("본부 정문 (비우면 자동 개폐 없음)")]
    [Tooltip(
        "방출·반출 대상이 도시로 나갈 때 열어 줄 본부 정문 (#744). 안 열면 닫힌 문짝을 그대로 "
            + "통과한다 — 문짝 콜라이더는 CharacterController만 막고 NavMeshAgent는 지나간다.\n\n"
            + "닫는 것은 플레이어 몫이다 (E 토글)"
    )]
    [SerializeField] private DoubleDoor[] m_frontDoors;

    [Header("감옥 방 범위 (비우면 자식에서 자동 탐색)")]
    [Tooltip(
        "'이 좌표가 감옥 안인가'를 답하는 부피 — 문 E가 들어가기/나오기를 가르는 유일한 기준이다. "
            + "방 전체를 덮되 도시 쪽과 겹치지 않게 둘 것. Is Trigger를 켜 둘 것(끄면 플레이어를 막는다)"
    )]
    [SerializeField] private BoxCollider m_roomVolume;

    // 서버 권위 수용 인원 — 서버만 쓰고 모든 클라이언트가 읽는다 (#56)
    private readonly NetworkVariable<int> m_inmateCount = new NetworkVariable<int>(0);

    // 오프라인(네트워크 없이 Play) 폴백용 로컬 값 — NpcController의 게이지 이중 구조와 동일
    private int m_localInmateCount;

    // 현재 수감자들의 현상금 합 — 라운드 목표 금액(#395)의 라이브 진행도. 인원 수와 같은 이중 구조.
    private readonly NetworkVariable<int> m_bountyTotal = new NetworkVariable<int>(0);
    private int m_localBountyTotal;

    // 이미 수용된 NPC — 중복 카운트 방어(같은 대상이 두 번 판정·통보되거나 재수용되는 경우)
    private readonly HashSet<NpcController> m_inmates = new HashSet<NpcController>();

    // 방 안의 시체 — m_inmates와 분리한다: 탈옥 발동 판정은 산 수감자만 봐야 하지만, 표지판 총원(InmateCount)에는 잡혀야 한다.
    private readonly HashSet<NpcController> m_corpses = new HashSet<NpcController>();

    // 수감자별 정산 레코드 — 보상액(bounty)과 진범 여부를 수감 시점(NPC 생존 확정)에 박제한다. 라운드 종료 시
    // 잔류 정리(MisdemeanorLoiterer)로 NPC가 파괴돼도 살아 있는 참조 없이 합산할 수 있어, 파괴 타이밍과
    // 정산 읽는 프레임의 경합으로 돌발이벤트 수감자가 누락되던 문제를 없앤다. 탈옥 방출 시 함께 제거되므로
    // 유치장에 남아 있는 대상만 계상된다("끝까지 데리고 있어야 보상"). 서버(또는 오프라인) 전용. (#358/#340)
    private readonly Dictionary<NpcController, InmateRecord> m_records = new Dictionary<NpcController, InmateRecord>();

    // 정산에 필요한 값만 담은 불변 레코드 — 수감 시점 스냅샷이라 NpcController 참조 없이 합산할 수 있다. (#358)
    private readonly struct InmateRecord
    {
        public readonly int Bounty;
        public readonly bool IsCriminal;

        /// <summary>이 수감자를 유치장에 앉힌 인계자들의 clientId — 아무도 없으면 빈 배열. (#484)
        /// 착석 시점 스냅샷이라 그 뒤 손이 바뀌어도 흔들리지 않는다.</summary>
        public readonly ulong[] Deliverers;

        public InmateRecord(int bounty, bool isCriminal, ulong[] deliverers)
        {
            Bounty = bounty;
            IsCriminal = isCriminal;
            Deliverers = deliverers;
        }
    }

    // 배치 지점별 점유자 — 인덱스가 m_inmatePoints와 1:1이다. null이면 빈 자리. 서버(또는 오프라인) 전용. (#462/#537)
    private NpcController[] m_placementOccupants;

    // 정원 초과분을 나눠 세울 커서 — 지점이 전부 찼을 때만 쓴다 (아래 ReservePlacement)
    private int m_overflowCursor;

    /// <summary>현재 수용 인원(산 수감자 + 시체) — 표지판이 보여주는 총원. 탈옥 발동 판정에는 쓰지 말 것, 그건 <see cref="Inmates"/>를 볼 것.</summary>
    public int InmateCount => IsSpawned ? m_inmateCount.Value : m_localInmateCount;

    /// <summary>현재 수감자(산 사람만) — 범인 탈출 이벤트가 방출 대상·발동 전제로 읽는다. 서버에서만 유효.</summary>
    public IReadOnlyCollection<NpcController> Inmates => m_inmates;

    /// <summary>
    /// 셀에서 나오는 대상이 서는 <b>본관 실내</b> 지점 — 미배선이면 감옥 자신의 위치. (#415/#537/#744)
    /// 셀 바닥은 본관과 이어진 경로가 없으므로, 방출·반출·퇴장이 전부 여기로 순간이동한다.
    /// 그 뒤 도시까지는 <b>걸어서</b> 간다 — 그 구간이 본부가 알아채고 막을 수 있는 창이다.
    /// </summary>
    public Transform ExitPoint => m_exitPoint != null ? m_exitPoint : transform;

    /// <summary>
    /// 본부 정문을 연다 — 셀에서 나온 대상이 도시로 나갈 길을 튼다. 서버(또는 오프라인) 전용. (#744/#838)
    ///
    /// <b>이제 여는 것이 통행 그 자체다</b> (#838). #744 때는 문짝 콜라이더가 NavMeshAgent를 막지
    /// 않아 열지 않아도 NPC가 지나갔고, 여는 이유는 "닫힌 문을 뚫고 나가는 그림"을 없애는 것뿐이었다.
    /// 지금은 <see cref="DoorNavBlocker"/>가 닫힌 문 자리의 NavMesh를 도려내므로, 열지 않으면
    /// <b>나갈 길이 없다</b>. 그림을 위해 하던 일이 그대로 통행 조건이 됐다 — 부르는 것을 빠뜨리면
    /// 방출된 대상이 본부 안을 맴돈다.
    ///
    /// <b>닫지는 않는다.</b> 자동으로 닫으면 아직 줄지어 나가는 뒷사람이 갇히고, 몇 명이 남았는지는
    /// 여기서 알 수 없다. 닫는 것은 플레이어의 E다 — 열린 동안 시민이 본부로 들어올 수 있다는 것이
    /// 문으로 통제하기로 한 대가다(#838).
    ///
    /// 잠긴 문은 건너뛴다 — 준비 구간의 잠금은 <b>플레이어</b>의 현장 선점을 막는 것이고
    /// (<see cref="DoubleDoor"/>), 그 구간에는 방출·반출 자체가 없어 여기 걸릴 대상이 없다.
    /// </summary>
    public void ServerOpenFrontDoors()
    {
        if (IsSpawned && !IsServer)
            return;

        if (m_frontDoors == null)
            return;

        for (int i = 0; i < m_frontDoors.Length; i++)
            if (m_frontDoors[i] != null && !m_frontDoors[i].IsLocked)
                m_frontDoors[i].ServerSetOpen(true);
    }

    /// <summary>문에 E를 눌러 들어온 플레이어가 서는 감옥 안 지점 — 미배선이면 감옥 자신의 위치. (#537)</summary>
    public Transform PlayerEntryPoint => m_playerEntryPoint != null ? m_playerEntryPoint : transform;

    /// <summary>입장 지점 둘레의 <paramref name="index"/>번째 자리 — <see cref="ExitSlot"/>의 입장판이다.
    /// 0번은 입장 지점 그 자신(누른 플레이어)이고, 업고 들어온 동료 몸은 1번부터 받는다. (#614)</summary>
    public Vector3 PlayerEntrySlot(int index)
    {
        Transform entry = PlayerEntryPoint;
        if (index <= 0)
            return entry.position;

        int row = (index + 1) / 2;
        float side = (index % 2 == 1) ? -1f : 1f;

        return entry.position
            + entry.right * (side * k_exitSlotSpacing)
            + entry.forward * (row * k_exitSlotSpacing);
    }

    /// <summary>
    /// 퇴장 지점 둘레의 <paramref name="index"/>번째 자리 — <b>여럿이 한 번에 나올 때 겹치지 않게</b> 벌린다. (#537)
    ///
    /// 퇴장 지점 하나에 전부 내보내면 같은 좌표에 겹쳐 놓이고, 물리가 그 겹침을 풀면서 서로를
    /// 튕겨낸다(플레이어의 CharacterController와 NPC 캡슐이 같은 자리에서 만난다).
    ///
    /// 좌우로 번갈아 벌리되 줄은 <b>퇴장 지점이 보는 쪽</b>(본관 안쪽)으로 늘어난다. 반대로 깔면
    /// 뒤가 곧 철창이라 자리가 벽 안으로 파고들고, NavMesh 스냅이 그것을 도로 끌어내면서
    /// 결국 같은 자리에 몰린다.
    ///
    /// 0번은 퇴장 지점 그 자신이다 — 데리고 나오는 플레이어가 쓰는 자리라, 동행은 1번부터 준다.
    /// </summary>
    public Vector3 ExitSlot(int index)
    {
        Transform exit = ExitPoint;
        if (index <= 0)
            return exit.position;

        // 1,2 / 3,4 / ... 로 좌우 번갈아. 한 쌍이 찰 때마다 한 줄씩 앞으로(도시 쪽으로) 나간다.
        int row = (index + 1) / 2;          // 1,1,2,2,3,3...
        float side = (index % 2 == 1) ? -1f : 1f;

        return exit.position
            + exit.right * (side * k_exitSlotSpacing)
            + exit.forward * (row * k_exitSlotSpacing);
    }

    // 퇴장 자리 간격(m) — 플레이어 캡슐(반지름 ~0.4)과 NPC가 서로 밀지 않을 만큼.
    private const float k_exitSlotSpacing = 1.2f;

    /// <summary>방 범위가 배선돼 있는가 — <see cref="ContainsPoint"/>의 false를 '밖'으로 읽어도 되는지 가른다. (#866)</summary>
    public bool HasRoomVolume => m_roomVolume != null;

    /// <summary>
    /// 이 좌표가 감옥 방 안인가 — 범위가 미배선이면 항상 false(문 E가 전부 '들어가기'로 읽힌다). (#537)
    ///
    /// 로컬 공간에서 직접 검사한다: <see cref="Collider.bounds"/>는 회전을 무시하는 월드 AABB라
    /// 방을 비스듬히 놓으면 판정이 어긋나고, 트랜스폼을 옮긴 직후에는 물리 동기화 전까지 낡은 값을 준다.
    /// (옛 <c>JailScanner</c>에서 옮겨 온 판정 — 물리 쿼리도 필요 없다)
    /// 서버·클라 구분 없는 순수 판정이라 어디서 불러도 안전하다.
    /// </summary>
    public bool ContainsPoint(Vector3 position)
    {
        if (m_roomVolume == null)
            return false;

        Vector3 local = m_roomVolume.transform.InverseTransformPoint(position) - m_roomVolume.center;
        Vector3 half = m_roomVolume.size * 0.5f;

        return Mathf.Abs(local.x) <= half.x
            && Mathf.Abs(local.y) <= half.y
            && Mathf.Abs(local.z) <= half.z;
    }

    /// <summary>
    /// 감옥 방 안의 임의의 좌표 — 수감자 배회(<see cref="NpcJailedState"/>)가 다음 목적지를 고를 때 쓴다. (#537)
    /// 방 범위가 미배선이면 감옥 자신의 위치를 돌려준다(그 자리에 머문다).
    ///
    /// 벽에 코를 박지 않게 가장자리를 <see cref="k_roamInset"/>만큼 물린다 — 여기서 고른 점은
    /// 부르는 쪽이 NavMesh로 한 번 더 스냅하므로, 이 함수는 "방 안 아무 데나"만 답하면 된다.
    /// </summary>
    public Vector3 RandomPointInRoom()
    {
        if (m_roomVolume == null)
            return transform.position;

        Vector3 half = m_roomVolume.size * 0.5f;
        // UnityEngine.Random을 명시한다 — 이 파일은 System을 함께 쓰고 있어 이름이 겹친다
        float x = UnityEngine.Random.Range(-half.x + k_roamInset, half.x - k_roamInset);
        float z = UnityEngine.Random.Range(-half.z + k_roamInset, half.z - k_roamInset);

        // 바닥 높이는 부피 아래쪽을 기준으로 잡는다 — 부피 중심은 사람 키보다 위다
        Vector3 local = m_roomVolume.center + new Vector3(x, -half.y, z);
        return m_roomVolume.transform.TransformPoint(local);
    }

    // 배회 목적지를 벽에서 물릴 거리(m) — 사람 반지름보다 넉넉히.
    private const float k_roamInset = 0.9f;

    /// <summary>
    /// 감옥 방 안의 <b>바닥 높이가 맞는</b> 임의의 좌표 — 시체를 눕힐 자리다. 서버(또는 오프라인). (#571)
    ///
    /// ⚠ <b><see cref="RandomPointInRoom"/>을 그대로 쓰면 안 된다.</b> 저쪽은 부피 <b>밑면</b>을
    /// 돌려주는데, 방 부피는 바닥을 조금 파고들게 잡는 것이 정상이라(실측: 부피 밑면 -0.20 / 실제
    /// 바닥 0.25) 그 점은 <b>바닥 속</b>이다. 배회는 부르는 쪽이 NavMesh로 스냅해서 문제가 없었지만,
    /// 시체는 스냅해 줄 에이전트가 없다 — 파묻힌 채 놓으면 겹침 탈출과 중력이 계속 다퉈
    /// <b>바닥에서 비벼지며 떨린다.</b>
    ///
    /// 그래서 여기서 스냅까지 끝내 준다. NavMesh를 기준으로 삼는 이유는 방 안에서 "설 수 있는
    /// 높이"의 단일 진실이 그것이고(수감자가 그 위를 걷는다), 밧줄 끌기도 같은 방식으로 높이를
    /// 잡기 때문이다(<c>NpcRopeDrag.ResolveDragPosition</c>).
    /// </summary>
    public Vector3 RandomRestPointInRoom()
    {
        Vector3 point = RandomPointInRoom();

        if (UnityEngine.AI.NavMesh.SamplePosition(
                point, out UnityEngine.AI.NavMeshHit hit, k_restSnapRadius, UnityEngine.AI.NavMesh.AllAreas))
            return hit.position;

        // 방에 NavMesh가 안 깔린 구성 — 파묻히는 것보다는 부피 밑면 그대로가 낫다(위로 떠도 떨어진다).
        Debug.LogWarning($"JailZone: 감옥 방 바닥을 NavMesh에서 찾지 못했다 — 시체가 바닥에 파묻힐 수 있다: {point:F2}", this);
        return point;
    }

    // 바닥 스냅 탐색 반경(m) — 부피 밑면이 바닥보다 얼마나 아래인지를 덮을 만큼. 방 높이보다는 작게.
    private const float k_restSnapRadius = 3f;

    /// <summary>수용 인원 변경 — 서버·클라이언트 모든 피어에서 발생한다. 본부 UI(별도 이슈)가 구독.</summary>
    public event Action<int> OnInmateCountChanged;

    /// <summary>
    /// 지금 유치장에 잡아둔 대상들의 현상금 합 — 라운드 목표 금액(#395)의 진행도다.
    /// 라운드 종료 정산액(<see cref="TallySettlement"/>의 total)과 같은 레코드에서 나오므로
    /// 진행 중에 보이던 금액과 최종 정산이 어긋나지 않는다. 탈옥으로 방출되면 함께 줄어든다
    /// ("끝까지 데리고 있어야 인정"). 세션 중에는 동기화된 값이라 클라이언트에서도 읽을 수 있다.
    ///
    /// 팀 자금(TeamFund)과 혼동하지 말 것 — 그쪽은 세션 이월 잔액이라 상점 구매로 줄고
    /// 이전 라운드 몫이 섞여 있어 이번 라운드 진행도가 아니다.
    /// </summary>
    public int BountyTotal => IsSpawned ? m_bountyTotal.Value : m_localBountyTotal;

    /// <summary>누적 현상금 변경 — 목표 진행 HUD·라운드 종료 버튼(#395)이 구독한다. 전 피어에서 발생.</summary>
    public event Action<int> OnBountyTotalChanged;

    protected override void Awake()
    {
        base.Awake(); // App.Game.Jail 등록

        // 점유 배열은 배치 지점 수와 1:1 — 지점은 씬 배치라 런타임에 늘지 않으므로 여기서 한 번만 잡는다 (#462)
        m_placementOccupants = new NpcController[m_inmatePoints != null ? m_inmatePoints.Length : 0];

        if (m_roomVolume == null)
            m_roomVolume = GetComponentInChildren<BoxCollider>();

        if (m_roomVolume == null)
            Debug.LogWarning("JailZone: 감옥 방 범위(BoxCollider)가 없다 — 문 E가 나오기를 판단하지 못한다", this);
        else if (!m_roomVolume.isTrigger)
            Debug.LogWarning($"JailZone: 방 범위({m_roomVolume.name})의 Is Trigger가 꺼져 있다 — 플레이어가 막힌다", this);
    }

    public override void OnNetworkSpawn()
    {
        m_inmateCount.OnValueChanged += HandleInmateCountChanged;
        m_bountyTotal.OnValueChanged += HandleBountyTotalChanged;
    }

    public override void OnNetworkDespawn()
    {
        m_inmateCount.OnValueChanged -= HandleInmateCountChanged;
        m_bountyTotal.OnValueChanged -= HandleBountyTotalChanged;
    }

    private void HandleInmateCountChanged(int previous, int current)
    {
        OnInmateCountChanged?.Invoke(current);
    }

    private void HandleBountyTotalChanged(int previous, int current)
    {
        OnBountyTotalChanged?.Invoke(current);
    }

    /// <summary>
    /// 배치 지점 배정 — 수감 대상 1명이 설 자리를 내준다. 서버(또는 오프라인)에서 호출. (#462/#492/#537)
    ///
    /// 손으로 배치한 지점 목록에서 <b>앞에서부터</b> 빈 자리를 고른다. 자리를 계산해 만들지 않는 것이
    /// 핵심이다 — 예전 방식(셀 지점 돌려 쓰기 + GatherSlot 오프셋)은 인원이 늘면 자리가 문 앞 동선에
    /// 떨어졌다. 목록에 그런 자리가 없으면 그 사고가 구조적으로 불가능해진다.
    ///
    /// <b>"놓은 자리에서 가장 가까운 곳"을 더는 보지 않는다</b> (#537). 순간이동으로 배치되므로 걸어갈
    /// 거리라는 것이 없어졌고, 기준이 될 '놓은 자리'도 문 밖이라 방 안 좌표와 무관하다.
    ///
    /// 정원을 넘으면 지점을 돌려 써 겹쳐 세운다 — 지점은 전부 동선 밖이라 겹쳐도 통행을 막지 않는다
    /// (팀 확정 2026-07-30). 조용히 넘어가지 않게 경고를 남긴다.
    /// </summary>
    public Transform ReservePlacement(NpcController npc)
    {
        if (npc == null || m_inmatePoints == null || m_inmatePoints.Length == 0)
            return transform;

        // 이미 자리가 있는 대상이면 그 자리를 그대로 준다 — 재판정·중복 통보로 한 명이 두 자리를 쥐지 않게
        for (int i = 0; i < m_inmatePoints.Length; i++)
            if (m_placementOccupants[i] == npc && m_inmatePoints[i] != null)
                return m_inmatePoints[i];

        // 앞에서부터 빈 자리. 점유자가 파괴됐으면(라운드 종료 잔류 정리 등) Unity의 null 비교가 빈 자리로 본다.
        for (int i = 0; i < m_inmatePoints.Length; i++)
        {
            if (m_inmatePoints[i] == null || m_placementOccupants[i] != null)
                continue;

            m_placementOccupants[i] = npc;
            return m_inmatePoints[i];
        }

        return ShareOverflowPlacement(npc);
    }

    // 정원 초과 — 지점을 돌려 써 겹쳐 세운다. 경고는 여기 한 곳에서만 낸다.
    private Transform ShareOverflowPlacement(NpcController npc)
    {
        Transform shared = NextOverflowPlacement();
        Debug.LogWarning(
            $"[감옥] 배치 지점({m_inmatePoints.Length}개) 초과 — {npc.name}을(를) {shared.name}에 겹쳐 세운다. "
                + "정원을 늘리려면 감옥 방에 배치 지점을 추가할 것",
            this
        );
        return shared;
    }

    // 정원 초과분이 설 자리 — 한 자리에 전부 몰리지 않게 커서로 나눠 준다.
    // 점유 목록에는 올리지 않는다(그 자리 주인은 먼저 온 수감자다) — 그 주인이 방출되면 자리는 정상적으로 빈다.
    private Transform NextOverflowPlacement()
    {
        for (int i = 0; i < m_inmatePoints.Length; i++)
        {
            Transform spot = m_inmatePoints[m_overflowCursor % m_inmatePoints.Length];
            m_overflowCursor = (m_overflowCursor + 1) % m_inmatePoints.Length;

            if (spot != null)
                return spot;
        }

        return transform; // 배선된 지점이 하나도 없다 — 감옥 자신의 위치로 폴백
    }

    // 배치 점유 해제 — 방출·반출로 자리가 빈다. 비우지 않으면 정원이 조용히 줄어든다.
    private void ReleasePlacement(NpcController npc)
    {
        for (int i = 0; i < m_placementOccupants.Length; i++)
            if (m_placementOccupants[i] == npc)
                m_placementOccupants[i] = null;
    }

    /// <summary>
    /// 수용 — 검거 판정 직후 CustodyRouter가 호출한다(NPC가 셀까지 걸어 도착하기를 기다리지 않는다).
    /// 판정 순간 바로 세므로 할당량 종료(#340)가 카운트를 앞질러 마지막 검거가 정산에서 누락되지 않는다.
    /// 탈옥해 풀려난 대상은 ReleaseInmate로 이 카운트에서 빠지므로 "끝까지 데리고 있어야 보상"은 유지된다.
    /// <paramref name="bounty"/>는 이 수감자가 라운드 종료 정산(#340)에 기여할 보상액이다(CustodyRouter가 판정 보상을 넘긴다).
    /// <paramref name="deliverers"/>는 이 수감자를 넣은 인계자들의 clientId — 개인 자금(#484)의 귀속 근거다.
    /// 문 앞 판정 시점에 JailIntake가 확정해 넘긴다(#537 — 착석이라는 별도 시점이 없어졌다). 아무도 없으면 빈 배열.
    /// </summary>
    public void Admit(NpcController npc, int bounty, ulong[] deliverers)
    {
        if (npc == null)
            return;

        // 카운트는 서버 권위 — 클라이언트에서 불려도 무시한다
        if (IsSpawned && !IsServer)
            return;

        if (!m_inmates.Add(npc))
            return; // 이미 수용됨 — 중복 통보 무시

        // 진범 여부를 수감 시점에 판정해 박제한다 — 정산 때 살아 있는 NPC를 다시 안 봐도 되게 (#358).
        m_records[npc] = new InmateRecord(bounty, IsCriminalInmate(npc), deliverers ?? Array.Empty<ulong>()); // 방출을 거친 재수용 시 최신 값으로 갱신
        RefreshInmateCount();
        RefreshBountyTotal();
        Debug.Log($"[유치장] 수용: {npc.name} — 현재 {InmateCount}명, 누적 현상금 {BountyTotal}원");

        // 수감 시 재잠금은 하지 않는다 (#492) — 잠금 복구는 JailLock의 자동 재잠금 타이머 몫이다 (#744).
        // 수감이 잠금까지 겸하면 "털렸으면 어떻게든 되돌린다"가 수감 타이밍에 얹혀, 되돌리는 시점이
        // 유치장 사정에 따라 들쭉날쭉해진다.

        // 수감 중 사망을 지켜본다 — 죽으면 점유에서 빼야 한다 (아래 HandleInmateDied)
        npc.Death.OnDied += HandleInmateDied;

    }

    /// <summary>
    /// 수감 중 사망 — 탈출 가능 점유(<c>m_inmates</c>)에서는 빼지만(탈옥이 시체를 방출 대상으로
    /// 오판하면 안 된다), 몸은 <c>m_corpses</c>로 옮겨 표지판 총원에는 그대로 남는다. 원장은 그대로다.
    /// </summary>
    private void HandleInmateDied(NpcController npc, GameObject killer)
    {
        if (npc == null)
            return;

        npc.Death.OnDied -= HandleInmateDied;

        if (!m_inmates.Remove(npc))
            return; // 이미 방출된 뒤에 죽었다

        ReleasePlacement(npc);
        m_corpses.Add(npc);
        RefreshInmateCount();
        Debug.Log($"[유치장] 수감 중 사망: {npc.name} — 현재 {InmateCount}명 (정산 계상은 유지)");
    }

    /// <summary>
    /// 사망 계상 — 시체를 정산 원장에 올린다. 서버(또는 오프라인) 전용. (#571)
    /// 부르는 곳은 시체 수감(<c>JailIntake.ServerAdmitCorpse</c>) 하나다.
    ///
    /// <c>m_inmates</c>(탈출 가능 점유)에는 넣지 않는다 — 시체는 탈옥으로 달아날 수 없어 그 목록에
    /// 섞이면 <c>JailbreakEvent</c>가 오판한다. 대신 <c>m_corpses</c>에 올려 표지판 총원
    /// (<see cref="InmateCount"/>)에는 잡히게 한다.
    /// </summary>
    /// <param name="bounty">이 시체가 정산에 기여할 보상액.</param>
    /// <param name="deliverers">공을 나눠 가질 clientId — 없으면 빈 배열.</param>
    public void RecordDeceased(NpcController npc, int bounty, ulong[] deliverers)
    {
        if (npc == null)
            return;

        if (IsSpawned && !IsServer)
            return;

        if (m_records.ContainsKey(npc))
            return; // 이미 계상됨

        m_records[npc] = new InmateRecord(bounty, IsCriminalInmate(npc), deliverers ?? Array.Empty<ulong>());
        m_corpses.Add(npc);
        RefreshInmateCount();
        RefreshBountyTotal();
        Debug.Log($"[유치장] 사망 계상: {npc.name} — 현상금 {bounty}원, 누적 {BountyTotal}원, 수용 인원 {InmateCount}명");

    }

    /// <summary>
    /// 사망 계상 취소 — 감옥 밖으로 나간 시체를 정산 원장에서 뺀다. 서버(또는 오프라인) 전용.
    ///
    /// <see cref="RecordDeceased"/>의 역이고, 부르는 곳은 시체 반출
    /// (<c>JailIntake.ServerExitRopedCorpses</c>) 하나다. 저쪽 주석의 "빠져나갈 수 없다"는 전제는
    /// <b>밧줄에 걸린 시체가 문으로 함께 끌려 나오면서</b>(#597) 깨졌다 — 몸이 방에 없는데 계상만
    /// 남으면 정산이 줄지 않고, 그 상태로 다시 넣을 수도 없다(그쪽 <c>ServerReleaseCorpse</c> 주석).
    ///
    /// <b>산 수감자는 여기서 손대지 않는다.</b> 그쪽 방출은 배치 반납·인원 카운트·사망 구독 해제까지
    /// 함께 되돌려야 하므로 <see cref="ReleaseInmate"/> 몫이다. 수감 중 사망한 대상
    /// (<see cref="HandleInmateDied"/>)은 이미 점유에서 빠져 있어 시체와 같은 취급을 받는다 —
    /// 그 몸을 끌고 나가도 계상이 정상적으로 취소된다.
    /// </summary>
    /// <returns>원장에서 실제로 뺐으면 참 — 계상된 적 없는 시체면 거짓.</returns>
    public bool ReleaseDeceased(NpcController npc)
    {
        if (npc == null)
            return false;

        if (IsSpawned && !IsServer)
            return false;

        if (m_inmates.Contains(npc))
        {
            Debug.LogWarning($"[유치장] 사망 계상 취소 실패 — 산 수감자다(ReleaseInmate를 쓸 것): {npc.name}", this);
            return false;
        }

        if (!m_records.Remove(npc))
        {
            Debug.Log($"[유치장] 사망 계상 취소 대상 아님 — 원장에 없다: {npc.name}");
            return false;
        }

        m_corpses.Remove(npc); // 몸이 문 밖으로 나갔다 — 표지판 총원에서도 뺀다
        RefreshInmateCount();
        RefreshBountyTotal();
        Debug.Log($"[유치장] 사망 계상 취소: {npc.name} — 감옥 밖으로 나갔다, 누적 현상금 {BountyTotal}원, 수용 인원 {InmateCount}명");
        return true;
    }

    /// <summary>
    /// 이 수감자의 기록된 현상금 — 없으면 false. 서버(또는 오프라인) 전용. (#517)
    ///
    /// 반출(<see cref="JailIntake.ServerExtract"/>)이 <see cref="ReleaseInmate"/> <b>직전에</b> 읽는다.
    /// 반출은 정산에서 대상을 빼면서 판정 결과까지 잃는데, 감옥 안에서 다시 세우면(추종 정지) 문 앞
    /// 재판정을 거치지 않고 그 자리에서 다시 수감돼야 한다 — 그때 같은 값으로 계상하려고 꺼내 둔다. (#517/#537)
    /// </summary>
    public bool TryGetBounty(NpcController npc, out int bounty)
    {
        bounty = 0;
        if (npc == null || !m_records.TryGetValue(npc, out InmateRecord record))
            return false;

        bounty = record.Bounty;
        return true;
    }

    /// <summary>
    /// 수용 해제 — 범인 탈출 이벤트(별도 이슈)가 호출할 접합점. 카운트에서 뺀다.
    /// 상태 전이(탈출 후 도주 등)는 호출자가 NpcController로 따로 처리한다.
    /// </summary>
    public void ReleaseInmate(NpcController npc)
    {
        if (npc == null)
            return;

        if (IsSpawned && !IsServer)
            return;

        if (!m_inmates.Remove(npc))
            return;

        npc.Death.OnDied -= HandleInmateDied; // 더 이상 수감자가 아니다 — 밖에서 죽어도 점유와 무관하다

        m_records.Remove(npc); // 방출된 수감자는 정산에서 빠진다 — 탈옥해 감옥에 없으면 보상 없음 (#340)
        ReleasePlacement(npc); // 서 있던 자리를 비운다 — 다음 수감자가 그 자리를 쓸 수 있게 (#462/#537)
        RefreshInmateCount();
        RefreshBountyTotal();
        Debug.Log($"[유치장] 수용 해제: {npc.name} — 현재 {InmateCount}명, 누적 현상금 {BountyTotal}원");
    }

    /// <summary>
    /// 정산 원장을 진범/경범죄로 나눈 인원과 보상액 합 — 라운드 종료 정산(#340)이 읽는다.
    /// 진범 여부는 수감(또는 사망 계상) 시점의 <see cref="CitizenIdentity.IsCriminal"/>로 박제된 값이다.
    /// 서버(또는 오프라인) 전용.
    ///
    /// <b>'점유 기반'이 아니라 '원장 기반'이다</b> (#571) — 탈옥 방출된 대상은 점유·원장에서 함께
    /// 빠지므로, 여기 합계는 <see cref="InmateCount"/>(산 수감자 + 시체)와 항상 일치한다.
    /// </summary>
    public (int criminals, int misdemeanors, int total) TallySettlement()
    {
        int criminals = 0;
        int misdemeanors = 0;
        int total = 0;
        // 저장된 레코드로 합산한다 — NPC 오브젝트가 이미 파괴됐어도(라운드 종료 잔류 정리) 계상된다. (#358)
        foreach (InmateRecord record in m_records.Values)
        {
            total += record.Bounty;
            if (record.IsCriminal)
                criminals++;
            else
                misdemeanors++;
        }
        return (criminals, misdemeanors, total);
    }

    /// <summary>
    /// clientId별 귀속 현상금 — 개인 자금 정산(#484)이 읽는다.
    /// 수감자 1명의 현상금을 그 인계자들에게 균등 분배해 합산한다(나머지 원은 버린다).
    /// 비율(10%)은 여기서 적용하지 않는다 — TallySettlement가 할당량을 떼지 않는 것과 같은 이유로, 정책은 SettlementController 몫이다.
    /// 인계자가 없는 수감자(반출 후 밧줄 없이 재수감, #492)는 아무에게도 계상되지 않는다.
    /// 서버(또는 오프라인) 전용.
    /// </summary>
    public Dictionary<ulong, int> TallyDelivererCredits()
    {
        var credits = new Dictionary<ulong, int>();
        foreach (InmateRecord record in m_records.Values)
        {
            if (record.Deliverers.Length == 0) continue;

            int per = record.Bounty / record.Deliverers.Length;
            if (per <= 0) continue;
            
            foreach (ulong clientId in record.Deliverers) 
                credits[clientId] = credits.TryGetValue(clientId, out int sum) ? sum + per : per;
        }

        return credits;
    }

    // 수감 시점의 진범 여부 — 기존 정산 분류와 동일 기준(CitizenIdentity.IsCriminal, 그 외는 경범죄). (#358)
    private static bool IsCriminalInmate(NpcController npc)
    {
        CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();
        return identity != null && identity.IsCriminal;
    }

    // 레코드가 바뀔 때마다 합을 다시 낸다 — 수감자 수가 많지 않아 매번 합산해도 부담이 없고,
    // 재수용으로 레코드가 통째로 교체되는 경우(Admit의 '최신 값으로 갱신')에 증분 갱신보다 안전하다.
    private void RefreshBountyTotal()
    {
        int total = 0;
        foreach (InmateRecord record in m_records.Values)
            total += record.Bounty;

        m_localBountyTotal = total;

        if (IsSpawned && IsServer)
            m_bountyTotal.Value = total; // OnValueChanged를 거쳐 모든 피어에서 이벤트 발생
        else if (!IsSpawned)
            OnBountyTotalChanged?.Invoke(total);
    }

    // 산 수감자 + 시체 합으로 총원을 다시 잰다 — m_inmates/m_corpses가 바뀌는 모든 지점에서 호출한다.
    private void RefreshInmateCount() => SetInmateCount(m_inmates.Count + m_corpses.Count);

    // 서버 진실값과 동기화 변수에 함께 기록한다 — 오프라인에서는 NetworkVariable에 쓰지 않고
    // 이벤트를 직접 발행한다 (NpcController.HandleFsmStateChanged와 동일 구조)
    private void SetInmateCount(int value)
    {
        m_localInmateCount = value;

        if (IsSpawned && IsServer)
            m_inmateCount.Value = value; // OnValueChanged를 거쳐 모든 피어에서 이벤트 발생
        else if (!IsSpawned)
            OnInmateCountChanged?.Invoke(value);
    }
}
