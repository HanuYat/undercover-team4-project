using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 오검거 페널티의 플레이어 측 표현 (#101/#279) — 두 가지를 담당한다:
///
/// 1) <b>추격 경고</b>: 추격대 출동 시 초기 타겟 "본인"에게만 카운트다운을 띄운다.
///    서버의 <see cref="WrongfulArrestPenalty"/>가 호출하면 [Rpc(SendTo.Owner)]로 대상 오너 클라에만
///    전달된다 — 다른 플레이어 화면에는 뜨지 않는다. 카운트다운은 폴백 강제 이송까지 남은 시간.
///    [임시] 표시는 OnGUI — PlayerReviveHud·SignalDecoderHud의 임시 HUD 관례를 따른다(정식 UI 후속).
///
/// 2) <b>끌려가기 중계</b>: 포획 후 호송(#279)에서 서버가 끌기 담당 NPC 2명을 넘기면, 오너 클라가
///    <see cref="PlayerMovement.BeginCarriedFollow"/>로 둘 사이를 추종하게 한다 —
///    플레이어 위치는 NetworkTransform 오너 권한이라 서버(NPC)가 직접 끌 수 없기 때문.
/// </summary>
public class PlayerPenaltyView : NetworkBehaviour
{
    private PlayerMovement m_movement; // 끌려가기 추종의 실제 이동 담당 (#279)

    private bool m_showing;
    private float m_deadline; // Time.time 기준 카운트다운 종료 시각
    private GUIStyle m_style;

    private void Awake()
    {
        m_movement = GetComponent<PlayerMovement>();
    }

    // ---- 추격 경고 (#278) ----

    /// <summary>서버 전용 — 대상 오너 클라에 카운트다운 경고를 띄운다. seconds = 폴백 강제 이송까지 남은 시간.</summary>
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
            m_showing = false;
    }

    // 서버가 호출하지만 오너 클라에서만 실행된다 — 대상 본인에게만 보여야 하므로 SendTo.Owner.
    [Rpc(SendTo.Owner)]
    private void ShowWarningRpc(float seconds) => ShowLocal(seconds);

    [Rpc(SendTo.Owner)]
    private void HideWarningRpc() => m_showing = false;

    private void ShowLocal(float seconds)
    {
        m_showing = true;
        m_deadline = Time.time + seconds;
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
        else if (m_movement != null)
            m_movement.BeginCarriedFollow(carrierA.transform, carrierB.transform); // 오프라인 폴백
    }

    /// <summary>서버 전용 — 호송 종료(광장 도착·중단): 추종을 풀어 준다. 직후 서버가 광장 스냅 텔레포트로 보정한다.</summary>
    public void StopCarried()
    {
        if (IsSpawned)
            StopCarriedRpc();
        else if (m_movement != null)
            m_movement.EndCarriedFollow();
    }

    // 오너 클라에서만 실행 — NetworkTransform 오너 권한이라 위치 추종은 오너가 해야 전 피어에 전파된다 (#279).
    [Rpc(SendTo.Owner)]
    private void StartCarriedRpc(NetworkObjectReference carrierA, NetworkObjectReference carrierB)
    {
        if (m_movement == null)
            return;
        if (!carrierA.TryGet(out NetworkObject a) || !carrierB.TryGet(out NetworkObject b))
            return; // 담당 NPC가 이미 디스폰됨 — 추종 없이 서버의 스냅 텔레포트(HangAsync)에 맡긴다

        m_movement.BeginCarriedFollow(a.transform, b.transform);
    }

    [Rpc(SendTo.Owner)]
    private void StopCarriedRpc()
    {
        if (m_movement != null)
            m_movement.EndCarriedFollow();
    }

    // ---- 임시 OnGUI 표시 ----

    private void OnGUI()
    {
        if (!m_showing)
            return;

        float remaining = m_deadline - Time.time;
        if (remaining <= 0f)
        {
            m_showing = false; // 폴백 집행 시점 — 이후엔 행동불능 상태가 대신 보인다
            return;
        }

        EnsureStyle();
        const float width = 680f;
        Rect rect = new Rect((Screen.width - width) * 0.5f, Screen.height * 0.28f, width, 64f);
        GUI.Label(
            rect,
            $"오검거 누적 초과! 성난 시민들이 당신을 노립니다 — 잡히면 광장으로 끌려갑니다\n({Mathf.CeilToInt(remaining)}초 안에 안 잡혀도 강제 이송)",
            m_style);
    }

    private void EnsureStyle()
    {
        if (m_style != null)
            return;

        m_style = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 20,
            fontStyle = FontStyle.Bold,
            wordWrap = true
        };
        m_style.normal.textColor = new Color(1f, 0.5f, 0.4f);
    }
}
