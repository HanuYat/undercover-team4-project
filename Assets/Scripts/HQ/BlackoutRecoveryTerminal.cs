using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;
using Random = UnityEngine.Random;

/// <summary>
/// 전자기기 해킹 복구 단말 (#689) — 본부에 놓인 컴퓨터. 해킹 중에만 켜지고, 화면에 뜬 복구 코드를
/// 입력하면 <see cref="DeviceBlackoutEvent"/>가 풀린다.
///
/// <b>기본 설비다</b> — <see cref="InstallableItem"/>이 아니다. 상점 구매품으로 두면 단말을 사지 않은
/// 팀은 해킹을 <b>영영</b> 못 푼다. 이슈의 "안전망을 두지 않는다"는 관제가 복구를 못 하는 경우를 말한
/// 것이지 복구 수단 자체가 없는 경우가 아니다. (<see cref="RoundEndButton"/>과 같은 성격)
///
/// <b>채널링이 아니다</b> — 가만히 서서 게이지를 채우는 것은 시간만 쓰고 판단이 없다. 대신 화면 앞으로
/// 가서 짧은 절차를 수행한다. 그 대가는 시간이 아니라 <b>시야</b>다: 코드를 넣는 동안 관제는 CCTV도
/// 주변도 못 본다. 이 이벤트가 "관제의 일거리"가 되는 지점이 여기다.
///
/// <b>드롭인 프리팹이다</b> — 본부 구조가 바뀌어도 갖다 놓기만 하면 동작한다. 씬·본부 프리팹에 배선할
/// 것이 없다: 해킹 이벤트는 <see cref="App.Game"/> 경유로 매번 찾고(R1/R8), 화면·포커스 지점은 자기
/// 자식이다. <see cref="JailSirenButton"/>이 씬 배선 때문에 내내 무음이었던 전례를 따르지 않는다.
///
/// 서버 권위 — 코드 생성·정답 판정·해제는 전부 서버다. 클라가 보내는 것은 입력한 숫자뿐이고,
/// 클라 쪽 게이트(<see cref="CanInteract"/>)는 조준 피드백용이라 신뢰하지 않는다.
/// </summary>
public class BlackoutRecoveryTerminal : NetworkBehaviour, IInteractable
{
    /// <summary>복구 코드 자릿수 — 화면 표시와 입력 검증이 함께 쓴다.</summary>
    public const int k_codeDigits = 4;

    // 코드가 아직 없음을 나타내는 값 — 해킹이 아닐 때의 상태다. 0은 유효한 코드("0000")라 쓸 수 없다.
    private const int k_noCode = -1;

    // 제한시간이 걸려 있지 않음 — ServerTime은 0에서 시작하므로 음수를 쓴다 (JailSirenButton과 동일).
    private const double k_noDeadline = -1d;

    // 아무도 앞에 앉아 있지 않음. 클라이언트 id는 0부터라 최대값을 빈 자리로 쓴다.
    private const ulong k_noUser = ulong.MaxValue;

    // 한 클라이언트가 제출을 다시 보낼 수 있는 최소 간격(초). 사람이 네 자리를 눌러 넣는 데 드는
    // 시간보다 한참 짧아 정상 입력은 걸리지 않고, 조작된 클라가 한 프레임에 만 번을 쏘는 것만 막는다.
    private const double k_submitCooldown = 0.2d;

    [Header("화면")]
    [Tooltip("먹통 중에만 켜지는 화면 루트 — 코드 표시·입력 UI가 이 아래에 있다. 비워 두면 화면 없이 동작한다")]
    [SerializeField] private GameObject m_screenRoot;

    [Header("제한시간")]
    [Tooltip("코드 하나가 유효한 시간(초). 넘기면 서버가 새 코드를 뽑고 입력이 초기화된다")]
    [Min(1f)]
    [SerializeField] private float m_codeSeconds = 10f;

