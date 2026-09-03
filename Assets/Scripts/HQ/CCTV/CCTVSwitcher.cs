using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 본부 CCTV 콘솔의 상태 보유자 — 채널·전원을 서버 권위로 동기화하고,
/// 실제 카메라 렌더/RT 적용은 각 클라가 로컬로 수행한다. (#43-A, #362)
/// 상호작용은 이 컴포넌트가 아니라 별도 버튼이 담당한다 — 조준 윤곽선이
/// 모니터 전체가 아니라 눌릴 버튼에만 켜지게 하기 위함. (#362)
/// </summary>
public class CCTVSwitcher : NetworkBehaviour
{
    private const int k_noEntry = -1;

    // 세 자리까지 받는다 — 채널이 수십 개로 늘어도 남고, 넘치면 새 번호로 다시 시작한다.
    private const int k_maxEntry = 999;


    // 카메라가 아니라 설치물(노드)을 받는다 — 카메라 컴포넌트는 조준 리그 안쪽에 있어
    // 인스펙터 배열에 죄다 같은 자식 이름으로 뜬다. 노드는 이름을 붙이는 루트에 있다.
    [Tooltip("이 콘솔이 돌려 볼 CCTV들 — 순서가 곧 채널 번호다")]
    [SerializeField]
    CCTVNode[] m_installations;

    [SerializeField]
    RenderTexture m_monitorRt;

    // IR 볼륨 레이어 — 캐싱 시 한 번만 배선, 런타임엔 건드리지 않는다. (#677)
    [SerializeField]
    LayerMask m_volumeLayers = ~0;

    [SerializeField]
    Color m_monitorBacklightEmission = new(0.05f, 0.07f, 0.09f);

    [SerializeField]
    Color m_infraredMonitorEmission = new(1.4f, 1.4f, 1.4f);

    private Camera[] m_cameras;
    private Renderer m_monitorRenderer;

