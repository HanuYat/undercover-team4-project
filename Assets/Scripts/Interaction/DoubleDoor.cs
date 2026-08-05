using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 양쪽으로 열리는 대문 — 플레이어가 상호작용키(E)로 여닫는다. 문짝 두 짝이 각자 반대로 회전한다.
///
/// <b>닫힌 각도는 씬에 저장된 문짝의 회전값이다.</b> Awake에서 잡아 두고, 거기에
/// <see cref="m_openAngleLeft"/>/<see cref="m_openAngleRight"/>를 더한 곳이 열린 각도가 된다.
/// 그래서 문짝은 <b>닫힌 상태로 저장해 둬야 한다</b> — 열린 자세로 저장하면 그게 닫힌 각도로 잡힌다.
///
/// 열림 각은 ±180도 미만으로 둘 것. <see cref="Quaternion.RotateTowards"/>가 짧은 쪽 호를 타므로
/// 200도처럼 넘겨 적으면 반대 방향(-160도)으로 돌아간다. 같은 자세라도 도는 방향이 뒤집힌다.
///
/// <b>여닫는 것은 E 토글뿐이다.</b> 근접 자동 개폐는 없다 — 상태가 개폐 하나뿐이라
/// 자물쇠·탈옥까지 함께 보는 유치장 문(<see cref="JailDoor"/>)보다 단순하다.
///
/// 씬 배치:
///  · <b>문짝 콜라이더를 Interactable 레이어에 둘 것</b> — PlayerInteractor의 조준 마스크가 그 레이어만
///    본다. 레이어는 조준 마스크만 가르고 충돌은 끊지 않으므로, 조준도 되고 닫힌 문이 플레이어를
///    막는 것도 그대로 된다 (CellGate와 같은 관례).
///  · <b>문짝은 NavMesh 베이크에서 제외할 것</b>(NavMeshModifier의 Ignore From Build) — 닫힌 문짝이
///    베이크에 잡히면 문턱의 NavMesh가 끊겨 안팎 경로가 사라진다.
///  · 문턱(문틀 하단) 콜라이더가 CharacterController의 stepOffset보다 높으면 열어도 못 지나간다.
///    Synty 벙커 문은 0.32m라 stepOffset 0.3을 넘어서 껐다.
///
/// 서버 권위 — 개폐 상태는 서버가 정해 NetworkVariable로 전 피어에 동기화하고(#56),
/// 회전 연출은 각 피어가 로컬로 보간한다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class DoubleDoor : NetworkBehaviour, IInteractable
{
    [Header("문짝 (닫힌 자세로 저장해 둘 것)")]
    [SerializeField] private Transform m_leafLeft;
    [SerializeField] private Transform m_leafRight;

    [Header("열림 각도 (닫힌 각도 기준 상대값, Y축)")]
    [Tooltip("두 짝이 반대로 열리므로 부호가 서로 반대여야 한다. ±180도 미만으로 둘 것")]
    [SerializeField] private float m_openAngleLeft = 160f;
    [SerializeField] private float m_openAngleRight = -160f;

    [Tooltip("완전히 열리거나 닫히는 데 걸리는 시간(초)")]
    [SerializeField] private float m_swingSeconds = 0.7f;

    // 서버 권위 개폐 상태 — JailDoor와 동일한 이중 구조(오프라인 폴백 로컬 값)
    private readonly NetworkVariable<bool> m_isOpenSynced = new NetworkVariable<bool>(false);
    private bool m_localIsOpen;

    // 닫힌 자세 — Awake에 잡아 두고 여기에 열림 각도를 더한 곳이 열린 자세가 된다
    private Quaternion m_closedLeft;
    private Quaternion m_closedRight;

    /// <summary>문이 열려 있는가. 세션 중에는 동기화된 값이라 클라이언트에서도 읽을 수 있다.</summary>
    public bool IsOpen => IsSpawned ? m_isOpenSynced.Value : m_localIsOpen;

    /// <summary>개폐 전환 — 소리·연출이 구독할 훅. 전 피어에서 발생한다.</summary>
    public event System.Action<bool> OnOpenChanged;

    private void Awake()
    {
        if (m_leafLeft != null)
            m_closedLeft = m_leafLeft.localRotation;

        if (m_leafRight != null)
            m_closedRight = m_leafRight.localRotation;
    }

    public override void OnNetworkSpawn() => m_isOpenSynced.OnValueChanged += HandleOpenSyncedChanged;

    public override void OnNetworkDespawn() => m_isOpenSynced.OnValueChanged -= HandleOpenSyncedChanged;

    private void HandleOpenSyncedChanged(bool previous, bool current) => OnOpenChanged?.Invoke(current);

    // ---- 플레이어 상호작용 (E 토글) ----

    /// <summary>문짝이 하나라도 연결돼 있으면 여닫을 수 있다. (사거리·가시선은 PlayerInteractor가 걸러 준다)</summary>
    public bool CanInteract(GameObject interactor) => m_leafLeft != null || m_leafRight != null;

    /// <summary>E — 여닫기 토글.</summary>
    public void Interact(GameObject interactor)
    {
        if (!IsSpawned)
        {
            ServerToggle(); // 오프라인 단독 테스트
            return;
        }

        RequestToggleRpc();
    }

    // 클라 입력을 서버로 넘긴다 — 소유권을 요구하지 않는다(씬 오브젝트이고 누구나 여닫는다).
    // 개폐 권위는 서버에 있으므로 여기서 상태를 직접 건드리지 않는다 (JailDoor와 같은 관례, #362).
    [Rpc(SendTo.Server)]
    private void RequestToggleRpc() => ServerToggle();

    private void ServerToggle()
    {
        if (IsSpawned && !IsServer)
            return;

        ServerSetOpen(!IsOpen);
    }

    /// <summary>개폐 설정 — 서버(또는 오프라인) 전용. 외부 강제(연출·치트)도 이 지점을 지난다.</summary>
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
    }

    // ---- 연출 (전 피어 로컬) ----

    private void Update()
    {
        Swing(m_leafLeft, m_closedLeft, m_openAngleLeft);
        Swing(m_leafRight, m_closedRight, m_openAngleRight);
    }

    private void Swing(Transform leaf, Quaternion closed, float openAngle)
    {
        if (leaf == null)
            return;

        Quaternion target = IsOpen ? closed * Quaternion.Euler(0f, openAngle, 0f) : closed;
        if (leaf.localRotation == target)
            return;

        // 속도는 "완주 시간"에서 역산한다 — 각도를 인스펙터에서 바꿔도 체감 속도가 유지된다
        float speed = m_swingSeconds > 0f ? Mathf.Abs(openAngle) / m_swingSeconds : float.MaxValue;
        leaf.localRotation = Quaternion.RotateTowards(leaf.localRotation, target, speed * Time.deltaTime);
    }
}
