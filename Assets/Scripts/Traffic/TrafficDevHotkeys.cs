using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 폭주 차량 테스트 개발자 단축키 — <b>에디터 전용</b>. 레인 추첨을 기다리지 않고 <b>지금 내 자리로</b>
/// 차를 부른다.
///
/// <list type="bullet">
/// <item><c>O</c> — 내 앞 <see cref="m_carDistance"/>m에서 차가 나를 향해 달려온다</item>
/// <item><c>P</c> — <b>콤보</b>: 나를 <b>수직으로</b> 날리고, 공중에 있는 동안 차가 내 자리를 지나간다</item>
/// </list>
///
/// <b>콤보가 이 파일의 존재 이유다.</b> "비행 중에 차에 치인다"는 조합은 소유권이 래그돌 구간 안에서
/// 뒤집히는 유일한 경로라(<c>docs/865-down-ragdoll.md</c> §9) 반드시 확인해야 하는데, 폭탄으로
/// 날아가는 사람과 달려오는 차를 손으로 겹치게 하는 것은 사실상 불가능하다.
///
/// <b>왜 수직인가.</b> 위로 날리면 <b>착지 지점 = 출발 지점</b>이라, 차를 "지금 내가 선 자리"로
/// 보내기만 하면 된다 — 궤적 예측이 통째로 사라지고 한 키로 결정적으로 재현된다. 폭발의 실제 궤적과
/// 방향만 다를 뿐 <b>코드 경로는 완전히 같다</b>: 래그돌 재진입(<c>EnterRagdoll</c>의 키네마틱 갈래) ·
/// 소유권 이관 미룸 · 정착 통보까지 그대로 탄다. 진짜 폭탄+차 조합은 최종 확인 때 한 번이면 된다.
///
/// ⚠ <b>높이 띄울수록 잘 맞는 것이 아니다 — 반대다.</b> 차 판정 박스는 높이 1.8m뿐이라 최고점이
/// 그보다 높으면 차가 <b>몸 밑으로 지나간다</b>. 필요한 조건은 "공중"이 아니라 <b>"아직 정착하지 않은
/// 래그돌"</b>이고, 그것은 착지해서 구르는 동안에도 참이다. 그래서 기본값은 낮게(4 m/s, 최고점 0.8m)
/// 잡아 두었고, 차가 도착할 즈음 몸은 바닥에서 뒹구는 중이다 — 그 편이 훨씬 잘 맞는다.
///
/// <b>클라이언트에서도 듣는다</b> — 키 입력은 로컬이고 스폰·발사는 서버 권위라 요청만 서버로 넘긴다.
/// MPPM 클론에서 누르면 <b>그 클론이</b> 날아가고 그 자리로 차가 온다. 이 계열 버그는 클라 캐릭터에서만
/// 나므로 그쪽에서 누르는 것이 기본이다.
///
/// 관례는 <see cref="BombDevHotkeys"/>와 같다 — <c>UNITY_EDITOR</c>로 본문을 감싸 빌드에서 사라지고
/// (클래스는 감싸지 않는다 — NetworkBehaviour 인덱스가 에디터/빌드에서 어긋나지 않게),
/// 키보드가 없는 구성에서는 조용히 넘어간다.
/// </summary>
public class TrafficDevHotkeys : NetworkBehaviour
{
#if UNITY_EDITOR
    [Tooltip("끄면 단축키가 듣지 않는다 — 같은 키를 쓰는 다른 테스트를 할 때 잠깐 내린다")]
    [SerializeField] private bool m_enabled = true;

    [Header("키 (인스펙터 조절)")]
    [Tooltip("내 앞에서 차가 나를 향해 달려온다")]
    [SerializeField] private Key m_carKey = Key.O;

    [Tooltip("콤보 — 나를 위로 날리고, 공중에 있는 동안 차가 내 자리를 지나간다")]
    [SerializeField] private Key m_comboKey = Key.P;