    private readonly NetworkVariable<int> m_currentIndex = new(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    // 전원 — 플레이어가 끈다. Game 씬이 매 라운드 재로드되므로 초기값 true면 항상 켜진 채 시작한다.
    private readonly NetworkVariable<bool> m_isPowered = new(
        true,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    // 적외선(야시경) — 콘솔 단위 설정이라 채널·전원과 독립. 라운드마다는 false로 새로 시작한다. (#677)
    private readonly NetworkVariable<bool> m_isInfrared = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    // 키패드에 눌린 번호. 버튼이 공용 물체라 콘솔 앞에 둘이 서면 같은 숫자를 봐야 한다
    // (RemoteDoorConsole이 선택을 동기화하는 것과 같은 이유). -1은 입력 없음.
    private readonly NetworkVariable<int> m_entry = new(
        k_noEntry,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    // 외부 차단(먹통 이벤트) — 이미 동기화된 플래그에서 각 피어가 로컬로 유도하므로 동기화하지 않는다.
    private bool m_externallyJammed;

    // 구독 해제를 위해 들고 있는 먹통 이벤트 — 없는 구성에서는 null이다. (#382)
    private DeviceBlackoutEvent m_blackout;

    /// <summary>현재 채널의 설치 위치 이름 — 송출 중이 아니면 빈 문자열. (#362 라벨용)</summary>
    public string CurrentLocationLabel =>
        IsDisplaying ? GetLocationLabel(CurrentIndex) : string.Empty;

    /// <summary>
    /// index번째 채널의 설치 위치 이름 — 범위 밖이거나 미배선이면 빈 문자열.
    /// 송출 여부를 보지 않는다: 채널 목록은 실시간 정보가 아니라 배치표라 화면이 꺼져도 유효하다.
    /// </summary>
    public string GetLocationLabel(int index) =>
        m_installations != null
        && index >= 0
        && index < m_installations.Length
        && m_installations[index] != null
            ? m_installations[index].LocationLabel
            : string.Empty;

    public int ChannelCount => m_installations != null ? m_installations.Length : 0;
    public int CurrentIndex => m_currentIndex.Value;
    public bool IsPowered => m_isPowered.Value;
    public bool IsExternallyJammed => m_externallyJammed;
    public bool IsInfrared => m_isInfrared.Value;

    /// <summary>화면이 실제로 송출 중인가 — 전원·외부 차단·유효 채널을 모두 만족해야 한다.</summary>
    public bool IsDisplaying => m_isPowered.Value && !m_externallyJammed && ChannelCount > 0;

    /// <summary>키패드에 지금까지 눌린 번호 — 아무것도 안 눌렀으면 -1. 표시용 1-based 값이다.</summary>
    public int PendingEntry => m_entry.Value;

    /// <summary>표시 상태 변화 — 채널 라벨·미니맵 하이라이트가 구독한다. (#362)</summary>
    public event Action OnDisplayChanged;

    public override void OnNetworkSpawn()
    {
        CacheNodes();
        m_currentIndex.OnValueChanged += HandleIndexChanged;
        m_isPowered.OnValueChanged += HandlePowerChanged;
        m_isInfrared.OnValueChanged += HandleInfraredChanged;
        m_entry.OnValueChanged += HandleEntryChanged;
        Apply();
    }

    public override void OnNetworkDespawn()
    {
        m_currentIndex.OnValueChanged -= HandleIndexChanged;
        m_isPowered.OnValueChanged -= HandlePowerChanged;
        m_isInfrared.OnValueChanged -= HandleInfraredChanged;
        m_entry.OnValueChanged -= HandleEntryChanged;
    }

    // 먹통 구독은 OnNetworkSpawn이 아니라 Start에서 한다 — App 매니저 등록이 Awake에서
    // 끝나야 SuddenEvent 조회가 성립하기 때문이다(RoundManager와 같은 이유). (#382)
    private void Start()
    {
        m_blackout = App.Game.SuddenEvent?.GetEvent<DeviceBlackoutEvent>();
        if (m_blackout == null)
            return; // 먹통이 인스펙터 리스트에 없거나 꺼진 구성 — 그 이벤트는 발생하지도 않는다

        m_blackout.OnCommsBlackoutChanged += SetExternallyJammed;

        // 이미 먹통이 진행 중인 경우(늦은 접속·이벤트 도중 씬 진입)를 즉시 반영한다.
        SetExternallyJammed(m_blackout.IsCommsBlackout);
    }

    // NetworkBehaviour.OnDestroy는 virtual이라 반드시 override + base 호출이다 —
    // 새로 선언하면 NGO의 파괴 시 정리가 통째로 가려진다(PlayerEscorter와 같은 관례).
    public override void OnDestroy()
    {
        if (m_blackout != null)
            m_blackout.OnCommsBlackoutChanged -= SetExternallyJammed;

        base.OnDestroy();
    }

    private void CacheNodes()
    {
        m_monitorRenderer = GetComponent<Renderer>();

        int count = ChannelCount;
        m_cameras = new Camera[count];
        for (int i = 0; i < count; i++)
        {
            if (m_installations[i] == null)
                continue;

            m_installations[i].SetChannel(i + 1); // 번호는 배열 순서가 정한다 — 한 번만 밀면 된다

            // InChildren — 카메라가 노드와 같은 오브젝트에 있어도 잡히고, 머리·몸통으로 나뉜
            // 소품 리그 안쪽에 카메라를 둔 배치도 허용한다.
            m_cameras[i] = m_installations[i].GetComponentInChildren<Camera>(true);
            if (m_cameras[i] == null)
            {
                Debug.LogWarning($"CCTVSwitcher: CH{i + 1} 설치물에 카메라가 없다", m_installations[i]);
                continue;
            }

            CCTVInfraredLook.SetVolumeLayers(m_cameras[i], m_volumeLayers);
        }
    }

    private void HandleIndexChanged(int previous, int current) => Apply();

    private void HandlePowerChanged(bool previous, bool current) => Apply();

    private void HandleInfraredChanged(bool previous, bool current) => Apply();

    // 입력은 카메라·발광과 무관하므로 Apply를 돌리지 않고 표시만 깨운다.
    private void HandleEntryChanged(int previous, int current) => OnDisplayChanged?.Invoke();

    [Rpc(SendTo.Server)]
    public void RequestSwitchRpc(int delta)
    {
        int count = ChannelCount;
        if (count == 0 || delta == 0)
            return;
        if (!m_isPowered.Value)
            return; // 꺼진 화면에서는 채널 바뀌지 않음
        if (m_externallyJammed)
            return; // 먹통 중엔 채널도 안 바뀐다 — 버튼 CanInteract와 같은 기준

        // C# %는 음수를 그대로 돌려준다 — (0 - 1) % 3 == -1. 두 번 감아 양수로 만든다.
        m_currentIndex.Value = ((m_currentIndex.Value + delta) % count + count) % count;
    }

    /// <summary>키패드 숫자 입력 — 자릿수만큼 쌓인다. 0~9 버튼이 부른다.</summary>
    [Rpc(SendTo.Server)]
    public void RequestAppendDigitRpc(int digit)
    {
        if (digit < 0 || digit > 9)
            return;
        if (!m_isPowered.Value || m_externallyJammed)
            return;

        int next = (m_entry.Value < 0 ? 0 : m_entry.Value) * 10 + digit;
        m_entry.Value = next > k_maxEntry ? digit : next;
    }

    /// <summary>
    /// 입력한 번호로 채널을 옮긴다 — 확인 버튼이 부른다.
    /// 맞든 틀리든 입력을 비운다: 잘못 누른 번호를 지우는 수단도 이것뿐이다.
    /// </summary>
    [Rpc(SendTo.Server)]
    public void RequestConfirmEntryRpc()
    {
        if (!m_isPowered.Value || m_externallyJammed)
            return;

        int index = m_entry.Value - 1; // 표시가 1-based
        m_entry.Value = k_noEntry;

        if (index >= 0 && index < ChannelCount)
            m_currentIndex.Value = index;
    }

    [Rpc(SendTo.Server)]
    public void RequestTogglePowerRpc()
    {
        // 먹통 중 전원 조작 차단은 버튼(CanInteract)만으로는 부족하다 — 그건 조준 피드백용
        // 클라이언트 게이팅이라, RPC를 직접 호출하면 이벤트를 무력화할 수 있다. 서버에서 한 번 더 막는다.
        // m_externallyJammed는 동기화 변수가 아니지만, 서버의 값도 서버 권위 먹통 플래그에서
        // 파생되므로(OnCommsBlackoutChanged는 서버에서도 발행된다) 여기서 읽는 값이 권위값이다.
        if (m_externallyJammed)
            return;

        m_isPowered.Value = !m_isPowered.Value;

        if (!m_isPowered.Value)
            m_entry.Value = k_noEntry; // 꺼진 화면에 입력이 남아 있을 이유가 없다
    }

    [Rpc(SendTo.Server)]
    public void RequestToggleInfraredRpc()
    {
        // CanInteract와 같은 두 조건을 서버에서 다시 가드 (#382와 같은 이유)
        if (!m_isPowered.Value)
            return;
        if (m_externallyJammed)
            return;

        m_isInfrared.Value = !m_isInfrared.Value;
    }

    /// <summary>외부 차단(먹통 등) 설정 — 로컬 시각 상태. 각 피어가 자기 화면을 끈다. (#106 연동)</summary>
    public void SetExternallyJammed(bool value)
    {
        if (m_externallyJammed == value)
            return;
        m_externallyJammed = value;

        // 먹통 중엔 콘솔이 죽으므로 입력도 버린다 — 복구 뒤에 남은 숫자가 되살아나지 않게.
        // m_entry는 서버 권위라 서버에서만 쓴다(이 메서드는 전 피어에서 돈다).
        if (value && IsSpawned && IsServer)
            m_entry.Value = k_noEntry;

        Apply();
    }

    private void Apply()
    {
        bool displaying = IsDisplaying;

        if (m_cameras != null)
        {
            for (int i = 0; i < m_cameras.Length; i++)
            {
                if (m_cameras[i] == null)
                    continue;

                bool active = displaying && i == m_currentIndex.Value;
                m_cameras[i].targetTexture = active ? m_monitorRt : null;
                m_cameras[i].enabled = active;
                if (m_installations != null && i < m_installations.Length && m_installations[i] != null)
                    m_installations[i].SetSelected(active);

                // 무조건 대입 — 채널 넘긴 이전 카메라에 포스트가 남지 않게 한다. (#677)
                CCTVInfraredLook.Apply(m_cameras[i], active && m_isInfrared.Value);
            }
        }

        if (!displaying)
            ClearMonitor();

        CCTVInfraredLook.ApplyMonitorEmission(
            m_monitorRenderer,
            displaying,
            m_isInfrared.Value,
            m_monitorRt,
            m_monitorBacklightEmission,
            m_infraredMonitorEmission
        );

        OnDisplayChanged?.Invoke();
    }

    private void ClearMonitor()
    {
        if (m_monitorRt == null)
            return;

        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = m_monitorRt;
        GL.Clear(true, true, Color.black);
        RenderTexture.active = previous;
    }
}
