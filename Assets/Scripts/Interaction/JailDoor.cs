using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 유치장 창살 문 (#415) — 플레이어가 다가오면 저절로 열리는 미닫이 자동문. 문짝이 옆으로 미끄러진다.
///
/// <b>플레이어만 막는다.</b> 닫힌 문짝의 콜라이더는 CharacterController(플레이어)를 막지만
/// NavMeshAgent(NPC)는 NavMesh만 따라가므로 통과한다 — 그래서 자동 수감 이송(NpcJailedState)을
/// 깨지 않고, 신병을 직접 끌고 들어가는 흐름(밧줄 끌기는 위치를 직접 세팅한다)에도 걸리지 않는다.
/// 시민이 유치장에 못 들어가는 것은 이 문이 아니라 NavMesh 영역(Jail) 게이팅이 담당한다.
///
/// <b>여닫는 조건은 근접뿐이다.</b> 경찰은 유치장에 드나들 권한이 있으니 자물쇠(<see cref="JailLock"/>)
/// 잠김과 무관하게 열린다 — 상호작용키(E)를 요구하지 않는다. 다만 자물쇠가 풀린 동안(탈옥, #231)에는
/// 근처에 아무도 없어도 계속 열어 둔다: "문이 열려 있다"가 탈옥을 알아채는 신호이기 때문이다.
///
/// 서버 권위 — 개폐 판단과 상태는 서버가 정해 NetworkVariable로 전 피어에 동기화하고(#56),
/// 미끄러지는 연출은 각 피어가 로컬로 보간한다.
///
/// 씬 배치: 문짝(m_leaf)은 NavMesh 베이크에서 제외할 것(NavMeshModifier의 Ignore From Build) —
/// 닫힌 문짝이 베이크에 잡히면 문턱의 NavMesh가 끊겨 수감 이송 경로가 사라진다.
/// </summary>
public class JailDoor : NetworkBehaviour
{
    [Header("문짝 (미끄러지는 창살 게이트)")]
    [SerializeField] private Transform m_leaf;

    [Tooltip("열릴 때 문짝이 이동하는 오프셋(문짝의 부모 기준). 개구부 폭만큼 옆으로 밀면 통로가 완전히 열린다")]
    [SerializeField] private Vector3 m_openOffset = new Vector3(-1.05f, 0f, 0f);

    [Tooltip("완전히 열리거나 닫히는 데 걸리는 시간(초)")]
    [SerializeField] private float m_slideSeconds = 0.7f;

    [Header("자동 개폐")]
    [Tooltip("이 거리(m) 안에 플레이어가 들어오면 저절로 열리고, 모두 벗어나면 닫힌다")]
    [SerializeField] private float m_autoOpenRadius = 3f;

    [Tooltip("근접 검사 주기(초) — 매 프레임 돌 필요가 없다. 0이면 매 프레임 검사한다")]
    [SerializeField] private float m_proximityCheckInterval = 0.1f;

    [Header("자물쇠·유치장 (비우면 부모에서 자동 탐색)")]
    [Tooltip("풀려 있고 아직 수감자가 남아 있는 동안(탈옥 진행 중, #231)에는 근처에 아무도 없어도 열어 둔다")]
    [SerializeField] private JailLock m_jailLock;

    [Tooltip("수감자가 남아 있는지 확인용 — 다 빠져나간 빈 유치장이면 문을 닫는다")]
    [SerializeField] private JailZone m_jailZone;

    // 서버 권위 개폐 상태 — JailLock·JailZone과 동일한 이중 구조(오프라인 폴백 로컬 값)
    private readonly NetworkVariable<bool> m_isOpenSynced = new NetworkVariable<bool>(false);
    private bool m_localIsOpen;

    // 닫힌 위치 — Awake에 잡아 두고 여기에 m_openOffset을 더한 곳이 열린 위치가 된다
    private Vector3 m_closedLocalPosition;

    // 다음 근접 검사까지 남은 시간
    private float m_proximityCooldown;

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

        if (m_jailZone == null)
            m_jailZone = GetComponentInParent<JailZone>();
    }

    public override void OnNetworkSpawn() => m_isOpenSynced.OnValueChanged += HandleOpenSyncedChanged;

    public override void OnNetworkDespawn() => m_isOpenSynced.OnValueChanged -= HandleOpenSyncedChanged;

    private void HandleOpenSyncedChanged(bool previous, bool current) => OnOpenChanged?.Invoke(current);

    // ---- 자동 개폐 판단 (서버 권위) ----

    // 근처에 플레이어가 있으면 연다. 자물쇠가 풀린 동안에는 아무도 없어도 계속 열어 둔다 (#231).
    private void ServerTickAutoDoor()
    {
        m_proximityCooldown -= Time.deltaTime;
        if (m_proximityCooldown > 0f)
            return;
        m_proximityCooldown = m_proximityCheckInterval;

        Vector3 center = DoorCenter;

        // 플레이어 목록을 직접 훑는다 — 최대 6명이라 물리 쿼리보다 싸고, 콜라이더가 빽빽한
        // 도시 씬에서 논알록 버퍼가 넘쳐 대상을 놓치는 문제가 없다 (SuddenEventUtil 주석과 같은 이유)
        bool playerNear = SuddenEventUtil.FindNearestFieldPlayer(center, m_autoOpenRadius) != null;

        // 문을 지나야 하는 NPC도 연다 — 수감 이송되는 진범·위조범·난동꾼(Jailed)과 침입자(Intruding).
        bool npcNear = IsJailBoundNpcNear(center);
        // 탈옥이 '진행 중'일 때만 열어 둔다 — 자물쇠가 풀렸어도 수감자가 다 빠져나갔으면 닫는다.
        // 안 그러면 마지막 수감자가 나간 뒤 다음 수감자가 들어와 재잠금될 때까지 영영 열려 있다.
        // Inmates는 서버 권위 집합이라 이 판단은 서버(또는 오프라인)에서만 유효하다.
        bool jailbreakOpen = m_jailLock != null && !m_jailLock.IsLocked
                             && m_jailZone != null && m_jailZone.Inmates.Count > 0;

        ServerSetOpen(playerNear || npcNear || jailbreakOpen);
    }

    // 문을 통과해야 하는 NPC가 반경 안에 있는가 — 상태 화이트리스트로 본다.
    // 배회 시민까지 세면 본부를 지나가는 것만으로 문이 계속 열려 있게 되고, 애초에 시민은
    // Jail 영역에 못 들어가므로(NavMesh 게이팅) 열어 줄 이유가 없다.
    // 연행(Escorted) 중인 대상은 끌고 있는 플레이어가 반경 안에 있으니 위 판정에서 이미 걸린다.
    private bool IsJailBoundNpcNear(Vector3 center)
    {
        // 검사 주기(m_proximityCheckInterval)로 호출을 눌러 두었기에 목록 훑기로 충분하다.
        NpcController[] npcs = UnityEngine.Object.FindObjectsByType<NpcController>(FindObjectsSortMode.None);
        float sqrRadius = m_autoOpenRadius * m_autoOpenRadius;

        for (int i = 0; i < npcs.Length; i++)
        {
            NpcState state = npcs[i].CurrentState;
            if (state != NpcState.Jailed && state != NpcState.Intruding)
                continue;
            if ((npcs[i].transform.position - center).sqrMagnitude <= sqrRadius)
                return true;
        }

        return false;
    }

    // 근접 판정의 기준점 — 문짝의 <b>닫힌 위치</b>를 월드로 환산해 쓴다.
    // 문짝의 현재 위치를 쓰면 열린 뒤 기준점이 옆으로 옮겨가 반경을 벗어나고, 닫혔다 열리는 진동이 생긴다.
    // 이 오브젝트(문틀)의 원점도 부적합하다 — 실측상 개구부에서 1m 넘게 떨어져 있어 접근 방향에 따라
    // 문 앞에 서고도 반경 밖이 된다. 문짝이 없으면 문틀로 폴백한다.
    private Vector3 DoorCenter =>
        m_leaf != null && m_leaf.parent != null
            ? m_leaf.parent.TransformPoint(m_closedLocalPosition)
            : transform.position;

    /// <summary>
    /// 개폐 설정 — 서버(또는 오프라인) 전용. 자동 판단과 외부 강제(연출·치트)가 모두 이 지점을 지난다.
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
        // 개폐 판단은 서버(또는 오프라인)만 한다 — 클라이언트는 동기화된 IsOpen을 보고 연출만 따라간다
        if (!IsSpawned || IsServer)
            ServerTickAutoDoor();

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
