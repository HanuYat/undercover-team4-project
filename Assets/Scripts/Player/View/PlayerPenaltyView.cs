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
    /// 서버 전용 — 관전 오빗 중심을 지정한 지점으로 고정한다. 납치 결말이 시체를 지하로 데려가므로,
    /// 그대로 두면 땅속을 도는 화면이 된다. 사망을 확정하기 <b>전에</b> 불러야 첫 프레임부터 맞는다.
    /// </summary>
    public void SetSpectatePivot(Vector3 worldPosition)
    {
        if (IsSpawned)
            SetSpectatePivotRpc(worldPosition);
        else
            ApplySpectatePivot(worldPosition); // 오프라인 Play 테스트 폴백
    }

    [Rpc(SendTo.Owner)]
    private void SetSpectatePivotRpc(Vector3 worldPosition) => ApplySpectatePivot(worldPosition);

    private void ApplySpectatePivot(Vector3 worldPosition)
    {
        PlayerSpectateCamera spectate = GetComponent<PlayerSpectateCamera>();
        if (spectate != null)
            spectate.SetPivotOverride(worldPosition);
    }

    // ---- 끌려가기 (#279) ----

    /// <summary>
    /// 서버 전용 — 호송 시작: 오너 클라가 끌기 담당 2명 사이를 추종하게 한다.
    /// 끌기가 1명뿐이면 같은 NPC를 두 번 넘긴다(매니저 관례) — 추종 중점이 그 NPC 위치가 된다.
    /// </summary>
    public void StartCarried(NpcController carrierA, NpcController carrierB)
    {
        if (carrierA == null || carrierB == null)
            return;

        if (IsSpawned)
            StartCarriedRpc(carrierA.NetworkObject, carrierB.NetworkObject);
        else if (m_towed != null)
            m_towed.BeginEscortFollow(carrierA.transform, carrierB.transform); // 오프라인 폴백
    }

    /// <summary>서버 전용 — 호송 종료(광장 도착·중단): 추종을 풀어 준다. 직후 서버가 광장 스냅 텔레포트로 보정한다.</summary>
    public void StopCarried()
    {
        if (IsSpawned)
            StopCarriedRpc();
        else if (m_towed != null)
            m_towed.EndEscortFollow();
    }

    // 오너 클라에서만 실행 — NetworkTransform 오너 권한이라 위치 추종은 오너가 해야 전 피어에 전파된다 (#279).
    [Rpc(SendTo.Owner)]
    private void StartCarriedRpc(NetworkObjectReference carrierA, NetworkObjectReference carrierB)
    {
        if (m_towed == null)
            return;
        if (!carrierA.TryGet(out NetworkObject a) || !carrierB.TryGet(out NetworkObject b))
            return; // 담당 NPC가 이미 디스폰됨 — 추종 없이 서버의 스냅 텔레포트(HangAsync)에 맡긴다

        m_towed.BeginEscortFollow(a.transform, b.transform);
    }

    [Rpc(SendTo.Owner)]
    private void StopCarriedRpc()
    {
        if (m_towed != null)
            m_towed.EndEscortFollow();
    }

}
