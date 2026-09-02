using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;

/// <summary>
/// 폭탄 테스트 개발자 단축키 (#947) — <b>에디터 전용</b>. 상자 추첨·등장 2초·30초 카운트다운을
/// 건너뛰고, <b>원하는 자리에서</b> 폭발을 재현한다.
///
/// <list type="bullet">
/// <item><c>[</c> — 내 앞에 폭탄을 놓는다</item>
/// <item><c>]</c> — 지금 터뜨린다</item>
/// <item><c>\</c> — 놓인 폭탄 옆으로 순간이동한다</item>
/// </list>
///
/// 기본값은 <b>제자리 고정</b>이다 — 놓인 폭탄은 <see cref="BombState.Idle"/>이라 쫓아오지도
/// 시계가 돌지도 않는다. 연석·경사·턱 위 같은 지형을 골라 세워 두고 그 자리에서 터뜨리기 위함이다
/// (#947 가림 판정의 회귀 지점이 지형이다). <c>]</c>는 무장과 폭발을 한 프레임에 이어 붙인다 —
/// <see cref="BombDevice.ServerDetonate"/>가 카운트다운 중에만 받기 때문이다.
/// 진짜 이벤트처럼 쫓기며 시험하려면 인스펙터에서 <c>m_armOnSpawn</c>을 켠다.
///
/// <b>클라이언트에서도 듣는다</b> — 키 입력은 로컬이고 스폰·순간이동은 서버 권위라 요청만 서버로
/// 넘긴다. <see cref="SuddenEventDevHotkeys"/>와 달리 MPPM 클론에서 눌러도 <b>그 클론 앞에</b> 놓인다.
///
/// <b>돌발 이벤트 프레임워크는 이 폭탄을 모른다</b> — <see cref="BombChaseEvent.IsActive"/>가 false라
/// 추격 폭탄 이벤트가 겹쳐 발생할 수 있다. 그때는 <see cref="BombDevice.Active"/> 가드에 걸려 이쪽이
/// 물러난다(라운드당 폭탄 1개 전제를 깨지 않는다).
///
/// 관례는 <see cref="SuddenEventDevHotkeys"/>와 같다 — <c>UNITY_EDITOR</c>로 감싸 빌드에서 사라지고,
/// 키보드가 없는 구성에서는 조용히 넘어간다.
/// </summary>
[RequireComponent(typeof(BombChaseEvent))]
public class BombDevHotkeys : NetworkBehaviour
{
#if UNITY_EDITOR
    [Tooltip("끄면 단축키가 듣지 않는다 — 같은 키를 쓰는 다른 테스트를 할 때 잠깐 내린다")]
    [SerializeField] private bool m_enabled = true;

    [Header("키 (인스펙터 조절)")]
    [Tooltip("내 앞에 폭탄을 놓는다")]
    [SerializeField] private Key m_spawnKey = Key.LeftBracket;

    [Tooltip("놓인 폭탄을 지금 터뜨린다 — 무장 전이면 무장부터 시킨다")]
    [SerializeField] private Key m_detonateKey = Key.RightBracket;

    [Tooltip("놓인 폭탄 옆으로 순간이동한다")]
    [SerializeField] private Key m_teleportKey = Key.Backslash;

    [Header("배치 (인스펙터 조절)")]
    [Tooltip("내 앞 몇 m에 놓을지 — 0에 가까우면 내 몸 안에 겹친다")]
    [SerializeField] private float m_spawnDistance = 3f;

    [Tooltip("순간이동으로 폭탄에서 몇 m 떨어져 설지 — 0이면 폭탄에 겹쳐 선다")]
    [SerializeField] private float m_teleportStandoff = 2f;

    [Tooltip("놓는 자리에서 이 거리(m) 안의 NavMesh를 찾아 그 위에 올려놓는다 — " +
             "폭탄은 NavMeshAgent라 떠 있거나 인도 밖이면 굳는다 (BombChaseEvent와 같은 이유)")]
    [SerializeField] private float m_navSampleMaxDistance = 5f;

