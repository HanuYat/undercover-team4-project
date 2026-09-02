using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;
using Random = UnityEngine.Random;

/// <summary>
/// 시스템 해킹 복구 단말 (#689) — 본부에 놓인 컴퓨터. 해킹 중에만 켜지고, 화면에 뜬 복구 코드를
/// 입력하면 <see cref="DeviceBlackoutEvent"/>가 풀린다.
///
/// <b>기본 설비다</b> — <see cref="InstallableItem"/>이 아니다. 상점 구매품으로 두면 사지 않은 팀은
/// 해킹을 영영 못 푼다. 이슈의 "안전망을 두지 않는다"는 관제가 복구에 <b>실패하는</b> 경우를 말한 것이다.
///
/// 서버 권위 — 코드 생성·정답 판정·제한시간·자리 배정이 전부 서버다. 클라 게이트는 조준 피드백용이라
/// 신뢰하지 않는다. 씬 배선은 없다: 이벤트는 App 경유로 매번 찾고(R1/R8) 화면·초점 지점은 자기 자식이다.
/// </summary>
public class BlackoutRecoveryTerminal : NetworkBehaviour, IInteractable
{
    /// <summary>복구 코드 자릿수 — 화면 표시와 입력 검증이 함께 쓴다.</summary>
    public const int k_codeDigits = 4;

    private const int k_noCode = -1;               // 0은 유효한 코드("0000")라 빈 값으로 못 쓴다
    private const double k_noDeadline = -1d;       // ServerTime은 0에서 시작하므로 음수가 빈 값이다
    private const ulong k_noUser = ulong.MaxValue; // 클라이언트 id는 0부터라 최대값이 빈 자리다

    // 조작된 클라가 한 프레임에 만 번을 쏘면 네 자리 코드는 사실상 즉시 뚫린다. 사람의 입력 속도보다
    // 한참 짧아 정상 입력은 걸리지 않는다.
    private const double k_submitCooldown = 0.2d;

    [Header("화면")]
    [Tooltip("해킹 중에만 켜지는 화면 루트 — 비워 두면 화면 없이 동작한다")]
    [SerializeField] private GameObject m_screenRoot;

    [Header("제한시간")]
    [Tooltip("코드 하나가 유효한 시간(초). 넘기면 서버가 새 코드를 뽑고 입력이 초기화된다")]
    [Min(1f)]
    [SerializeField] private float m_codeSeconds = 10f;

    [Header("카메라 포커스")]
    [Tooltip("상호작용하면 카메라가 이 자리로 옮겨 간다 — 비우면 포커스 없이 동작한다")]
    [SerializeField] private Transform m_focusPoint;

    // 화면에 띄우는 것이 목적이라 코드를 숨기지 않는다 — 판정은 서버가 하므로 무결성과 무관하다.
    private readonly NetworkVariable<int> m_codeSynced = new NetworkVariable<int>(k_noCode);
    private int m_code = k_noCode; // 서버·오프라인의 진실값

    // "남은 시간"이 아니라 만료 시각을 동기화한다 — 늦게 들어온 피어도 카운트다운을 중간부터 이어받는다.
    // (JailSirenButton·RoundTimerSync와 같은 방식)
    private readonly NetworkVariable<double> m_deadlineSynced = new NetworkVariable<double>(k_noDeadline);
    private double m_deadline = k_noDeadline;

    // 자리 판정을 로컬로 두면 같은 프레임에 둘이 앉아 서로 다른 코드를 밀어 넣는다.
    private readonly NetworkVariable<ulong> m_userSynced = new NetworkVariable<ulong>(k_noUser);
    private ulong m_user = k_noUser;

    private PlayerTerminalFocus m_pendingFocus; // 자리 승인을 기다리는 로컬 포커스
    private readonly Dictionary<ulong, double> m_lastSubmit = new Dictionary<ulong, double>();

