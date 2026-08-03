using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 유치장 창살 문 (#415) — 플레이어가 상호작용키(E)로 여닫는 미닫이 게이트. 문짝이 옆으로 미끄러진다.
///
/// <b>플레이어만 막는다.</b> 닫힌 문짝의 콜라이더는 CharacterController(플레이어)를 막지만
/// NavMeshAgent(NPC)는 NavMesh만 따라가므로 통과한다 — 신병을 끌고 들어가는 흐름(밧줄 끌기는
/// 위치를 직접 세팅한다)에도 걸리지 않는다.
/// 시민이 유치장에 못 들어가는 것은 이 문이 아니라 NavMesh 영역(Jail) 게이팅이 담당한다.
///
/// <b>여닫는 것은 E 토글뿐이다.</b> 경찰은 유치장에 드나들 권한이 있으니 자물쇠
/// (<see cref="JailLock"/>) 잠김과 무관하게 E가 먹힌다. 한 번 열면 다시 누를 때까지 열려 있다 —
/// 신병을 끌고 드나드는 동안 등 뒤에서 닫히지 않게 하기 위해서다.
///
/// 근접 자동 개폐는 제거됐다 (#492). 그것은 <b>수감 이송(Jailed) NPC 전용</b>이었는데 —
/// 스스로 걸어 들어가는 대상이라 열어 줄 주체가 없었다 — 자동 이송이 폐기되면서 그런 NPC가
/// 사라졌다. 이제 신병을 데려오는 것은 사람이고, 그 사람이 문을 연다.
/// 이송 완료 시 수동 개방을 함께 풀던 처리(#457)도 같은 이유로 함께 빠졌다.
///
/// 자물쇠가 풀린 동안(탈옥, #231)에는 아무도 없어도 계속 열어 둔다: "문이 열려 있다"가 탈옥을 알아채는
/// 신호이기 때문이다. <b>그 상태에서 E는 '잠그고 닫기'가 된다</b> (#492) — 자동 재잠금이 제거돼
/// 털린 유치장을 되돌리는 것이 플레이어의 책임이 됐고, 잠그기 전에는 문이 닫히지 않으므로
/// 두 동작을 한 번의 E로 묶는다.
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

    [Header("자물쇠 (비우면 부모에서 자동 탐색)")]
    [Tooltip("풀려 있는 동안(탈옥 진행 중, #231)에는 아무도 없어도 열어 둔다 — 열린 문이 곧 탈옥 신호다")]
    [SerializeField] private JailLock m_jailLock;

    // 서버 권위 개폐 상태 — JailLock·JailZone과 동일한 이중 구조(오프라인 폴백 로컬 값)
    private readonly NetworkVariable<bool> m_isOpenSynced = new NetworkVariable<bool>(false);
    private bool m_localIsOpen;

    // 닫힌 위치 — Awake에 잡아 두고 여기에 m_openOffset을 더한 곳이 열린 위치가 된다
    private Vector3 m_closedLocalPosition;

    // 플레이어가 E로 걸어 둔 개방 — 다시 누를 때까지 유지된다. 개폐 판단이 서버에서만 돌므로
    // 동기화하지 않는다(결과인 m_isOpenSynced만 전 피어가 본다). 서버(또는 오프라인) 전용.
    private bool m_manualOpen;

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

    // 지금 문이 열려 있어야 하는가 — 수동 개방(E)과 탈옥 진행 중 둘의 합.
    // 근접 자동 개폐는 제거됐다 (#492) — 스스로 걸어 들어오는 수감 이송 NPC가 없어졌다.
    // 플레이어 근접도 보지 않는다: 여닫는 것은 플레이어의 명시적 입력(E)이다.
    private bool ShouldBeOpen => m_manualOpen || IsJailbreakHoldingOpen;

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
    /// 유치장 문은 경찰 누구나 여닫을 수 있다 — 자물쇠 잠김·수감 인원과 무관하다.
    /// (사거리·가시선은 PlayerInteractor가 이미 걸러 준다)
    /// </summary>
    public bool CanInteract(GameObject interactor) => m_leaf != null;

    /// <summary>E — 자물쇠가 풀려 있으면 <b>잠그고 닫는다</b>(#492), 잠겨 있으면 여닫기 토글. (#415)</summary>
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

    // E 처리 — 서버(또는 오프라인) 전용. 자물쇠 상태에 따라 두 가지로 갈린다.
    //
    //  · <b>자물쇠가 풀려 있으면(탈옥 이후) 먼저 잠근다.</b> 자동 재잠금이 제거돼(#492) 털린 유치장을
    //    되돌리는 것은 플레이어의 몫이고, 그 조작이 여기다. 문을 닫는 것과 한 동작으로 묶는 이유는
    //    순서 때문이다 — 풀린 동안은 ShouldBeOpen이 문을 계속 열어 두므로(탈옥 신호), 잠그지 않고는
    //    애초에 닫을 수가 없다. 잠금과 개방 래치 해제를 같이 해서 한 번 누르면 "잠그고 닫힌다"가 된다.
    //  · <b>잠겨 있으면 평소대로 여닫기 토글.</b>
    private void ServerToggleManual()
    {
        if (IsSpawned && !IsServer)
            return;

        if (m_jailLock != null && !m_jailLock.IsLocked)
        {
            m_jailLock.ServerRelock();
            m_manualOpen = false; // 잠근 김에 닫는다 — 두 번 누르게 하지 않는다
            ServerSetOpen(ShouldBeOpen);
            return;
        }

        m_manualOpen = !m_manualOpen;
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
        // 매 프레임 평가해도 부담이 없다: 자물쇠 상태를 읽는 것뿐이고(근접 전체 조회는 #492에서
        // 사라졌다), ServerSetOpen이 값이 같으면 조기 반환해 대역폭도 먹지 않는다.
        // 탈옥으로 자물쇠가 풀리는 순간을 문이 따라가야 하므로 계속 본다.
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
