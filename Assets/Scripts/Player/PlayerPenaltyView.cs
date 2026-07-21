using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 오검거 매달기 경고 표시 (#101) — 매달기 집행 전 유예 동안, 페널티 대상 플레이어 "본인"에게만
/// 카운트다운을 띄운다. 서버의 <see cref="WrongfulArrestPenalty"/>가 이 컴포넌트를 호출하면
/// [Rpc(SendTo.Owner)]로 대상 오너 클라에만 전달된다 — 다른 플레이어 화면에는 뜨지 않는다.
/// 팀원이 호루라기(#250)로 구제하면 서버가 HideWarning을 보내 즉시 사라진다.
/// [임시] 표시는 OnGUI — PlayerReviveHud·SignalDecoderHud의 임시 HUD 관례를 따른다(정식 UI 후속).
/// </summary>
public class PlayerPenaltyView : NetworkBehaviour
{
    private bool m_showing;
    private float m_deadline; // Time.time 기준 카운트다운 종료 시각
    private GUIStyle m_style;

    /// <summary>서버 전용 — 대상 오너 클라에 카운트다운 경고를 띄운다.</summary>
    public void ShowWarning(float seconds)
    {
        if (IsSpawned)
            ShowWarningRpc(seconds);
        else
            ShowLocal(seconds); // 오프라인 Play 테스트 폴백
    }

    /// <summary>서버 전용 — 유예 취소(호루라기 구제) 시 경고를 지운다.</summary>
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

    private void OnGUI()
    {
        if (!m_showing)
            return;

        float remaining = m_deadline - Time.time;
        if (remaining <= 0f)
        {
            m_showing = false; // 집행 시점 — 이후엔 행동불능 상태가 대신 보인다
            return;
        }

        EnsureStyle();
        const float width = 680f;
        Rect rect = new Rect((Screen.width - width) * 0.5f, Screen.height * 0.28f, width, 64f);
        GUI.Label(
            rect,
            $"오검거 누적 초과! {Mathf.CeilToInt(remaining)}초 후 광장으로 이송됩니다\n동료가 호루라기를 써주면 취소됩니다",
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