    [Header("카메라 포커스")]
    [Tooltip("상호작용하면 카메라가 이 자리로 옮겨 간다 — 화면을 정면으로 크게 보는 지점. 비우면 포커스 없이 동작한다")]
    [SerializeField] private Transform m_focusPoint;

    // 현재 정답 코드 — 서버가 뽑아 전 클라에 내린다. 화면에 띄워야 하므로 숨기지 않는다(그게 목적이다).
    // 판정은 서버가 하므로 값이 보이는 것과 무결성은 무관하다.
    private readonly NetworkVariable<int> m_codeSynced = new NetworkVariable<int>(k_noCode);
    private int m_code = k_noCode; // 서버·오프라인의 진실값 (비네트워크 Play 폴백)

    // 코드가 만료되는 시각(ServerTime 기준) — "남은 시간"이 아니라 시각을 동기화한다. 그래야 늦게
    // 접속한 피어도 진행 중인 카운트다운을 중간부터 이어 표시하고, 매 프레임 값을 흘려보낼 필요가 없다.
    // (JailSirenButton.m_cooldownEndSynced·RoundTimerSync.m_endServerTime과 같은 방식)
    private readonly NetworkVariable<double> m_deadlineSynced = new NetworkVariable<double>(k_noDeadline);
    private double m_deadline = k_noDeadline; // 서버·오프라인의 진실값

    // 지금 이 단말 앞에 앉은 클라이언트 — 한 번에 한 명만 쓴다. 자리 판정은 서버 권위여야 한다:
    // 로컬 판정으로 두면 같은 프레임에 둘이 앉아 서로 다른 코드를 밀어 넣는다.
    private readonly NetworkVariable<ulong> m_userSynced = new NetworkVariable<ulong>(k_noUser);
    private ulong m_user = k_noUser; // 서버·오프라인의 진실값

    // 승인을 기다리는 로컬 포커스 — 자리를 잡고 나서야 화면 앞으로 간다. 로컬 전용이라 동기화하지 않는다.
    private PlayerTerminalFocus m_pendingFocus;

    // 클라이언트별 마지막 제출 시각 — 서버에서만 쓴다. 값이 아니라 간격만 보므로 라운드마다 비울 필요가 없다.
    private readonly Dictionary<ulong, double> m_lastSubmit = new Dictionary<ulong, double>();

    // 동기화 시계 — 세션 밖에서는 로컬 시간으로 떨어진다 (PlayerIncapacitation과 동일).
    private double Now => IsSpawned && NetworkManager != null ? NetworkManager.ServerTime.Time : Time.timeAsDouble;

    /// <summary>지금 화면에 떠 있는 복구 코드 — 없으면 <c>-1</c>. 전 피어에서 유효하다.</summary>
    public int Code => IsSpawned && !IsServer ? m_codeSynced.Value : m_code;

    /// <summary>코드 하나의 제한시간(초) — 화면 게이지가 진행률을 채울 때 쓴다.</summary>
    public float CodeSeconds => m_codeSeconds;

    /// <summary>지금 코드가 만료되기까지 남은 시간(초). 서버 시각에서 파생돼 전 피어가 같은 값을 본다.</summary>
    public float RemainingSeconds =>
        (float)System.Math.Max(0d, (IsSpawned && !IsServer ? m_deadlineSynced.Value : m_deadline) - Now);

    /// <summary>지금 단말 앞에 앉은 클라이언트 — 비어 있으면 최대값. 전 피어에서 유효하다.</summary>
    public ulong User => IsSpawned && !IsServer ? m_userSynced.Value : m_user;

    /// <summary>남이 쓰는 중인가 — 조준 안내가 회색 사유를 붙일지 가른다.</summary>
    public bool IsUsedByOther => User != k_noUser && User != LocalId;

    // 이 피어의 클라이언트 id — 세션 밖(오프라인 Play)에서는 0으로 떨어진다.
    private ulong LocalId => IsSpawned && NetworkManager != null ? NetworkManager.LocalClientId : 0ul;