    // 세션 밖에서는 로컬 시간·0번으로 떨어진다 (PlayerIncapacitation과 동일).
    private double Now => IsSpawned && NetworkManager != null ? NetworkManager.ServerTime.Time : Time.timeAsDouble;
    private ulong LocalId => IsSpawned && NetworkManager != null ? NetworkManager.LocalClientId : 0ul;

    /// <summary>지금 화면에 떠 있는 복구 코드 — 없으면 <c>-1</c>. 전 피어에서 유효하다.</summary>
    public int Code => IsSpawned && !IsServer ? m_codeSynced.Value : m_code;

    /// <summary>코드 하나의 제한시간(초) — 화면 게이지가 진행률을 채울 때 쓴다.</summary>
    public float CodeSeconds => m_codeSeconds;

    /// <summary>코드가 만료되기까지 남은 시간(초) — 서버 시각에서 파생돼 전 피어가 같은 값을 본다.</summary>
    public float RemainingSeconds =>
        (float)Math.Max(0d, (IsSpawned && !IsServer ? m_deadlineSynced.Value : m_deadline) - Now);

    /// <summary>지금 단말 앞에 앉은 클라이언트 — 비어 있으면 <see cref="k_noUser"/>.</summary>
    public ulong User => IsSpawned && !IsServer ? m_userSynced.Value : m_user;

    /// <summary>남이 쓰는 중인가 — 조준 안내가 회색 사유를 붙일지 가른다.</summary>
    public bool IsUsedByOther => User != k_noUser && User != LocalId;

    /// <summary>이 클라이언트의 플레이어가 화면 앞에 앉아 있는가 — 로컬 전용이라 동기화하지 않는다.</summary>
    public bool IsLocalFocused { get; private set; }

    /// <summary>복구 코드가 바뀌었다(발급·오답·시간 초과) — 화면이 구독해 다시 그린다. 전 피어 발생.</summary>
    public event Action<int> OnCodeChanged;

    /// <summary>단말을 지금 쓸 수 있는가 — 해킹 중일 때만이다.</summary>
    public bool IsOnline => Blackout != null && Blackout.IsCommsBlackout;

    /// <summary>카메라가 옮겨 갈 자리 — 없으면 null.</summary>
    public Transform FocusPoint => m_focusPoint;

    // 매니저는 캐싱하지 않고 App 경유로 매번 읽는다 (R1/R8). 이벤트가 없는 구성에서는 null이다.
    private DeviceBlackoutEvent Blackout => App.Game.SuddenEvent?.GetEvent<DeviceBlackoutEvent>();

