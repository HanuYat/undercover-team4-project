using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 오검거 페널티의 플레이어 측 표현 (#101/#279) — 두 가지를 담당한다:
///
/// 1) <b>추격 경고</b>: 추격대 출동 시 초기 타겟 "본인"에게만 알림을 잠깐 띄운다(표시 시간 뒤 자동 소멸).
///    서버의 <see cref="WrongfulArrestPenalty"/>가 호출하면 [Rpc(SendTo.Owner)]로 대상 오너 클라에만
///    전달된다 — 다른 플레이어 화면에는 뜨지 않는다. 추격에 시간 제한이 없으므로(팀 결정) 카운트다운이 아니다.
///    표시는 HUD의 공용 토스트(<see cref="ToastView"/>)에 맡긴다 — 이 클래스는 띄우라고 알리기만 한다 (#493).
///
/// 2) <b>끌려가기 중계</b>: 포획 후 호송(#279)에서 서버가 끌기 담당 NPC 2명을 넘기면, 오너 클라가
///    <see cref="PlayerTowedMotion.BeginEscortFollow"/>로 둘 사이를 추종하게 한다 —
///    플레이어 위치는 NetworkTransform 오너 권한이라 서버(NPC)가 직접 끌 수 없기 때문.
/// </summary>
public class PlayerPenaltyView : NetworkBehaviour
{
    private PlayerTowedMotion m_towed; // 끌려가기 추종의 실제 이동 담당 (#279)

    [Header("추격 경고")]
    [Tooltip("추격대 출동 시 띄울 문구 — HudTable/Hud.Penalty.ChaseWarning")]
    [SerializeField]
    private LocalizedString m_chaseWarning;

    // 지금 이 몸을 끌고 있는 캐리어 NPC — 전 피어가 읽는다(밧줄 표시 <c>AbductionRopeView</c>, #901).
    // StartCarried/StopCarried는 [Rpc(SendTo.Owner)]라 오너(끌려가는 본인) 클라에만 닿는데, 밧줄은
    // 동료·관전자 화면에도 그려져야 하므로 여기서 따로 동기화한다 — 쓰기는 서버, 읽기는 Everyone
    // (기본값). 빈손이면 default(NetworkObjectReference) — PlayerHeldItemView.m_equipped과 같은 관례.
    private readonly NetworkVariable<NetworkObjectReference> m_carrierASynced = new();
    private readonly NetworkVariable<NetworkObjectReference> m_carrierBSynced = new();

    // 오프라인(네트워크 미사용) Play 테스트 폴백 — 위 NetworkVariable은 스폰 전엔 못 쓴다.
    private NpcController m_carrierALocal;
    private NpcController m_carrierBLocal;

    /// <summary>지금 이 몸을 끄는 캐리어 NPC 1 — 전 피어에서 유효. 없으면 null. (#901)</summary>
    public NpcController CarrierA =>
        IsSpawned ? ResolveCarrier(m_carrierASynced.Value) : m_carrierALocal;

    /// <summary>지금 이 몸을 끄는 캐리어 NPC 2 — 전 피어에서 유효. 없으면 null. (#901)</summary>
    public NpcController CarrierB =>
        IsSpawned ? ResolveCarrier(m_carrierBSynced.Value) : m_carrierBLocal;

    private static NpcController ResolveCarrier(NetworkObjectReference reference) =>
        reference.TryGet(out NetworkObject obj) ? obj.GetComponent<NpcController>() : null;

    private void Awake()
    {
        m_towed = GetComponent<PlayerTowedMotion>();
    }

    // ---- 추격 경고 (#278) ----

    /// <summary>서버 전용 — 대상 오너 클라에 경고를 띄운다. seconds = 표시 시간(지나면 자동 소멸).</summary>
    public void ShowWarning(float seconds)
    {
        if (IsSpawned)
            ShowWarningRpc(seconds);
        else
            ShowLocal(seconds); // 오프라인 Play 테스트 폴백
    }

    /// <summary>서버 전용 — 포획 확정·타임아웃 등으로 경고를 지운다.</summary>
    public void HideWarning()
    {
        if (IsSpawned)
            HideWarningRpc();
        else
            HideLocal(); // 오프라인 Play 테스트 폴백
    }

    // 서버가 호출하지만 오너 클라에서만 실행된다 — 대상 본인에게만 보여야 하므로 SendTo.Owner.
    [Rpc(SendTo.Owner)]
    private void ShowWarningRpc(float seconds) => ShowLocal(seconds);

    [Rpc(SendTo.Owner)]
    private void HideWarningRpc() => HideLocal();

    // HUD가 없는 환경(데디케이티드 서버 등)에선 App.UI.Toast가 null이라 무동작 — App.UI.Gauge와 같은 방침
    private void ShowLocal(float seconds) => App.UI.Toast?.Show(m_chaseWarning, seconds);

    // 내가 띄운 경고가 아직 떠 있을 때만 지운다 — 그 사이 다른 알림이 덮어썼으면 건드리지 않는다
    private void HideLocal() => App.UI.Toast?.Hide(m_chaseWarning);

    // ---- 납치 결말 관전 (#775) ----

    /// <summary>
    /// 서버 전용 — 관전 오빗 중심을 지정한 지점으로 고정한다. 납치 결말이 몸을 지하로 데려가므로,
    /// 그대로 두면 땅속을 도는 화면이 된다.
    ///
    /// 고정 자체가 <b>관전 진입 신호</b>이기도 하다 — 그래서 사망 확정이 아니라 <b>하강을 시작하기
    /// 전에</b> 부른다(PlayerLook이 이 값을 보고 시점을 지상 3인칭으로 뺀다). 무력화가 풀리면 그쪽이 놓는다.
    /// </summary>
    public void SetSpectatePivot(Vector3 worldPosition, bool cycleToTeammate = false)
    {
        if (IsSpawned)
            SetSpectatePivotRpc(worldPosition, cycleToTeammate);
        else
            ApplySpectatePivot(worldPosition, cycleToTeammate); // 오프라인 Play 테스트 폴백
    }

    [Rpc(SendTo.Owner)]
    private void SetSpectatePivotRpc(Vector3 worldPosition, bool cycleToTeammate) =>
        ApplySpectatePivot(worldPosition, cycleToTeammate);

    private void ApplySpectatePivot(Vector3 worldPosition, bool cycleToTeammate)
    {
        PlayerSpectateCamera spectate = GetComponent<PlayerSpectateCamera>();
        if (spectate == null)
            return;

        spectate.SetPivotOverride(worldPosition);
        if (cycleToTeammate)
            spectate.SpectateTeammateIfAny();
    }

    // ---- 끌려가기 (#279) ----

    /// <summary>
    /// 서버 전용 — 호송 시작: 오너 클라가 끌기 담당 2명 사이를 추종하게 한다.
    /// 끌기가 1명뿐이면 같은 NPC를 두 번 넘긴다(매니저 관례) — 추종 중점이 그 NPC 위치가 된다.
    /// </summary>
    /// <param name="collide">참이면 CharacterController를 켠 채로 추종한다 — 벽 스윕·미끄러짐을
    /// CC가 풀게 한다(납치 지상 호송 전용, #902). 거짓이면 기존처럼 transform을 직접 옮긴다
    /// (오검거 호송·맨홀 하강 — 하강은 CC가 켜져 있으면 지면을 통과하지 못한다).</param>
    public void StartCarried(NpcController carrierA, NpcController carrierB, bool collide = false)
    {
        if (carrierA == null || carrierB == null)
            return;

        if (IsSpawned)
        {
            m_carrierASynced.Value = carrierA.NetworkObject;
            m_carrierBSynced.Value = carrierB.NetworkObject;
            StartCarriedRpc(carrierA.NetworkObject, carrierB.NetworkObject, 0f, collide);
        }
        else if (m_towed != null)
        {
            m_carrierALocal = carrierA;
            m_carrierBLocal = carrierB;
            m_towed.BeginEscortFollow(carrierA.transform, carrierB.transform, 0f, collide); // 오프라인 폴백
        }
    }

    /// <summary>
    /// 서버 전용 — 앵커 <b>하나</b>를 정해진 속도로 따라가게 한다 (#819 UFO 흡입).
    ///
    /// 추종 자체는 호송과 같은 경로다: 같은 앵커를 두 번 넘겨 중점이 그 앵커 위치가 되게 한다
    /// (끌기가 1명뿐일 때의 관례와 같다). 다른 것은 속도 상한 하나뿐이고, 그것이 떠오르는 속도다.
    /// 푸는 것은 <see cref="StopCarried"/>로 같다 — 추종은 한 종류이므로 끄는 문도 하나다.
    /// </summary>
    public void StartTowedBy(NetworkObject anchor, float maxSpeed)
    {
        if (anchor == null)
            return;

        // 앵커가 NPC가 아니라 UFO 기체다 — 밧줄 캐리어로 착각하지 않게 비워 둔다(이전 납치의 값이
        // 남아 있을 수 있다). AbductionRopeView는 이 값이 비어 있으면 그리지 않는다 (#901).
        if (IsSpawned)
        {
            m_carrierASynced.Value = default;
            m_carrierBSynced.Value = default;
            StartCarriedRpc(anchor, anchor, maxSpeed, false);
        }
        else if (m_towed != null)
        {
            m_carrierALocal = null;
            m_carrierBLocal = null;
            m_towed.BeginEscortFollow(anchor.transform, anchor.transform, maxSpeed, false); // 오프라인 폴백
        }
    }

    /// <summary>서버 전용 — 호송 종료(광장 도착·중단): 추종을 풀어 준다. 직후 서버가 광장 스냅 텔레포트로 보정한다.</summary>
    public void StopCarried()
    {
        if (IsSpawned)
        {
            m_carrierASynced.Value = default;
            m_carrierBSynced.Value = default;
            StopCarriedRpc();
        }
        else if (m_towed != null)
        {
            m_carrierALocal = null;
            m_carrierBLocal = null;
            m_towed.EndEscortFollow();
        }
    }

    // 오너 클라에서만 실행 — NetworkTransform 오너 권한이라 위치 추종은 오너가 해야 전 피어에 전파된다 (#279).
    [Rpc(SendTo.Owner)]
    private void StartCarriedRpc(
        NetworkObjectReference carrierA,
        NetworkObjectReference carrierB,
        float maxSpeed,
        bool collide
    )
    {
        if (m_towed == null)
            return;
        if (!carrierA.TryGet(out NetworkObject a) || !carrierB.TryGet(out NetworkObject b))
            return; // 담당 NPC가 이미 디스폰됨 — 추종 없이 서버의 스냅 텔레포트(HangAsync)에 맡긴다

        m_towed.BeginEscortFollow(a.transform, b.transform, maxSpeed, collide);
    }

    [Rpc(SendTo.Owner)]
    private void StopCarriedRpc()
    {
        if (m_towed != null)
            m_towed.EndEscortFollow();
    }
}