    /// <summary>
    /// 이 클라이언트의 플레이어가 지금 이 화면 앞에 앉아 있는가 — <b>로컬 전용</b> 상태다.
    /// 화면이 키 입력을 받을지, 조준 안내를 내릴지가 여기에 달려 있다. 동기화하지 않는다:
    /// 다른 피어에게는 누가 화면을 보고 있는지가 아무 의미도 없다.
    /// </summary>
    public bool IsLocalFocused { get; private set; }

    /// <summary>복구 코드가 바뀌었다(발급·오입력 재발급) — 화면이 구독해 다시 그린다. 전 피어에서 발생.</summary>
    public event Action<int> OnCodeChanged;

    /// <summary>단말이 지금 쓸 수 있는가 — 해킹 중일 때만이다. 화면 점등·조준 안내가 같은 값을 본다.</summary>
    public bool IsOnline => Blackout != null && Blackout.IsCommsBlackout;

    /// <summary>카메라가 옮겨 갈 자리 — 없으면 null. 포커스 연출이 읽는다.</summary>
    public Transform FocusPoint => m_focusPoint;

    // 매니저는 캐싱하지 않고 App 경유로 매번 읽는다 (R1/R8). 먹통 이벤트가 없는 구성에서는 null이다.
    private DeviceBlackoutEvent Blackout => App.Game.SuddenEvent?.GetEvent<DeviceBlackoutEvent>();

    public override void OnNetworkSpawn()
    {
        m_codeSynced.OnValueChanged += HandleCodeSyncedChanged;
        m_userSynced.OnValueChanged += HandleUserSyncedChanged;

        // 앉은 채로 접속이 끊기면 자리가 영영 잠긴다 — 그 라운드 동안 아무도 복구를 못 한다.
        if (IsServer && NetworkManager != null)
            NetworkManager.OnClientDisconnectCallback += HandleClientDisconnect;

        // 해킹이 진행 중일 때 들어온 피어 — 발급 통지를 놓쳤으므로 현재 코드로 화면을 맞춘다.
        if (Code != k_noCode)
            OnCodeChanged?.Invoke(Code);

        ApplyScreen();
    }

    public override void OnNetworkDespawn()
    {
        m_codeSynced.OnValueChanged -= HandleCodeSyncedChanged;
        m_userSynced.OnValueChanged -= HandleUserSyncedChanged;

        if (IsServer && NetworkManager != null)
            NetworkManager.OnClientDisconnectCallback -= HandleClientDisconnect;
    }

    // 자리 주인이 바뀌었다 — 내가 잡았으면 그제야 화면 앞으로 가고, 남이 잡았으면 기다리던 요청을 버린다.
    private void HandleUserSyncedChanged(ulong previous, ulong current) => ApplySeat();

    // 앉아 있던 사람이 나갔으면 자리를 비운다. 서버 전용.
    private void HandleClientDisconnect(ulong clientId) => ServerRelease(clientId);

    // 서버는 SetCode에서 직접 발행하므로 여기선 원격 클라만 중계한다 (이중 발행 방지 — DeviceBlackoutEvent와 같은 구조)
    private void HandleCodeSyncedChanged(int previous, int current)
    {
        if (IsServer)
            return;

        OnCodeChanged?.Invoke(current);
    }

    // 해킹 구독은 OnEnable이 아니라 Start에서 한다 — App 매니저 등록이 Awake에서 끝나야 SuddenEvent
    // 조회가 성립하기 때문이다. OnEnable에서 물으면 씬 오브젝트 사이의 Awake 순서에 따라 null이
    // 돌아오고, 그 판에서는 단말이 <b>영영 켜지지 않는다</b> (CCTVSwitcher와 같은 이유, #382).
    private void Start()
    {
        DeviceBlackoutEvent blackout = Blackout;
        if (blackout == null)
            return; // 해킹이 인스펙터 리스트에 없는 구성 — 그 이벤트는 발생하지도 않는다

        blackout.OnCommsBlackoutChanged += HandleBlackoutChanged;

        // 이미 해킹이 진행 중인 경우(이벤트 도중 씬 진입)를 즉시 반영한다.
        ApplyScreen();
    }

