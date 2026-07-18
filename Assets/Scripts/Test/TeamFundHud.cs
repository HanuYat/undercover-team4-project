using UnityEngine;

/// <summary>
/// [임시] 팀 공용 자금 온스크린 표시. (#104)
/// 화면 좌상단에 현재 팀 자금을 그려 플레이·멀티플레이 테스트 중 잔액을 눈으로 확인하게 한다.
/// 자금은 NetworkVariable로 전 피어에 동기화되므로(TeamFund) 호스트·클라이언트 모두 같은 값을 본다.
/// PlayerReviveHud 등 임시 OnGUI 관례를 따른다 — 정식 자금 표시는 정산 UI(#107)·상점 UI(#182)로 대체 예정.
/// </summary>
[RequireComponent(typeof(TeamFund))]
public class TeamFundHud : MonoBehaviour
{
    private TeamFund m_teamFund;

    private void Awake()
    {
        m_teamFund = GetComponent<TeamFund>();
    }

    private void OnGUI()
    {
        // 세션 시작(Host/Client) 전에는 씬 NetworkObject가 스폰되지 않아 자금이 무의미하다 — 스폰 후에만 표시.
        if (m_teamFund == null || !m_teamFund.IsSpawned)
            return;

        const float width = 200f;
        const float height = 32f;
        Rect rect = new Rect(12f, (Screen.height - height) * 0.5f, width, height);

        Color prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.5f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = prev;

        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleLeft,
            fontSize = 16,
            fontStyle = FontStyle.Bold,
            padding = new RectOffset(10, 10, 0, 0)
        };
        style.normal.textColor = Color.white;
        GUI.Label(rect, $"팀 자금: {m_teamFund.Balance:N0}원", style);
    }
}