    [Header("차량")]
    [Tooltip(
        "차가 출발할 거리(m) — 나로부터 이만큼 뒤에서 나를 향해 온다. "
        + "⚠ <b>콤보의 성패가 이 값에 달려 있다</b>: 도착 시간(거리÷속도)이 <b>정착 전에</b> 끝나야 한다. "
        + "늦으면 몸이 이미 정착해 기상 블렌드에 들어가는데, 그 상태에서는 EnterRagdoll이 "
        + "'일어나는 중'으로 보고 임펄스를 통째로 버려 <b>제자리에서 죽는다</b>. "
        + "실측: 30m÷20m/s=1.5초는 늦어서 실패, 12m=0.6초면 아직 공중이라 잘 맞는다"
    )]
    [SerializeField] private float m_carDistance = 12f;

    [Tooltip("차 속도(m/s) — 도착까지 걸리는 시간은 거리÷속도다. 콤보의 선행 시간을 이 둘로 맞춘다")]
    [SerializeField] private float m_carSpeed = 20f;

    [Tooltip("차가 나를 지나친 뒤 더 달릴 거리(m) — 짧으면 내 앞에서 멈춰 판정 전에 사라진다")]
    [SerializeField] private float m_carOverrun = 20f;

    [Header("콤보 — 수직 발사")]
    [Tooltip(
        "위로 쏘아 올리는 속도(m/s) — <b>높이 띄우는 것이 목적이 아니다.</b> 차 판정 박스는 높이 "
        + "1.8m라, 최고점(≈속도²÷19.6 m)이 그보다 높으면 차가 몸 밑으로 지나가 아무 일도 안 난다. "
        + "필요한 것은 '아직 정착하지 않은 래그돌'이고 착지 후 구르는 동안도 그 상태다 — 4면 "
        + "최고점 0.8m로 충분하다"
    )]
    [SerializeField] private float m_launchUpSpeed = 4f;

    [Tooltip("비행 상태의 서버 상한(초) — 정착 통보가 안 올 때의 안전장치 (BombBlast와 같은 뜻)")]
    [SerializeField] private float m_launchMaxSeconds = 6f;

    [Tooltip(
        "발사보다 차를 이만큼(초) 먼저 출발시킨다 — 음수면 차가 먼저다. "
        + "차 도착 시각(거리÷속도)과 내 최고점(≈ 상승속도÷9.81초)이 겹치게 맞추는 값이다"
    )]
    [SerializeField] private float m_carHeadStartSeconds;

    private TrafficManager m_traffic;

    // 서버·오프라인에서만 권위 (BombDevHotkeys와 같은 관례).
    private bool IsAuthority => !IsSpawned || IsServer;

    // 콤보의 예약 — 차와 발사 중 <b>늦는 쪽</b> 하나만 여기 담긴다. 서버에서만 돈다.
    private bool m_carPending;
    private float m_carDueAt;
    private Vector3 m_carTarget;
    private Vector3 m_carDirection;

    private bool m_launchPending;
    private float m_launchDueAt;
    private Transform m_launchPlayer;

    private void Awake() =>
        // 매니저지만 App에 안 올라간 타입이라(그쪽 클래스 주석 — R3 ②) R1의 금지 대상이 아니다.
        m_traffic = FindFirstObjectByType<TrafficManager>();

