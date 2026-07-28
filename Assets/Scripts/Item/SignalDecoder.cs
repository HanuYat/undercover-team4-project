using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 신호 해석기 — 본부에 설치되는 단말. 타이핑한 짧은 메시지를 팀 전원에게 보낸다. (GDD 8-3/8-4/4-4, #108)
///
/// <b>존재 이유는 먹통 우회다.</b> '전자기기 먹통'(#106) 중에는 음성이 알아듣기 어렵게 망가지는데
/// (<see cref="DeviceBlackoutView"/>가 <see cref="VivoxManager.SetVoiceDistorted"/>를 켠다),
/// 이 단말은 텍스트 경로라 그 영향을 받지 않는다 — 먹통 판정을 <b>일부러 참조하지 않는다</b>.
/// 여기에 먹통 게이트를 추가하면 아이템의 존재 이유가 사라진다.
/// (#372에서 먹통 연출이 '무전 차단'에서 '음성 왜곡'으로 바뀌었지만, 텍스트가 먹통 중 유일하게
/// 또렷한 통신 수단이라는 이 아이템의 역할은 그대로다 — 오히려 왜곡이 심할수록 가치가 올라간다)
///
/// <b>설치형</b>(GDD 8-4) — 들고 다니는 아이템이 아니라 본부에 놓인 고정 단말이다.
/// 기본은 미설치이며, 상점(#182)에서 사면 배달(ShopDelivery)이 서버에서 <see cref="SetInstalled"/>를
/// 호출해 켠다. 미설치 상태에서는 모델도 콜라이더도 꺼져 있어 본부에 아예 없는 것처럼 보인다.
///
/// 서버 권위 — 전송은 오너가 아닌 아무 플레이어나 할 수 있으므로(씬에 놓인 서버 소유 오브젝트)
/// 요청 RPC는 Everyone 권한이며, 서버가 길이·설치 여부를 재검증한 뒤 전 피어에 뿌린다 (#55).
/// </summary>
[RequireComponent(typeof(SignalDecoderHud))]
public class SignalDecoder : NetworkBehaviour, IInteractable
{
    /// <summary>전송 가능한 최대 글자 수. 서버가 이 길이로 자른다 — 클라가 보낸 문자열은 신뢰할 수 없다.</summary>
    public const int k_maxMessageLength = 20;

    [Header("설치 (상점 #182 접합점)")]
    // 정식 흐름에서는 반드시 꺼 둔다 — 켜면 사지 않아도 단말이 작동한다. 상점을 거치지 않는
    // 단독 Play로 단말만 확인할 때만 임시로 켠다.
    [Tooltip("라운드 시작 시의 설치 상태. 정식 흐름에서는 꺼 둘 것 — 구매가 켜 준다")]
    [SerializeField]
    private bool m_installedOnStart;

    // 설치 여부 — 서버만 쓰고 모든 클라가 읽는다. (PlayerIncapacitation.m_isIncapacitatedSynced와 동일 이중 구조)
    private readonly NetworkVariable<bool> m_installedSynced = new NetworkVariable<bool>();
    private bool m_installed; // 서버·오프라인의 진실값 (비네트워크 Play 테스트 폴백)

    private SignalDecoderHud m_hud;

    // 미설치 단말을 숨기는 대상. GameObject 자체는 끄지 않는다 — 끄면 NetworkBehaviour가 멈춰
    // 설치 동기화값을 못 받아 영영 안 켜진다.
    private Renderer[] m_renderers;
    private Collider[] m_colliders;

    /// <summary>설치(구매 완료) 여부. 서버·오프라인은 실참조, 원격 피어는 동기화값으로 판정.</summary>
    public bool IsInstalled => IsSpawned && !IsServer ? m_installedSynced.Value : m_installed;

    private void Awake()
    {
        m_hud = GetComponent<SignalDecoderHud>();
        m_renderers = GetComponentsInChildren<Renderer>(true);
        m_colliders = GetComponentsInChildren<Collider>(true);

        ApplyVisibility(); // 스폰 전 기본값은 미설치 — 씬 로드 직후 한 프레임 노출되는 것을 막는다
    }

    public override void OnNetworkSpawn()
    {
        // 설치 상태 초기값은 서버가 채운다 — 쓰기 권한이 서버뿐이라 클라는 조작할 수 없다.
        if (IsServer)
            SetInstalled(m_installedOnStart);

        // 원격 피어는 스폰 페이로드로 도착한 값을, 이후 구매는 변경 콜백으로 반영한다.
        m_installedSynced.OnValueChanged += HandleInstalledChanged;
        ApplyVisibility();
    }

    public override void OnNetworkDespawn()
    {
        m_installedSynced.OnValueChanged -= HandleInstalledChanged;
    }

    private void HandleInstalledChanged(bool previous, bool current) => ApplyVisibility();

    // 미설치 단말은 보이지도, 부딪히지도 않는다 — 사지 않은 물건이 본부에 서 있으면 혼란스럽고,
    // 렌더러만 끄면 보이지 않는 벽이 남는다(콜라이더가 솔리드라 그대로 몸이 막힌다).
    private void ApplyVisibility()
    {
        bool visible = IsInstalled;

        foreach (Renderer renderer in m_renderers)
        {
            if (renderer != null)
                renderer.enabled = visible;
        }

        foreach (Collider itemCollider in m_colliders)
        {
            if (itemCollider != null)
                itemCollider.enabled = visible;
        }
    }

    /// <summary>
    /// 설치 상태를 바꾼다 — <b>상점(#182)의 접합점</b>. 구매 처리에서 서버가 호출하면 단말이 켜진다.
    /// 서버(또는 오프라인)에서만 유효하다.
    /// </summary>
    public void SetInstalled(bool installed)
    {
        if (IsSpawned && !IsServer)
            return;

        m_installed = installed;
        if (IsSpawned && IsServer)
            m_installedSynced.Value = installed;

        // 서버·오프라인은 자기 화면도 여기서 맞춘다 — 오프라인(!IsSpawned)에는 변경 콜백이 아예 없다.
        ApplyVisibility();
    }

    // ---- 상호작용 (E) ----

    /// <summary>Interact()가 거르는 조건과 동일 — 미설치 단말에는 윤곽선이 뜨지 않는다. (#184)</summary>
    public bool CanInteract(GameObject interactor) => IsInstalled;

    /// <summary>
    /// E 상호작용 — 입력창을 연다. PlayerInteractor는 오너 클라에서만 돌므로(오너 외 비활성)
    /// 이 호출도 상호작용한 본인의 클라이언트에서만 일어난다 = 입력창은 로컬로만 열린다.
    /// </summary>
    public void Interact(GameObject interactor)
    {
        if (!IsInstalled)
        {
            Debug.Log("신호 해석기: 아직 설치되지 않음 (상점에서 구매 필요)");
            return;
        }

        m_hud.Open(this, interactor);
    }

    // ---- 전송 ----

    // 이름에 주의: SendMessage는 UnityEngine.Component의 멤버라 그 이름을 쓰면 CS0108로 숨겨진다.
    // Assets/csc.rsp가 CS0108을 에러로 승격하므로(아키텍처 리팩토링 #245) 반드시 다른 이름을 쓸 것.
    /// <summary>입력창이 확정한 메시지를 팀 전원에게 보낸다. 보낸 사람의 클라이언트에서 호출된다.</summary>
    public void SendSignal(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        // 서버(호스트)·오프라인은 로컬로 즉시 처리
        if (!IsSpawned || IsServer)
        {
            ServerBroadcast(message);
            return;
        }

        RequestBroadcastRpc(message);
    }

    // InvokePermission = Everyone을 명시한다 — 이 단말은 씬에 놓인 서버 소유 오브젝트라
    // 어떤 플레이어도 오너가 아니다. 기본값(오너 전용)이면 아무도 전송할 수 없다.
    // (Scanner.RequestChargeRpc가 본부 충전기 때문에 같은 처리를 한다)
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestBroadcastRpc(string message)
    {
        ServerBroadcast(message);
    }

    // 서버 검증 후 전파 — 클라가 보낸 문자열은 길이도 내용도 신뢰할 수 없다.
    private void ServerBroadcast(string message)
    {
        if (IsSpawned && !IsServer)
            return;

        // 미설치 단말로는 보낼 수 없다 (클라 게이트는 신뢰 불가이므로 재검증)
        if (!IsInstalled)
            return;

        string trimmed = message.Trim();
        if (trimmed.Length == 0)
            return;

        // 신뢰 경계 — 길이를 서버가 자른다. 안 자르면 임의 길이 문자열이 그대로 전 피어에 퍼지고
        // RPC 크기 한도를 넘기면 전송 자체가 깨진다.
        if (trimmed.Length > k_maxMessageLength)
            trimmed = trimmed.Substring(0, k_maxMessageLength);

        if (!IsSpawned)
        {
            ShowLocal(trimmed); // 오프라인 Play 테스트
            return;
        }

        BroadcastRpc(trimmed);
    }

    [Rpc(SendTo.Everyone)]
    private void BroadcastRpc(string message) => ShowLocal(message);

    private void ShowLocal(string message)
    {
        Debug.Log($"[신호 해석기] {message}");
        m_hud.ShowMessage(message);
    }
}
