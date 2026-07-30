using TMPro;
using UnityEngine;

/// <summary>
/// 로비 접속자 목록의 한 행 (#429) — 닉네임 / 방장 아이콘 / 발화 아이콘.
/// 발화 상태는 동기화하지 않는다 — 각 클라의 Vivox가 자기에게 들리는 참가자만 알려주므로
/// 행이 자기 PlayerId를 기억해 두고 패널이 로컬로 갱신한다 (PlayerNameTag와 동일 방침).
/// </summary>
public class LobbyRosterRowView : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI m_nicknameText;
    [SerializeField] private GameObject m_hostIcon; // 방장 표시
    [SerializeField] private GameObject m_speakerIcon; // 발화 표시

    /// <summary>이 행이 표시 중인 UGS PlayerId — 발화 상태 매칭 키. 빈 슬롯이면 빈 문자열.</summary>
    public string PlayerId { get; private set; } = string.Empty;

    public void Bind(LobbyPlayerEntry entry, bool isHost)
    {
        PlayerId = entry.PlayerId.ToString();
        // 닉네임 보고가 아직 안 닿은 찰나에 빈 라벨이 보이지 않게 한다 (PlayerNameTag와 같은 취지)
        m_nicknameText.text = entry.Nickname.IsEmpty ? "접속 중..." : entry.Nickname.ToString();
        SetHost(isHost);
        SetSpeaking(false); // 실제 상태는 패널이 Vivox에 물어 곧바로 다시 세운다
    }

    /// <summary>정원까지 남은 자리 — 몇 명 더 들어올 수 있는지 보이게 한다.</summary>
    public void BindEmpty()
    {
        PlayerId = string.Empty;
        m_nicknameText.text = "대기 중...";
        SetHost(false);
        SetSpeaking(false);
    }

    public void SetSpeaking(bool on)
    {
        if (m_speakerIcon != null)
            m_speakerIcon.SetActive(on);
    }

    private void SetHost(bool on)
    {
        if (m_hostIcon != null)
            m_hostIcon.SetActive(on);
    }
}