    private void Update()
    {
        if (!m_enabled)
            return;

        WarnIfNotSpawned();

        // 키보드가 없는 구성(원격 데스크톱 등)에서는 조용히 넘어간다
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard[m_carKey].wasPressedThisFrame)
                RequestCar();
            if (keyboard[m_comboKey].wasPressedThisFrame)
                RequestCombo();
        }

        if (IsAuthority)
            TickPending();
    }

    // ⚠ <b>세션이 도는데 이 컴포넌트가 안 스폰됐다 = NetworkObject 없는 오브젝트에 붙였다는 뜻이다.</b>
    // 그러면 아래 요청들이 IsSpawned 거짓 갈래로 새어 <b>클라에서 로컬 처리</b>를 시도하는데, 서버 권위
    // 함수들이 각자 물러나므로 <b>아무 일도 안 일어난다</b> — 증상이 "호스트에서만 된다"로 나타나고
    // 에러가 하나도 안 뜬다. 실제로 한 번 겪은 자리라 소리를 내게 해 둔다.
    //
    // 붙일 곳은 <c>SuddenEvents.prefab</c>이다(BombDevHotkeys·ItemDevHotkeys와 같은 오브젝트).
    private bool m_warnedNotSpawned;

    private void WarnIfNotSpawned()
    {
        if (m_warnedNotSpawned || IsSpawned)
            return;

        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null || !manager.IsListening)
            return; // 오프라인 Play — 로컬 처리가 정상 동작이다

        m_warnedNotSpawned = true;
        Debug.LogError(
            "[차량/개발용] 이 컴포넌트가 스폰되지 않았다 — NetworkObject가 없는 오브젝트에 붙어 있다. "
                + "이대로면 클라이언트 입력이 서버로 가지 못해 <b>호스트에서만</b> 동작한다. "
                + "SuddenEvents 프리팹(BombDevHotkeys와 같은 오브젝트)으로 옮길 것.",
            this
        );
    }

    // ---- 요청 (전 피어) → 처리 (서버·오프라인) ----

    private void RequestCar()
    {
        if (IsSpawned && !IsServer)
            CarRpc();
        else
            ServerCar(DevPlayerLookup.LocalPlayer());
    }

    private void RequestCombo()
    {
        if (IsSpawned && !IsServer)
            ComboRpc();
        else
            ServerCombo(DevPlayerLookup.LocalPlayer());
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)] // 오너 없는 씬 오브젝트
    private void CarRpc(RpcParams rpcParams = default) =>
        ServerCar(DevPlayerLookup.ResolvePlayer(rpcParams.Receive.SenderClientId));

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void ComboRpc(RpcParams rpcParams = default) =>
        ServerCombo(DevPlayerLookup.ResolvePlayer(rpcParams.Receive.SenderClientId));

    // ---- 처리 (서버·오프라인) ----

    // 요청자를 향해 차 한 대를 달리게 한다. 지금 서 있는 자리를 관통하는 직선이다.
    private void ServerCar(Transform player)
    {
        if (!TryBuildRun(player, out Vector3 target, out Vector3 direction))
            return;

        SpawnRunaway(target, direction);
    }

    // 콤보 — 수직으로 날리고, 차를 예약한다.
    private void ServerCombo(Transform player)
    {
        if (!TryBuildRun(player, out Vector3 target, out Vector3 direction))
            return;

        // ⚠ 목표 지점은 <b>발사 전에</b> 잡아 둔 값을 쓴다(TryBuildRun이 위에서 읽었다) — 발사 뒤에
        // 읽으면 이미 몸이 뜨기 시작해 차가 빗나간다.
        //
        // 부호 하나로 갈린다: 양수면 차가 먼저 출발하고 발사가 늦고, 0 이하면 그 반대다.
        if (m_carHeadStartSeconds > 0f)
        {
            SpawnRunaway(target, direction);
            m_launchPending = true;
            m_launchDueAt = Time.time + m_carHeadStartSeconds;
            m_launchPlayer = player;
            return;
        }

        ServerLaunchUp(player);
        m_carPending = true;
        m_carDueAt = Time.time - m_carHeadStartSeconds; // 음수를 더하는 것이라 지연이 된다
        m_carTarget = target;
        m_carDirection = direction;
    }

    // 위로 쏘아 올린다 — 폭발 생존자 경로(BombBlast.ServerLaunchSurvivor)와 <b>같은 두 걸음</b>이다:
    // 상태를 Launched로 세우고(전 피어가 원인 폴링으로 래그돌에 진입한다) 임펄스를 얹는다.
    private void ServerLaunchUp(Transform player)
    {
        if (player == null)
            return;

        PlayerIncapacitation incap = player.GetComponent<PlayerIncapacitation>();
        if (incap == null || incap.IsIncapacitated)
        {
            Debug.LogWarning("[차량/개발용] 이미 무력화된 대상이라 날리지 않는다", this);
            return;
        }

        incap.ServerLaunch(m_launchMaxSeconds);

        Vector3 impulse = Vector3.up * m_launchUpSpeed;
        PlayerRagdoll ragdoll = player.GetComponent<PlayerRagdoll>();

        if (!IsSpawned)
        {
            ragdoll?.EnterRagdoll(impulse); // 오프라인 Play 테스트
            return;
        }

        // 비행(Launched)은 소유권을 안 옮기므로 물리를 도는 것이 <b>맞은 본인</b>이다 — 그쪽에만 보낸다.
        // ⚠ 사망 경로에 이 모양을 복사하지 말 것: 그쪽은 ApplyDeathOwnership이 같은 호출 스택에서
        // 소유권을 서버로 옮겨 OwnerClientId가 이미 서버다 (BombBlast·TrafficVehicle 주석과 같은 함정).
        if (player.TryGetComponent(out NetworkObject victim))
            LaunchRpc(victim, impulse, RpcTarget.Single(victim.OwnerClientId, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void LaunchRpc(NetworkObjectReference victim, Vector3 impulse, RpcParams rpcParams)
    {
        if (victim.TryGet(out NetworkObject resolved)
            && resolved.TryGetComponent(out PlayerRagdoll ragdoll))
            ragdoll.EnterRagdoll(impulse);
    }

    // 콤보가 미뤄 둔 쪽을 때가 되면 실행한다 — 둘 중 하나만 담기므로 분기가 겹치지 않는다.
    private void TickPending()
    {
        if (m_carPending && Time.time >= m_carDueAt)
        {
            m_carPending = false;
            SpawnRunaway(m_carTarget, m_carDirection);
        }

        if (m_launchPending && Time.time >= m_launchDueAt)
        {
            m_launchPending = false;
            Transform player = m_launchPlayer;
            m_launchPlayer = null;
            ServerLaunchUp(player);
        }
    }

    // 목표 지점을 관통하는 직선을 만든다 — 출발점은 목표에서 m_carDistance만큼 뒤다.
    // 방향은 <b>플레이어가 보는 쪽의 반대</b>다: 정면에서 달려와야 오는 것이 보인다.
    private bool TryBuildRun(Transform player, out Vector3 target, out Vector3 direction)
    {
        target = default;
        direction = default;

        if (player == null)
        {
            Debug.LogWarning("[차량/개발용] 요청자의 플레이어를 찾지 못했다", this);
            return false;
        }
        if (m_traffic == null)
        {
            Debug.LogWarning("[차량/개발용] 씬에 TrafficManager가 없다", this);
            return false;
        }

        target = player.position;

        // <b>내 정면에서 나를 향해</b> 온다 — 오는 것이 보여야 타이밍을 눈으로 맞출 수 있다.
        // 내가 +forward를 보고 있으면 차는 그 앞에서 출발해 -forward로 달린다.
        Vector3 facing = player.forward;
        facing.y = 0f;
        direction = facing.sqrMagnitude > 0.0001f ? -facing.normalized : Vector3.back;
        return true;
    }

    private void SpawnRunaway(Vector3 target, Vector3 direction)
    {
        if (m_traffic == null)
            return;

        // 출발점은 목표에서 진행 방향의 <b>반대</b>로 m_carDistance만큼 뒤. 주행 거리는 그 거리에
        // 여유(m_carOverrun)를 더해, 목표에서 멈추지 않고 지나가게 한다.
        Vector3 start = target - direction * m_carDistance;
        m_traffic.DevSpawnRunaway(start, direction, m_carDistance + m_carOverrun, m_carSpeed);

        Debug.Log(
            $"[차량/개발용] 배출 — {m_carDistance}m 뒤에서 {m_carSpeed}m/s "
                + $"(도착까지 약 {m_carDistance / Mathf.Max(0.01f, m_carSpeed):0.00}초)",
            this
        );
    }

    // ---- 조회 (BombDevHotkeys와 같은 구현) ----

#endif
}