    // NetworkBehaviour.OnDestroy는 virtual이라 반드시 override + base 호출이다 —
    // 새로 선언하면 NGO의 파괴 시 정리가 통째로 가려진다 (CCTVSwitcher와 같은 관례).
    public override void OnDestroy()
    {
        // ?. 금지 — 파괴된 Unity 오브젝트의 fake null을 우회하지 않게 한다 (HqPanelView 관례)
        DeviceBlackoutEvent blackout = Blackout;
        if (blackout != null)
            blackout.OnCommsBlackoutChanged -= HandleBlackoutChanged;

        base.OnDestroy();
    }

    // 해킹이 시작되면 코드를 새로 뽑고, 풀리면 화면을 끈다. 코드 발급은 서버만 한다.
    private void HandleBlackoutChanged(bool active)
    {
        if (!IsSpawned || IsServer)
        {
            SetCode(active ? NewCode() : k_noCode);

            if (!active)
                SetUser(k_noUser); // 복구·라운드 정리로 꺼지면 자리도 비운다
        }

        ApplyScreen();
    }

    /// <summary>해킹 중에만 상호작용이 뜬다 — 평상시에는 윤곽선도 안내도 없다. (#184/#664)</summary>
    public bool CanInteract(GameObject interactor) => IsOnline;

    /// <summary>
    /// 조준 안내 (#664).
    ///
    /// 화면 앞에 앉은 동안 안내·윤곽선·크로스헤어를 내리는 일은 여기가 아니라
    /// <see cref="InteractionFeedback"/>이 한다 — 셋을 한자리에서 같은 조건으로 꺼야
    /// "글자만 사라지고 테두리는 빛나는" 어긋남이 생기지 않는다.
    /// </summary>
    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.BlackoutRecovery;

    /// <summary>
    /// 막힌 사유 (#664) — 남이 앞에 앉아 있으면 회색 "사용 중"이 붙는다.
    ///
    /// <see cref="CanInteract"/>를 false로 만들지 않는 이유: 그러면 안내가 통째로 사라져
    /// "왜 안 되는지"를 어디에서도 말하지 않는다 (IInteractable 주석이 지목한 그 자리다).
    /// </summary>
    public LocalizedString BlockedReason(GameObject interactor) =>
        IsUsedByOther ? InteractPrompts.ReasonInUse : null;

    /// <summary>
    /// E 상호작용 — 화면 앞으로 카메라를 옮기고 입력을 받는다.
    /// <see cref="PlayerInteractor"/>는 오너 클라에서만 도므로 이 호출도 상호작용한 본인에게서만 일어난다
    /// = 포커스는 로컬 연출이고 동기화할 것이 없다.
    /// </summary>
    public void Interact(GameObject interactor)
    {
        if (!IsOnline || interactor == null)
            return;

        // 포커스 컴포넌트가 없는 구성(테스트 플레이어 등)에서는 화면 앞으로 가지 않는다 —
        // 그래도 화면 UI가 살아 있으면 손으로 다가가 누를 수는 있다.
        PlayerTerminalFocus focus = interactor.GetComponent<PlayerTerminalFocus>();
        if (focus == null)
        {
            Debug.LogWarning("BlackoutRecoveryTerminal: 상호작용자에게 PlayerTerminalFocus가 없어 화면 포커스를 건너뛴다", this);
            return;
        }

        // 이미 내가 보고 있으면 나가기다 — 자리도 함께 비워진다(SetLocalFocused 경유).
        if (IsLocalFocused)
        {
            focus.Release();
            return;
        }

        // 남이 앉아 있으면 아무 일도 하지 않는다 — 이유는 BlockedReason이 회색으로 안내한다.
        if (IsUsedByOther)
            return;

        // 자리를 먼저 잡는다. 승인은 서버가 하고, 확정된 뒤에야 ApplySeat가 화면 앞으로 보낸다 —
        // 낙관적으로 먼저 앉히면 같은 프레임에 둘이 앉았다가 한쪽이 튕겨 나가는 깜빡임이 생긴다.
        m_pendingFocus = focus;

        if (!IsSpawned || IsServer)
            ServerClaim(LocalId);
        else
            RequestClaimRpc();
    }

