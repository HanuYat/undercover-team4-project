using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;
using Random = UnityEngine.Random;

/// <summary>
/// 전자기기 먹통 복구 단말 (#689) — 본부에 놓인 컴퓨터. 먹통 중에만 켜지고, 화면에 뜬 복구 코드를
/// 입력하면 <see cref="DeviceBlackoutEvent"/>가 풀린다.
///
/// <b>기본 설비다</b> — <see cref="InstallableItem"/>이 아니다. 상점 구매품으로 두면 단말을 사지 않은
/// 팀은 먹통을 <b>영영</b> 못 푼다. 이슈의 "안전망을 두지 않는다"는 관제가 복구를 못 하는 경우를 말한
/// 것이지 복구 수단 자체가 없는 경우가 아니다. (<see cref="RoundEndButton"/>과 같은 성격)
///
/// <b>채널링이 아니다</b> — 가만히 서서 게이지를 채우는 것은 시간만 쓰고 판단이 없다. 대신 화면 앞으로
/// 가서 짧은 절차를 수행한다. 그 대가는 시간이 아니라 <b>시야</b>다: 코드를 넣는 동안 관제는 CCTV도
/// 주변도 못 본다. 이 이벤트가 "관제의 일거리"가 되는 지점이 여기다.
///
/// <b>드롭인 프리팹이다</b> — 본부 구조가 바뀌어도 갖다 놓기만 하면 동작한다. 씬·본부 프리팹에 배선할
/// 것이 없다: 먹통 이벤트는 <see cref="App.Game"/> 경유로 매번 찾고(R1/R8), 화면·포커스 지점은 자기
/// 자식이다. <see cref="JailSirenButton"/>이 씬 배선 때문에 내내 무음이었던 전례를 따르지 않는다.
///
/// 서버 권위 — 코드 생성·정답 판정·해제는 전부 서버다. 클라가 보내는 것은 입력한 숫자뿐이고,
/// 클라 쪽 게이트(<see cref="CanInteract"/>)는 조준 피드백용이라 신뢰하지 않는다.
/// </summary>
public class BlackoutRecoveryTerminal : NetworkBehaviour, IInteractable
{
    /// <summary>복구 코드 자릿수 — 화면 표시와 입력 검증이 함께 쓴다.</summary>
    public const int k_codeDigits = 4;

    // 코드가 아직 없음을 나타내는 값 — 먹통이 아닐 때의 상태다. 0은 유효한 코드("0000")라 쓸 수 없다.
    private const int k_noCode = -1;

    [Header("화면")]
    [Tooltip("먹통 중에만 켜지는 화면 루트 — 코드 표시·입력 UI가 이 아래에 있다. 비워 두면 화면 없이 동작한다")]
    [SerializeField] private GameObject m_screenRoot;

    [Header("카메라 포커스")]
    [Tooltip("상호작용하면 카메라가 이 자리로 옮겨 간다 — 화면을 정면으로 크게 보는 지점. 비우면 포커스 없이 동작한다")]
    [SerializeField] private Transform m_focusPoint;

    // 현재 정답 코드 — 서버가 뽑아 전 클라에 내린다. 화면에 띄워야 하므로 숨기지 않는다(그게 목적이다).
    // 판정은 서버가 하므로 값이 보이는 것과 무결성은 무관하다.
    private readonly NetworkVariable<int> m_codeSynced = new NetworkVariable<int>(k_noCode);
    private int m_code = k_noCode; // 서버·오프라인의 진실값 (비네트워크 Play 폴백)

    /// <summary>지금 화면에 떠 있는 복구 코드 — 없으면 <c>-1</c>. 전 피어에서 유효하다.</summary>
    public int Code => IsSpawned && !IsServer ? m_codeSynced.Value : m_code;

    /// <summary>복구 코드가 바뀌었다(발급·오입력 재발급) — 화면이 구독해 다시 그린다. 전 피어에서 발생.</summary>
    public event Action<int> OnCodeChanged;

    /// <summary>단말이 지금 쓸 수 있는가 — 먹통 중일 때만이다. 화면 점등·조준 안내가 같은 값을 본다.</summary>
    public bool IsOnline => Blackout != null && Blackout.IsCommsBlackout;

    /// <summary>카메라가 옮겨 갈 자리 — 없으면 null. 포커스 연출이 읽는다.</summary>
    public Transform FocusPoint => m_focusPoint;