    [Tooltip("켜면 놓자마자 무장한다 — 진짜 이벤트처럼 쫓아오고 30초 뒤 스스로 터진다. " +
             "끄면(기본) 제자리에 굳어 지형을 골라 세워 둘 수 있다")]
    [SerializeField] private bool m_armOnSpawn;

    [Tooltip("폭발 후 폭탄을 남겨 둘 시간(초) — BombChaseEvent의 잔류 시간과 같은 뜻")]
    [SerializeField] private float m_lingerSeconds;

    private BombChaseEvent m_event;

    // 이 단축키가 놓은 폭탄 — 이벤트가 낸 폭탄은 여기 담기지 않는다(치우는 주인이 다르다).
    private BombDevice m_bomb;
    private bool m_resolved;
    private float m_despawnAt;

    // 서버·오프라인에서만 권위. 스폰 전(오프라인 Play)이면 항상 권위 (BombDevice와 같은 관례).
    private bool IsAuthority => !IsSpawned || IsServer;

    private void Awake() => m_event = GetComponent<BombChaseEvent>();

    private void Update()
    {
        if (!m_enabled)
            return;

        // 키보드가 없는 구성(원격 데스크톱 등)에서는 조용히 넘어간다
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard[m_spawnKey].wasPressedThisFrame)
                RequestSpawn();
            if (keyboard[m_detonateKey].wasPressedThisFrame)
                RequestDetonate();
            if (keyboard[m_teleportKey].wasPressedThisFrame)
                RequestTeleport();
        }

        // 터진 폭탄 치우기 — 이 컴포넌트가 놓은 것은 이 컴포넌트가 치운다
        if (IsAuthority)
            TickDespawn();
    }

    // ---- 요청 (전 피어) → 처리 (서버·오프라인) ----

    private void RequestSpawn()
    {
        if (IsSpawned && !IsServer)
            SpawnRpc();
        else
            ServerSpawn(DevPlayerLookup.LocalPlayer());
    }

    private void RequestDetonate()
    {
        if (IsSpawned && !IsServer)
            DetonateRpc();
        else
            ServerDetonate();
    }

    private void RequestTeleport()
    {
        if (IsSpawned && !IsServer)
            TeleportRpc();
        else
            ServerTeleport(DevPlayerLookup.LocalPlayer());
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)] // 오너 없는 씬 오브젝트
    private void SpawnRpc(RpcParams rpcParams = default) =>
        ServerSpawn(DevPlayerLookup.ResolvePlayer(rpcParams.Receive.SenderClientId));

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void DetonateRpc() => ServerDetonate();

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void TeleportRpc(RpcParams rpcParams = default) =>
        ServerTeleport(DevPlayerLookup.ResolvePlayer(rpcParams.Receive.SenderClientId));

    // ---- 처리 (서버·오프라인) ----

    // 요청자 앞 NavMesh 위에 폭탄을 놓는다. 라운드당 1개 전제를 깨지 않도록 이미 있으면 물러난다.
    private void ServerSpawn(Transform player)
    {
        if (player == null)
        {
            Debug.LogWarning("[폭탄/개발용] 요청자의 플레이어를 찾지 못했다", this);
            return;
        }

        BombDevice prefab = m_event != null ? m_event.DevBombPrefab : null;
        if (prefab == null)
        {
            Debug.LogWarning("[폭탄/개발용] BombChaseEvent에 폭탄 프리팹이 물려 있지 않다", this);
            return;
        }

        if (BombDevice.Active != null)
        {
            Debug.Log($"[폭탄/개발용] 이미 폭탄이 있다 — {m_detonateKey}로 터뜨린 뒤 다시 누를 것");
            return;
        }

        Vector3 forward = Flat(player.forward, Vector3.forward);
        Vector3 wanted = player.position + forward * m_spawnDistance;
        if (!NavMesh.SamplePosition(wanted, out NavMeshHit hit, m_navSampleMaxDistance, NavMesh.AllAreas))
        {
            Debug.LogWarning($"[폭탄/개발용] 눈앞 {wanted}에서 NavMesh를 못 찾았다 — 인도 쪽을 보고 다시 누를 것", this);
            return;
        }

        m_bomb = Instantiate(prefab, hit.position, Quaternion.LookRotation(-forward)); // 나를 마주 본다
        if (SuddenEventUtil.IsNetworkSessionActive)
            m_bomb.GetComponent<NetworkObject>().Spawn();

        m_bomb.OnExploded += HandleExploded;
        m_resolved = false;

        if (m_armOnSpawn)
            m_bomb.ServerArm(); // 상자·등장·대기를 건너뛰고 곧바로 카운트다운

        Debug.Log(
            $"[폭탄/개발용] {m_spawnKey} — 눈앞 {m_spawnDistance}m에 폭탄을 놓았다"
                + (m_armOnSpawn ? " (무장 — 쫓아온다)" : $" (제자리 고정 — {m_detonateKey}로 터뜨릴 것)")
        );
    }

    // 놓인 폭탄이든 이벤트가 낸 폭탄이든 지금 터뜨린다.
    private void ServerDetonate()
    {
        BombDevice bomb = BombDevice.Active;
        if (bomb == null)
        {
            Debug.Log($"[폭탄/개발용] 터뜨릴 폭탄이 없다 — {m_spawnKey}로 먼저 놓을 것");
            return;
        }

        // ServerDetonate는 카운트다운 중에만 받는다(등장·대기 중 즉발을 막는 의도적 게이트) —
        // 제자리 고정으로 놓은 폭탄은 여기서 무장부터 시켜 한 프레임에 이어 붙인다.
        if (!bomb.IsCountingDown)
            bomb.ServerArm();

        bomb.ServerDetonate();
    }

    // 요청자를 폭탄 옆으로 옮긴다 — 폭탄을 마주 보고, 지금 서 있는 쪽에서 다가선 것처럼 선다.
    private void ServerTeleport(Transform player)
    {
        BombDevice bomb = BombDevice.Active;
        if (bomb == null)
        {
            Debug.Log($"[폭탄/개발용] 갈 폭탄이 없다 — {m_spawnKey}로 먼저 놓을 것");
            return;
        }

        if (player == null || !player.TryGetComponent(out PlayerMovement movement))
        {
            Debug.LogWarning("[폭탄/개발용] 요청자의 PlayerMovement를 찾지 못했다", this);
            return;
        }

        Vector3 origin = bomb.transform.position;
        Vector3 away = Flat(player.position - origin, bomb.transform.forward);
        Vector3 wanted = origin + away * m_teleportStandoff;

        // 플레이어는 NavMeshAgent가 아니라 NavMesh 밖이어도 서 있을 수 있다 — 못 찾으면 그대로 보낸다
        Vector3 destination =
            NavMesh.SamplePosition(wanted, out NavMeshHit hit, m_navSampleMaxDistance, NavMesh.AllAreas)
                ? hit.position
                : wanted;

        movement.ServerTeleport(destination, Quaternion.LookRotation(-away));
        Debug.Log($"[폭탄/개발용] {m_teleportKey} — 폭탄 옆 {m_teleportStandoff}m로 이동했다");
    }

    // ---- 뒷정리 ----

    // 폭발 콜백에서 직접 치우지 않는다 — 발행 중이라 뒤 구독자가 사라진 폭탄을 본다 (BombChaseEvent와 같다).
    private void HandleExploded()
    {
        if (m_resolved)
            return;

        m_resolved = true;
        m_despawnAt = Time.time + m_lingerSeconds;
    }

    private void TickDespawn()
    {
        if (m_bomb == null || !m_resolved || Time.time < m_despawnAt)
            return;

        m_bomb.OnExploded -= HandleExploded;
        SuddenEventUtil.DespawnOrDestroy(m_bomb.gameObject);
        m_bomb = null;
        m_resolved = false;
    }

    // ---- 조회 ----

    // 수평 성분만 남긴 단위 벡터 — 위아래를 보고 눌러도 발밑 평면에 놓기 위함.
    private static Vector3 Flat(Vector3 direction, Vector3 fallback)
    {
        direction.y = 0f;
        return direction.sqrMagnitude > 0.0001f ? direction.normalized : fallback;
    }
#endif
}