    // 자리 상태를 로컬 표현에 반영한다 — 전 피어에서 돌지만 실제로 움직이는 것은 자리 주인뿐이다.
    private void ApplySeat()
    {
        if (User != LocalId)
        {
            m_pendingFocus = null; // 남이 먼저 잡았다 — 기다리던 요청은 버린다
            return;
        }

        if (m_pendingFocus == null)
            return;

        PlayerTerminalFocus focus = m_pendingFocus;
        m_pendingFocus = null;
        focus.Begin(this);
    }

    // Everyone 권한 — 씬에 놓인 서버 소유 오브젝트라 어떤 플레이어도 오너가 아니다 (JailSirenButton과 동일, #55)
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestClaimRpc(RpcParams rpcParams = default) => ServerClaim(rpcParams.Receive.SenderClientId);

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestReleaseRpc(RpcParams rpcParams = default) => ServerRelease(rpcParams.Receive.SenderClientId);

    // 자리 배정 — 비어 있을 때만 준다. 클라 게이트는 신뢰하지 않으므로 여기서 다시 본다.
    private void ServerClaim(ulong client)
    {
        if (IsSpawned && !IsServer)
            return;

        if (!IsOnline || (m_user != k_noUser && m_user != client))
            return;

        SetUser(client);
    }

    // 자리 반납 — 자기 자리만 비울 수 있다. 위조 RPC로 남을 밀어내지 못하게 한다.
    private void ServerRelease(ulong client)
    {
        if (IsSpawned && !IsServer)
            return;

        if (m_user != client)
            return;

        SetUser(k_noUser);
    }

    private void SetUser(ulong value)
    {
        if (m_user == value)
            return;

        m_user = value;
        if (IsSpawned && IsServer)
            m_userSynced.Value = value; // 원격은 OnValueChanged가 중계한다

        ApplySeat(); // 서버·오프라인 로컬 반영 (원격은 위 동기화 콜백이 담당)
    }

    /// <summary>화면 앞에 앉았는지 알린다 — <see cref="PlayerTerminalFocus"/>가 들고 날 때 부른다.</summary>
    public void SetLocalFocused(bool focused)
    {
        IsLocalFocused = focused;
        if (focused)
            return;

        m_pendingFocus = null;
        if (User != LocalId)
            return;

        if (!IsSpawned || IsServer)
            ServerRelease(LocalId);
        else
            RequestReleaseRpc();
    }

    /// <summary>
    /// 입력한 코드를 제출한다 — 화면 UI가 부른다. 상호작용한 본인의 클라이언트에서 호출된다.
    /// 판정은 서버가 다시 하므로 여기서 맞다고 판단해 미리 풀지 않는다.
    /// </summary>
    public void SubmitCode(int code)
    {
        if (!IsSpawned || IsServer)
        {
            ServerSubmit(code, IsSpawned && NetworkManager != null ? NetworkManager.LocalClientId : 0ul);
            return;
        }

        RequestSubmitRpc(code);
    }

    // Everyone 권한 — 씬(프리팹)에 놓인 서버 소유 오브젝트라 어떤 플레이어도 오너가 아니다 (JailSirenButton과 동일, #55)
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestSubmitRpc(int code, RpcParams rpcParams = default) =>
        ServerSubmit(code, rpcParams.Receive.SenderClientId);