    public override void OnNetworkSpawn()
    {
        m_codeSynced.OnValueChanged += HandleCodeSyncedChanged;
        m_userSynced.OnValueChanged += HandleUserSyncedChanged;

        // 앉은 채로 접속이 끊기면 자리가 영영 잠겨 그 라운드 동안 아무도 복구를 못 한다.
        if (IsServer && NetworkManager != null)
            NetworkManager.OnClientDisconnectCallback += HandleClientDisconnect;

        // 해킹 도중 들어온 피어는 발급 통지를 놓쳤다.
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

    private void HandleUserSyncedChanged(ulong previous, ulong current) => ApplySeat();

    private void HandleClientDisconnect(ulong clientId) => ServerRelease(clientId);

    // 서버는 SetCode에서 직접 발행하므로 여기선 원격 클라만 중계한다 (이중 발행 방지).
    private void HandleCodeSyncedChanged(int previous, int current)
    {
        if (IsServer)
            return;

        OnCodeChanged?.Invoke(current);
    }

    // OnEnable이 아니라 Start다 — App 매니저 등록이 Awake에서 끝나야 조회가 성립한다. OnEnable에서
    // 물으면 씬 오브젝트 사이 Awake 순서에 따라 null이 돌아와 단말이 영영 안 켜진다 (CCTVSwitcher #382).
    private void Start()
    {
        DeviceBlackoutEvent blackout = Blackout;
        if (blackout == null)
            return; // 이 이벤트가 없는 맵 구성

        blackout.OnCommsBlackoutChanged += HandleBlackoutChanged;
        ApplyScreen(); // 이벤트 도중 씬에 들어온 경우
    }

    // NetworkBehaviour.OnDestroy는 virtual이라 override + base 호출이다 (CCTVSwitcher 관례).
    public override void OnDestroy()
    {
        // ?. 금지 — 파괴된 Unity 오브젝트의 fake null을 우회하지 않게 한다 (HqPanelView 관례)
        DeviceBlackoutEvent blackout = Blackout;
        if (blackout != null)
            blackout.OnCommsBlackoutChanged -= HandleBlackoutChanged;

        base.OnDestroy();
    }

    private void HandleBlackoutChanged(bool active)
    {
        if (!IsSpawned || IsServer)
        {
            SetCode(active ? NewCode() : k_noCode);

            if (!active)
                SetUser(k_noUser);
        }

        ApplyScreen();
    }

    /// <summary>해킹 중에만 상호작용이 뜬다. (#184/#664)</summary>
    public bool CanInteract(GameObject interactor) => IsOnline;

    /// <summary>조준 안내 (#664) — 화면 앞에서 표시를 내리는 일은 <see cref="InteractionFeedback"/>이 한다.</summary>
    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.BlackoutRecovery;

    /// <summary>
    /// 막힌 사유 (#664) — 남이 앉아 있으면 회색 "사용 중". <see cref="CanInteract"/>를 끄지 않는 이유는
    /// 그러면 안내가 통째로 사라져 왜 안 되는지를 말해 주지 못하기 때문이다.
    /// </summary>
    public LocalizedString BlockedReason(GameObject interactor) =>
        IsUsedByOther ? InteractPrompts.ReasonInUse : null;

    /// <summary>E 상호작용 — 자리를 잡고 화면 앞으로 간다. 오너 클라에서만 불린다.</summary>
    public void Interact(GameObject interactor)
    {
        if (!IsOnline || interactor == null)
            return;

        PlayerTerminalFocus focus = interactor.GetComponent<PlayerTerminalFocus>();
        if (focus == null)
        {
            Debug.LogWarning("BlackoutRecoveryTerminal: 상호작용자에게 PlayerTerminalFocus가 없어 화면 포커스를 건너뛴다", this);
            return;
        }

        if (IsLocalFocused)
        {
            focus.Release(); // 보고 있는 단말을 다시 누르면 나간다 — 자리도 함께 비워진다
            return;
        }

        if (IsUsedByOther)
            return; // 이유는 BlockedReason이 안내한다

        // 승인이 떨어진 뒤에야 ApplySeat가 앉힌다 — 낙관적으로 먼저 앉히면 둘이 동시에 눌렀을 때
        // 한쪽이 앉았다 튕겨 나가는 깜빡임이 생긴다.
        m_pendingFocus = focus;

        if (!IsSpawned || IsServer)
            ServerClaim(LocalId);
        else
            RequestClaimRpc();
    }

    // 전 피어에서 돌지만 실제로 움직이는 것은 자리 주인뿐이다.
    private void ApplySeat()
    {
        if (User != LocalId)
        {
            m_pendingFocus = null; // 남이 먼저 잡았다
            return;
        }

        if (m_pendingFocus == null)
            return;

        PlayerTerminalFocus focus = m_pendingFocus;
        m_pendingFocus = null;
        focus.Begin(this);
    }

    // Everyone 권한 — 씬에 놓인 서버 소유 오브젝트라 어떤 플레이어도 오너가 아니다 (JailSirenButton, #55)
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestClaimRpc(RpcParams rpcParams = default) => ServerClaim(rpcParams.Receive.SenderClientId);

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestReleaseRpc(RpcParams rpcParams = default) => ServerRelease(rpcParams.Receive.SenderClientId);

    private void ServerClaim(ulong client)
    {
        if (IsSpawned && !IsServer)
            return;

        if (!IsOnline || (m_user != k_noUser && m_user != client))
            return;

        SetUser(client);
    }

    // 자기 자리만 비울 수 있다 — 위조 RPC로 남을 밀어내지 못하게.
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
            m_userSynced.Value = value;

        ApplySeat(); // 서버·오프라인 로컬 반영 (원격은 동기화 콜백이 담당)
    }

