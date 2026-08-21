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
    [SerializeField]
    Camera[] m_cameras;

    [SerializeField]
    RenderTexture m_monitorRt;

    // IR 볼륨 레이어 — 캐싱 시 한 번만 배선, 런타임엔 건드리지 않는다. (#677)
    [SerializeField]
    LayerMask m_volumeLayers = ~0;
    private CCTVNode[] m_nodes;

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

    // 외부 차단(먹통 이벤트) — 이미 동기화된 플래그에서 각 피어가 로컬로 유도하므로 동기화하지 않는다.
    private bool m_externallyJammed;

    // 구독 해제를 위해 들고 있는 먹통 이벤트 — 없는 구성에서는 null이다. (#382)
    private DeviceBlackoutEvent m_blackout;

    /// <summary>현재 채널의 설치 위치 이름 — 송출 중이 아니면 빈 문자열. (#362 라벨용)</summary>
    public string CurrentLocationLabel =>
        IsDisplaying
        && m_nodes != null
        && CurrentIndex >= 0
        && CurrentIndex < m_nodes.Length
        && m_nodes[CurrentIndex] != null
            ? m_nodes[CurrentIndex].LocationLabel
            : string.Empty;

    public int ChannelCount => m_cameras != null ? m_cameras.Length : 0;
    public int CurrentIndex => m_currentIndex.Value;
    public bool IsPowered => m_isPowered.Value;
    public bool IsExternallyJammed => m_externallyJammed;
    public bool IsInfrared => m_isInfrared.Value;

    /// <summary>화면이 실제로 송출 중인가 — 전원·외부 차단·유효 채널을 모두 만족해야 한다.</summary>
    public bool IsDisplaying => m_isPowered.Value && !m_externallyJammed && ChannelCount > 0;

    /// <summary>표시 상태 변화 — 채널 라벨·미니맵 하이라이트가 구독한다. (#362)</summary>
    public event Action OnDisplayChanged;

    public override void OnNetworkSpawn()
    {
        CacheNodes();
        m_currentIndex.OnValueChanged += HandleIndexChanged;
        m_isPowered.OnValueChanged += HandlePowerChanged;
        m_isInfrared.OnValueChanged += HandleInfraredChanged;
        Apply();
    }

    public override void OnNetworkDespawn()
    {
        m_currentIndex.OnValueChanged -= HandleIndexChanged;
        m_isPowered.OnValueChanged -= HandlePowerChanged;
        m_isInfrared.OnValueChanged -= HandleInfraredChanged;
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
        int count = ChannelCount;
        m_nodes = new CCTVNode[count];
        for (int i = 0; i < count; i++)
        {
            if (m_cameras[i] == null)
                continue;
            // InParent — 노드가 카메라와 같은 오브젝트에 있어도(현재 배치) 잡히고,
            // 카메라를 자식으로 둔 CCTV 소품 리그에 노드를 붙이는 배치도 허용한다.
            m_nodes[i] = m_cameras[i].GetComponentInParent<CCTVNode>();
            CCTVInfraredLook.SetVolumeLayers(m_cameras[i], m_volumeLayers);
        }
    }

    private void HandleIndexChanged(int previous, int current) => Apply();

    private void HandlePowerChanged(bool previous, bool current) => Apply();

    private void HandleInfraredChanged(bool previous, bool current) => Apply();

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
                if (m_nodes != null && i < m_nodes.Length && m_nodes[i] != null)
                    m_nodes[i].SetSelected(active);

                // 무조건 대입 — 채널 넘긴 이전 카메라에 포스트가 남지 않게 한다. (#677)
                CCTVInfraredLook.Apply(m_cameras[i], active && m_isInfrared.Value);
            }
        }

        if (!displaying)
            ClearMonitor();

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