    // 클라 게이트는 신뢰 불가 — 해킹 여부와 정답을 서버가 다시 본다.
    private void ServerSubmit(int code, ulong sender)
    {
        if (IsSpawned && !IsServer)
            return;

        // 제출 간격 제한 — 위조 RPC로 한 프레임에 만 번을 쏘면 네 자리 코드는 사실상 즉시 뚫린다.
        // 사거리·가시선을 서버가 다시 보는 것(#360)과 같은 이유로, 빈도도 서버가 본다.
        if (m_lastSubmit.TryGetValue(sender, out double last) && Now - last < k_submitCooldown)
            return;

        m_lastSubmit[sender] = Now;

        DeviceBlackoutEvent blackout = Blackout;
        if (blackout == null || !blackout.IsCommsBlackout)
            return;

        if (code != m_code)
        {
            // 틀리면 코드를 새로 뽑는다 — 같은 코드를 계속 두면 찍어 맞히는 시도가 누적된다.
            // 해제되지 않았으므로 화면은 그대로 켜져 있고 곧바로 다시 입력할 수 있다.
            SetCode(NewCode());
            return;
        }

        // 실제로 푼 사람에게만 true가 돌아온다 — 같은 프레임에 둘이 확정하는 경합에서 알림이 두 번 나가지 않게.
        if (blackout.ServerRecover())
            SetCode(k_noCode);
    }

    /// <summary>
    /// 제한시간 감시 — 서버(또는 오프라인)만 판정한다. 클라는 동기화된 만료 시각을 읽어 표시만 한다.
    ///
    /// 넘기면 <b>해제되는 것이 아니라 코드가 새로 뽑힌다</b>. 시간을 넘겼다고 풀어 주면 가만히
    /// 기다리는 것이 공략이 되고, 실패로 해킹을 끝내 버리면 복구 수단이 사라진다 — 어느 쪽도
    /// "관제의 일거리"가 아니다. 다시 입력하면 그만이지만 그동안 CCTV는 계속 꺼져 있다.
    /// </summary>
    private void Update()
    {
        if (IsSpawned && !IsServer)
            return;

        if (m_code == k_noCode || m_deadline < 0d || Now < m_deadline)
            return;

        SetCode(NewCode()); // OnCodeChanged가 화면의 입력까지 비운다
    }

    // 코드 발급 — 서버(또는 오프라인) 전용. 자릿수만큼의 십진수 범위에서 고른다.
    private static int NewCode()
    {
        int max = 1;
        for (int i = 0; i < k_codeDigits; i++)
            max *= 10;

        return Random.Range(0, max);
    }

    // 동기화 변수와 로컬 진실값을 함께 갱신하고 화면에 알린다 (DeviceBlackoutEvent.SetBlackout과 같은 구조)
    private void SetCode(int value)
    {
        // 만료 시각은 코드 값이 같아도 먼저 갱신한다 — 새로 뽑은 값이 우연히 직전과 같으면(1/10000)
        // 아래 조기 return에 걸려 제한시간이 영영 안 밀리고, Update가 매 프레임 재추첨을 돈다.
        SetDeadline(value == k_noCode ? k_noDeadline : Now + m_codeSeconds);

        if (m_code == value)
            return;

        m_code = value;
        if (IsSpawned && IsServer)
            m_codeSynced.Value = value; // OnValueChanged로 원격 클라에 중계

        OnCodeChanged?.Invoke(value); // 서버·오프라인 로컬 발행 (원격은 위 동기화 콜백이 담당)
    }

    // 만료 시각도 코드와 같은 이중 구조다 — 서버가 진실값을 들고, 동기화 변수로 전 클라에 내린다.
    private void SetDeadline(double value)
    {
        m_deadline = value;
        if (IsSpawned && IsServer)
            m_deadlineSynced.Value = value;
    }

    // 해킹 중에만 화면이 켜져 있다 — 꺼진 컴퓨터에 코드가 떠 있으면 안 된다.
    private void ApplyScreen()
    {
        if (m_screenRoot != null)
            m_screenRoot.SetActive(IsOnline);
    }
}