    /// <summary>
    /// 화면 앞에 앉았는지 알린다 — <see cref="PlayerTerminalFocus"/>가 들고 날 때 부른다.
    /// 나갈 때 자리까지 반납한다: 복구·사망·라운드 종료가 모두 Release로 모이므로 여기만 잡으면 안 샌다.
    /// </summary>
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

    /// <summary>입력한 코드를 제출한다 — 판정은 서버가 다시 하므로 여기서 미리 풀지 않는다.</summary>
    public void SubmitCode(int code)
    {
        if (!IsSpawned || IsServer)
        {
            ServerSubmit(code, LocalId);
            return;
        }

        RequestSubmitRpc(code);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestSubmitRpc(int code, RpcParams rpcParams = default) =>
        ServerSubmit(code, rpcParams.Receive.SenderClientId);

    private void ServerSubmit(int code, ulong sender)
    {
        if (IsSpawned && !IsServer)
            return;

        // 사거리·가시선을 서버가 다시 보는 것(#360)과 같은 이유로 제출 빈도도 서버가 본다.
        if (m_lastSubmit.TryGetValue(sender, out double last) && Now - last < k_submitCooldown)
            return;

        m_lastSubmit[sender] = Now;

        DeviceBlackoutEvent blackout = Blackout;
        if (blackout == null || !blackout.IsCommsBlackout)
            return;

        if (code != m_code)
        {
            SetCode(NewCode()); // 찍어 맞히기 누적 방지 — 해제되지 않아 곧바로 다시 입력할 수 있다
            return;
        }

        // 실제로 푼 사람에게만 true — 같은 프레임에 둘이 확정해도 알림이 두 번 나가지 않는다.
        if (blackout.ServerRecover())
            SetCode(k_noCode);
    }

    /// <summary>
    /// 제한시간 감시 — 서버(또는 오프라인) 전용. 넘기면 <b>해제가 아니라 코드 재발급</b>이다:
    /// 풀어 주면 기다리는 것이 공략이 되고, 실패로 끝내면 복구 수단이 사라진다.
    /// </summary>
    private void Update()
    {
        if (IsSpawned && !IsServer)
            return;

        if (m_code == k_noCode || m_deadline < 0d || Now < m_deadline)
            return;

        SetCode(NewCode());
    }

    private static int NewCode()
    {
        int max = 1;
        for (int i = 0; i < k_codeDigits; i++)
            max *= 10;

        return Random.Range(0, max);
    }

    private void SetCode(int value)
    {
        // 만료 시각은 값이 같아도 먼저 민다 — 새 코드가 우연히 직전과 같으면(1/10000) 아래 조기 return에
        // 걸려 제한시간이 영영 안 밀리고 Update가 매 프레임 재추첨을 돈다.
        SetDeadline(value == k_noCode ? k_noDeadline : Now + m_codeSeconds);

        if (m_code == value)
            return;

        m_code = value;
        if (IsSpawned && IsServer)
            m_codeSynced.Value = value;

        OnCodeChanged?.Invoke(value); // 서버·오프라인 로컬 발행 (원격은 동기화 콜백이 담당)
    }

    private void SetDeadline(double value)
    {
        m_deadline = value;
        if (IsSpawned && IsServer)
            m_deadlineSynced.Value = value;
    }

    private void ApplyScreen()
    {
        if (m_screenRoot != null)
            m_screenRoot.SetActive(IsOnline);
    }
}
