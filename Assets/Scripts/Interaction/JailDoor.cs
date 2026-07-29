using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 유치장 창살 문 (#415) — 상호작용키(E)로 여닫는 미닫이 게이트. 문짝이 옆으로 미끄러진다.
///
/// <b>플레이어만 막는다.</b> 닫힌 문짝의 콜라이더는 CharacterController(플레이어)를 막지만
/// NavMeshAgent(NPC)는 NavMesh만 따라가므로 통과한다 — 그래서 자동 수감 이송(NpcJailedState)을
/// 깨지 않고, 신병을 직접 끌고 들어가는 흐름(밧줄 끌기는 위치를 직접 세팅한다)에도 걸리지 않는다.
/// 시민이 유치장에 못 들어가는 것은 이 문이 아니라 NavMesh 영역(Jail) 게이팅이 담당한다.
///
/// <b>자물쇠와는 분리된 상태다.</b> <see cref="JailLock"/>의 잠김은 탈옥 이벤트(#231)의 조건이고,
/// 이 문의 개폐는 경찰(플레이어)의 편의다 — 플레이어는 권한이 있으니 언제든 여닫는다.
/// 다만 자물쇠 → 문 방향의 단방향 연출은 잇는다: 자물쇠가 해제되면(탈옥) 문이 저절로 열리고,
/// 새 수감자로 재잠금되면 닫힌다.
///
/// 서버 권위 — 개폐 상태는 서버가 정해 NetworkVariable로 전 피어에 동기화하고(#56),
/// 미끄러지는 연출은 각 피어가 로컬로 보간한다.
///
/// 씬 배치: 조준 콜라이더는 <b>움직이지 않는 문틀 쪽</b>에 Interactable 레이어로 둔다 —
/// 문짝에 달면 열린 문짝이 벽 뒤로 들어가 가시선이 막혀 다시 닫을 수 없다.
/// 문짝(m_leaf)은 NavMesh 베이크에서 제외할 것(NavMeshModifier의 Ignore From Build) —
/// 닫힌 문짝이 베이크에 잡히면 문턱의 NavMesh가 끊겨 수감 이송 경로가 사라진다.
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
    [Tooltip("해제되면 문이 저절로 열리고, 재잠금되면 닫힌다 — 단방향 연출 연결 (#231)")]
    [SerializeField] private JailLock m_jailLock;

    // 서버 권위 개폐 상태 — JailLock·JailZone과 동일한 이중 구조(오프라인 폴백 로컬 값)
    private readonly NetworkVariable<bool> m_isOpenSynced = new NetworkVariable<bool>(false);
    private bool m_localIsOpen;

    // 닫힌 위치 — Awake에 잡아 두고 여기에 m_openOffset을 더한 곳이 열린 위치가 된다
    private Vector3 m_closedLocalPosition;

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

    public override void OnNetworkSpawn()
    {
        m_isOpenSynced.OnValueChanged += HandleOpenSyncedChanged;

        if (m_jailLock != null)
            m_jailLock.OnLockChanged += HandleLockChanged;
    }

    public override void OnNetworkDespawn()
    {
        m_isOpenSynced.OnValueChanged -= HandleOpenSyncedChanged;

        if (m_jailLock != null)
            m_jailLock.OnLockChanged -= HandleLockChanged;
    }

    private void HandleOpenSyncedChanged(bool previous, bool current) => OnOpenChanged?.Invoke(current);

    // 자물쇠 → 문 단방향 연결. 잠금 해제(탈옥)면 열고, 재잠금(새 수감자)이면 닫는다.
    // 상태 주인이 서버이므로 서버에서만 반영한다 — 클라이언트는 동기화 값으로 따라온다.
    private void HandleLockChanged(bool locked)
    {
        if (IsSpawned && !IsServer)
            return;

        ServerSetOpen(!locked);
    }

    // ---- 상호작용 (E) ----

    /// <summary>경찰은 권한이 있으니 잠김과 무관하게 언제든 여닫는다 — 조준 윤곽선도 항상 켜진다. (#184)</summary>
    public bool CanInteract(GameObject interactor) => true;

    /// <summary>
    /// 신병을 끌고 온 상태에서도 문을 열 수 있어야 한다 — 끌기 중 E가 '놓기'로 소비되면
    /// 문 앞에서 신병을 내려놓아야만 문을 열 수 있게 되어 동선이 끊긴다. (#414의 우선권 opt-in 재사용)
    /// </summary>
    public bool TakesPriorityOverRelease(GameObject interactor) => true;

    public void Interact(GameObject interactor)
    {
        // 세션 밖(오프라인 단독 Play)에서는 이 피어가 곧 서버다
        if (!IsSpawned)
        {
            ServerSetOpen(!m_localIsOpen);
            return;
        }

        RequestToggleRpc();
    }

    // InvokePermission = Everyone — 문은 씬에 놓인 서버 소유 오브젝트라 오너가 없다.
    // 기본값(오너 전용)이면 아무도 열 수 없다 (ShopStand.RequestPurchaseRpc와 같은 이유).
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestToggleRpc() => ServerSetOpen(!IsOpen);

    /// <summary>개폐 설정 — 서버(또는 오프라인) 전용. 탈옥 이벤트·수감 재잠금 경로도 이 지점을 지난다.</summary>
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
