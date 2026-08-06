using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 유치장 창살 문 (#415) — 플레이어가 상호작용키(E)로 여닫는 미닫이 게이트. 문짝이 옆으로 미끄러진다.
///
/// <b>근접 자동문이다.</b> 안팎을 가리지 않고 <see cref="m_openRadius"/> 안에 열 자격이 있는 대상이
/// 있으면 열리고, 없으면 닫힌다. 열 자격은 <b>모든 플레이어</b>와, <b>앉은 수감자·포박된 신병·침입자를
/// 뺀 NPC</b>다 (<see cref="CanOpen"/>에 세 예외의 근거가 있다).
///
/// E 토글이 아니라 자동인 이유: 문짝 콜라이더는 CharacterController(플레이어)만 막고 NavMeshAgent와
/// 밧줄 끌기(위치를 직접 세팅한다)는 그대로 지나가므로, 닫힌 문을 신병이 <b>뚫고 통과하는</b> 그림이
/// 나왔다 (#522에서 발견). 막는 쪽을 고치는 대신 <b>지나갈 사람이 오면 열리게</b> 했다 — 경찰은
/// 어차피 드나들 권한이 있어 E는 확인 절차일 뿐이었고, 신병을 끌고 오는 손은 이미 밧줄에 묶여 있다.
///
/// 시민이 유치장에 못 들어가는 것은 이 문이 아니라 NavMesh 영역(Jail) 게이팅이 담당한다 — 자동문이
/// 됐다고 아무 시민이나 걸어 들어오지는 않는다.
///
/// 자물쇠가 풀린 동안(탈옥, #231)에는 아무도 없어도 계속 열어 둔다: "문이 열려 있다"가 탈옥을 알아채는
/// 신호이기 때문이다. <b>E가 남아 있는 경우는 이것 하나뿐이다 — '잠그고 닫기'</b> (#492).
/// 자동 재잠금이 제거돼 털린 유치장을 되돌리는 것이 플레이어의 책임이 됐는데, 자물쇠가 풀린 동안은
/// 근접과 무관하게 열려 있으므로 잠그지 않고는 닫을 방법이 없다. 잠겨 있을 때는 E가 아예 뜨지 않는다.
///
/// 씬 배치: 조준용 콜라이더를 <b>Interactable 레이어</b>에 둘 것 — PlayerInteractor의 조준 마스크가 그
/// 레이어만 본다 (다른 상호작용물과 같은 관례).
///
/// 서버 권위 — 개폐 판단과 상태는 서버가 정해 NetworkVariable로 전 피어에 동기화하고(#56),
/// 미끄러지는 연출은 각 피어가 로컬로 보간한다.
///
/// 씬 배치: 문짝(m_leaf)은 NavMesh 베이크에서 제외할 것(NavMeshModifier의 Ignore From Build) —
/// 닫힌 문짝이 베이크에 잡히면 문턱의 NavMesh가 끊겨 유치장 안팎의 경로가 끊긴다.
/// </summary>
public class JailDoor : NetworkBehaviour, IInteractable
{
    [Header("문짝 (미끄러지는 창살 게이트)")]
    [SerializeField] private Transform m_leaf;

    [Tooltip("열릴 때 문짝이 이동하는 오프셋(문짝의 부모 기준). 개구부 폭만큼 옆으로 밀면 통로가 완전히 열린다")]
    [SerializeField] private Vector3 m_openOffset = new Vector3(-1.05f, 0f, 0f);

    [Tooltip("완전히 열리거나 닫히는 데 걸리는 시간(초)")]
    [SerializeField] private float m_slideSeconds = 0.7f;

    [Header("자동 개폐")]
    [Tooltip("이 반경(m) 안에 열 자격이 있는 대상이 있으면 열린다 — 문을 중심으로 안팎 양쪽을 함께 덮는다")]
    [SerializeField] private float m_openRadius = 3f;

    [Header("자물쇠 (비우면 부모에서 자동 탐색)")]
    [Tooltip("풀려 있는 동안(탈옥 진행 중, #231)에는 아무도 없어도 열어 둔다 — 열린 문이 곧 탈옥 신호다")]
    [SerializeField] private JailLock m_jailLock;

    // 서버 권위 개폐 상태 — JailLock·JailZone과 동일한 이중 구조(오프라인 폴백 로컬 값)
    private readonly NetworkVariable<bool> m_isOpenSynced = new NetworkVariable<bool>(false);
    private bool m_localIsOpen;

    // 닫힌 위치 — Awake에 잡아 두고 여기에 m_openOffset을 더한 곳이 열린 위치가 된다
    private Vector3 m_closedLocalPosition;

    // 근접 조회용 공유 버퍼 — 개폐 판단은 서버(또는 오프라인)에서만 돌므로 정적으로 공유해도 안전하다.
    // 문 하나에 한 프레임 한 번이라 32면 충분하다(넘치면 초과분이 잘릴 뿐, 가까운 것은 대개 남는다).
    private static readonly Collider[] s_openerBuffer = new Collider[32];

    /// <summary>문이 열려 있는가. 세션 중에는 동기화된 값이라 클라이언트에서도 읽을 수 있다.</summary>
    public bool IsOpen => IsSpawned ? m_isOpenSynced.Value : m_localIsOpen;

    /// <summary>개폐 전환 — 소리·램프 연출이 구독할 훅. 전 피어에서 발생한다.</summary>
    public event System.Action<bool> OnOpenChanged;

    private void Awake()
    {
        if (m_leaf != null)
            m_closedLocalPosition = m_leaf.localPosition;

        // 자물쇠는 같은 유치장 오브젝트에 있다 — 부모 쪽에서 찾는다 (JailZone.Awake와 같은 관례)
        if (m_jailLock == null)
            m_jailLock = GetComponentInParent<JailLock>();
    }

    public override void OnNetworkSpawn() => m_isOpenSynced.OnValueChanged += HandleOpenSyncedChanged;

    public override void OnNetworkDespawn() => m_isOpenSynced.OnValueChanged -= HandleOpenSyncedChanged;

    private void HandleOpenSyncedChanged(bool previous, bool current) => OnOpenChanged?.Invoke(current);

    // ---- 자동 개폐 판단 (서버 권위) ----

    // 지금 문이 열려 있어야 하는가 — 근접에 열 자격자가 있거나, 탈옥으로 자물쇠가 풀려 있거나.
    private bool ShouldBeOpen => IsJailbreakHoldingOpen || HasNearbyOpener();

    /// <summary>
    /// 반경 안에 문을 열 자격이 있는 대상이 있는가 — 서버(또는 오프라인) 전용.
    ///
    /// 물리 조회로 후보를 좁힌 뒤 컴포넌트로 가른다. 플레이어 목록 전수 순회(SuddenEventUtil 관례)를
    /// 쓰지 않는 이유는 NPC도 함께 봐야 하기 때문이다 — 시민은 수십 마리라 전수 순회가 훨씬 비싸다.
    /// </summary>
    private bool HasNearbyOpener()
    {
        int count = Physics.OverlapSphereNonAlloc(
            transform.position, m_openRadius, s_openerBuffer, ~0, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < count; i++)
        {
            Collider hit = s_openerBuffer[i];
            if (hit == null)
                continue;

            // 콜라이더가 루트의 자식일 수 있으므로 부모까지 탐색한다 (진압봉·폭발과 같은 관례)
            if (hit.GetComponentInParent<PlayerHealth>() != null)
                return true; // 경찰은 언제나 드나든다 — 다운 여부도 보지 않는다(끌려 들어오는 중일 수 있다)

            NpcController npc = hit.GetComponentInParent<NpcController>();
            if (npc != null && CanOpen(npc))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 이 NPC가 문을 열 수 있는가 — 못 여는 셋을 빼면 전부 연다.
    ///
    ///  · <b>벤치에 앉은 수감자</b>(<see cref="NpcController.IsSeated"/>) — 좌석이 문 근처라 이 예외가
    ///    없으면 수감자가 앉아 있는 내내 문이 열려 있고, 그건 유치장이 아니다.
    ///  · <b>침입자</b>(<see cref="NpcState.Intruding"/>, #231) — 자물쇠를 풀러 오는 자다. 문이 저절로
    ///    열려 주면 잠금 장치를 지나칠 수 있게 되어, 탈옥이 "자물쇠를 푼다"가 아니라 "걸어 들어간다"가
    ///    된다. 자물쇠를 실제로 풀고 나면 그때부터는 <see cref="IsJailbreakHoldingOpen"/>이 열어 준다.
    ///  · <b>포박된 신병</b>(<see cref="NpcState.Captured"/>) — 놓인 자리에 그대로 멈춰 있어 스스로
    ///    문을 지날 일이 없다. 이 예외가 없으면 문 앞에 신병을 놓고 떠난 순간, 아무도 없는 문이
    ///    영영 열려 있다(반출한 수감자를 문 앞에 세워 둔 경우도 같다).
    ///
    /// 끌고 지나가는 중이라면 문은 <b>끄는 플레이어가</b> 연다 — 밧줄 길이(1.6m,
    /// <see cref="NpcRopeDragConfig.RopeLength"/>)가 반경(<see cref="m_openRadius"/> 3m)보다 짧아
    /// 신병이 문턱에 있는 동안 플레이어는 반드시 반경 안에 있다. 둘 중 하나를 조정하면 이 관계를 유지할 것.
    /// </summary>
    private static bool CanOpen(NpcController npc) =>
        !npc.IsSeated && npc.CurrentState is not (NpcState.Intruding or NpcState.Captured);

    // 자물쇠가 풀려 있는 동안은 계속 열어 둔다 — <b>열린 문이 곧 탈옥 신호다</b> (GDD 7-2, #231).
    //
    // 예전에는 여기에 "수감자가 남아 있을 때만"(Inmates.Count > 0)이 붙어 있었다 (#415).
    // 그러면 신호가 아예 안 뜬다: JailbreakEvent.ReleaseAllInmates가 자물쇠가 열리는 순간
    // 전원을 한 번에 방출하므로 같은 프레임에 인원이 0이 되고 조건이 즉시 무너진다. (#492에서 수정)
    //
    // 그 조건이 막으려던 "영영 열려 있음"은 이제 플레이어가 끝낸다 — 문에 E를 누르면 잠기고 닫힌다
    // (아래 ServerToggleManual). 자동 재잠금은 제거됐다: 털린 유치장을 되돌리는 것이 플레이어의
    // 책임이어야 하기 때문이고, 그때까지 열려 있는 문이 "털렸고 아직 안 잠갔다"는 신호로 남는다.
    private bool IsJailbreakHoldingOpen => m_jailLock != null && !m_jailLock.IsLocked;

    // ---- 플레이어 상호작용 (E 토글) ----

    /// <summary>
    /// <b>자물쇠가 풀려 있을 때만</b> E가 뜬다 — 남은 조작은 '잠그고 닫기' 하나뿐이기 때문이다.
    /// 여닫기는 자동이라 평소에는 누를 것이 없다. (사거리·가시선은 PlayerInteractor가 이미 걸러 준다)
    /// </summary>
    public bool CanInteract(GameObject interactor) =>
        m_leaf != null && m_jailLock != null && !m_jailLock.IsLocked;

    /// <summary>E — 털린 유치장을 <b>잠그고 닫는다</b>. (#492/#522)</summary>
    public void Interact(GameObject interactor)
    {
        if (!IsSpawned)
        {
            ServerToggleManual(); // 오프라인 단독 테스트
            return;
        }

        RequestToggleRpc();
    }

    // 클라 입력을 서버로 넘긴다 — 소유권을 요구하지 않는다(씬 오브젝트이고 누구나 여닫는다).
    // 개폐 권위는 서버에 있으므로 여기서 상태를 직접 건드리지 않는다 (CCTVSwitcher와 같은 관례, #362).
    [Rpc(SendTo.Server)]
    private void RequestToggleRpc() => ServerToggleManual();

    // E 처리 — 서버(또는 오프라인) 전용. 털린 유치장을 잠근다.
    //
    // 자동 재잠금이 제거돼(#492) 되돌리는 것은 플레이어의 몫이고, 그 조작이 여기다. 잠그기와 닫기를
    // 한 동작으로 묶는 이유는 순서 때문이다 — 풀린 동안은 ShouldBeOpen이 문을 계속 열어 두므로
    // (탈옥 신호), 잠그지 않고는 애초에 닫을 수가 없다. 잠근 뒤에는 근접 판단이 이어받는다.
    private void ServerToggleManual()
    {
        if (IsSpawned && !IsServer)
            return;

        if (m_jailLock == null || m_jailLock.IsLocked)
            return; // 이미 잠겨 있다 — 누를 것이 없다(CanInteract가 막지만 RPC는 신뢰하지 않는다)

        m_jailLock.ServerRelock();
        ServerSetOpen(ShouldBeOpen);
    }

    /// <summary>
    /// 개폐 설정 — 서버(또는 오프라인) 전용. 개폐 판단과 외부 강제(연출·치트)가 모두 이 지점을 지난다.
    /// </summary>
    public void ServerSetOpen(bool open)
    {
        if (IsSpawned && !IsServer)
            return;

        if (IsOpen == open)
            return;

        m_localIsOpen = open;

        if (IsSpawned && IsServer)
            m_isOpenSynced.Value = open; // OnValueChanged를 거쳐 모든 피어에서 이벤트 발생
        else if (!IsSpawned)
            OnOpenChanged?.Invoke(open);

        Debug.Log($"[유치장 문] {(open ? "열림" : "닫힘")}");
    }

    // ---- 연출 (전 피어 로컬) ----

    private void Update()
    {
        // 개폐 판단은 서버(또는 오프라인)만 한다 — 클라이언트는 동기화된 IsOpen을 보고 연출만 따라간다.
        // 매 프레임 구 하나를 던지는 비용이고(문 하나뿐이다), ServerSetOpen이 값이 같으면 조기 반환해
        // 대역폭도 먹지 않는다.
        if (!IsSpawned || IsServer)
            ServerSetOpen(ShouldBeOpen);

        TickLeafSlide();
    }

    private void TickLeafSlide()
    {
        if (m_leaf == null)
            return;

        Vector3 target = IsOpen ? m_closedLocalPosition + m_openOffset : m_closedLocalPosition;
        if (m_leaf.localPosition == target)
            return;

        // 속도는 "완주 시간"에서 역산한다 — 오프셋을 인스펙터에서 바꿔도 체감 속도가 유지된다
        float speed = m_slideSeconds > 0f ? m_openOffset.magnitude / m_slideSeconds : float.MaxValue;
        m_leaf.localPosition = Vector3.MoveTowards(m_leaf.localPosition, target, speed * Time.deltaTime);
    }

}
