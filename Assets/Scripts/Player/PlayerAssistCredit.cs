using UnityEngine;

/// <summary>
/// 팀원 구조 집계 — 다운 리바이브(PlayerReviver) + 부활 키트(ReviveKit)로 남을 일으킨 횟수.
/// 정산 "최다 팀원 구조" 칭호 집계용 (#739). PlayerKillCredit과 같은 이유로 매니저를 두지 않는다 —
/// 상태가 플레이어 하나당 하나면 그 플레이어 오브젝트가 제자리다.
/// </summary>
public class PlayerAssistCredit : MonoBehaviour
{
    private int m_rescueCount;

    /// <summary>이번 라운드 구조 성공 횟수 — 서버 전용 참조. (#739)</summary>
    public int RescueCount => m_rescueCount;

    /// <summary>구조 집계 — 리바이브·부활 키트 성공 지점(서버)에서 부른다. 서버(또는 오프라인) 전용.</summary>
    public void ServerCreditRescue()
    {
        m_rescueCount++;
    }

    /// <summary>라운드 사이 초기화. 서버(또는 오프라인) 전용 — 상점 진입 지점(ShopManager)에서 부른다.</summary>
    public void ServerResetRound()
    {
        m_rescueCount = 0;
    }
}