    // 매니저는 캐싱하지 않고 App 경유로 매번 읽는다 (R1/R8). 먹통 이벤트가 없는 구성에서는 null이다.
    private DeviceBlackoutEvent Blackout => App.Game.SuddenEvent?.GetEvent<DeviceBlackoutEvent>();

    public override void OnNetworkSpawn()
    {
        m_codeSynced.OnValueChanged += HandleCodeSyncedChanged;

        // 먹통이 진행 중일 때 들어온 피어 — 발급 통지를 놓쳤으므로 현재 코드로 화면을 맞춘다.
        if (Code != k_noCode)
            OnCodeChanged?.Invoke(Code);

        ApplyScreen();
    }

    public override void OnNetworkDespawn()
    {
        m_codeSynced.OnValueChanged -= HandleCodeSyncedChanged;
    }

    // 서버는 SetCode에서 직접 발행하므로 여기선 원격 클라만 중계한다 (이중 발행 방지 — DeviceBlackoutEvent와 같은 구조)
    private void HandleCodeSyncedChanged(int previous, int current)
    {
        if (IsServer)
            return;

        OnCodeChanged?.Invoke(current);
    }

    // 먹통 구독은 OnEnable이 아니라 Start에서 한다 — App 매니저 등록이 Awake에서 끝나야 SuddenEvent
    // 조회가 성립하기 때문이다. OnEnable에서 물으면 씬 오브젝트 사이의 Awake 순서에 따라 null이
    // 돌아오고, 그 판에서는 단말이 <b>영영 켜지지 않는다</b> (CCTVSwitcher와 같은 이유, #382).
    private void Start()
    {
        DeviceBlackoutEvent blackout = Blackout;
        if (blackout == null)
            return; // 먹통이 인스펙터 리스트에 없는 구성 — 그 이벤트는 발생하지도 않는다

        blackout.OnCommsBlackoutChanged += HandleBlackoutChanged;

        // 이미 먹통이 진행 중인 경우(이벤트 도중 씬 진입)를 즉시 반영한다.
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

    // 먹통이 켜지면 코드를 새로 뽑고, 풀리면 화면을 끈다. 코드 발급은 서버만 한다.
    private void HandleBlackoutChanged(bool active)
    {
        if (!IsSpawned || IsServer)
            SetCode(active ? NewCode() : k_noCode);

        ApplyScreen();
    }

    /// <summary>먹통 중에만 상호작용이 뜬다 — 평상시에는 윤곽선도 안내도 없다. (#184/#664)</summary>
    public bool CanInteract(GameObject interactor) => IsOnline;

    /// <summary>조준 안내 (#664).</summary>
    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.BlackoutRecovery;

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

        focus.Begin(this);
    }

    /// <summary>
    /// 입력한 코드를 제출한다 — 화면 UI가 부른다. 상호작용한 본인의 클라이언트에서 호출된다.
    /// 판정은 서버가 다시 하므로 여기서 맞다고 판단해 미리 풀지 않는다.
    /// </summary>
    public void SubmitCode(int code)
    {
        if (!IsSpawned || IsServer)
        {
            ServerSubmit(code);
            return;
        }

        RequestSubmitRpc(code);
    }

    // Everyone 권한 — 씬(프리팹)에 놓인 서버 소유 오브젝트라 어떤 플레이어도 오너가 아니다 (JailSirenButton과 동일, #55)
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestSubmitRpc(int code) => ServerSubmit(code);

    // 클라 게이트는 신뢰 불가 — 먹통 여부와 정답을 서버가 다시 본다.
    private void ServerSubmit(int code)
    {
        if (IsSpawned && !IsServer)
            return;

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
        if (m_code == value)
            return;

        m_code = value;
        if (IsSpawned && IsServer)
            m_codeSynced.Value = value; // OnValueChanged로 원격 클라에 중계

        OnCodeChanged?.Invoke(value); // 서버·오프라인 로컬 발행 (원격은 위 동기화 콜백이 담당)
    }

    // 먹통 중에만 화면이 켜져 있다 — 꺼진 컴퓨터에 코드가 떠 있으면 안 된다.
    private void ApplyScreen()
    {
        if (m_screenRoot != null)
            m_screenRoot.SetActive(IsOnline);
    }
}
