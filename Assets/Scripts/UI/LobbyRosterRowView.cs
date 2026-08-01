using TMPro;
using UnityEngine;

/// <summary>
/// 로비 접속자 목록의 한 행 (#429) — 닉네임 / 방장 아이콘 / 발화 아이콘 / 음소거 아이콘(#430).
/// 발화 상태는 동기화하지 않는다 — 각 클라의 Vivox가 자기에게 들리는 참가자만 알려주므로
/// 행이 자기 PlayerId를 기억해 두고 패널이 로컬로 갱신한다 (PlayerNameTag와 동일 방침).
/// </summary>
public class LobbyRosterRowView : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI m_nicknameText;
    [SerializeField] private GameObject m_hostIcon; // 방장 표시
    [SerializeField] private GameObject m_speakerIcon; // 발화 표시
    [SerializeField] private GameObject m_micMutedIcon; // 음소거 표시 (#430)

    // 발화 아이콘은 음소거와 배타다 — 소리가 나가지 않는 사람에게 발화 표시가 켜지면 안 된다.
    // 두 신호가 서로 다른 경로로 도착하므로(음소거는 로스터 동기화, 발화는 로컬 Vivox 이벤트)
    // 마지막 값을 기억해 두고 둘 중 어느 쪽이 바뀌어도 같은 판정을 다시 내린다.
    private bool m_speaking;
    private bool m_micMuted;

    /// <summary>이 행이 표시 중인 UGS PlayerId — 발화 상태 매칭 키. 빈 슬롯이면 빈 문자열.</summary>
    public string PlayerId { get; private set; } = string.Empty;

    public void Bind(LobbyPlayerEntry entry, bool isHost)
    {
        PlayerId = entry.PlayerId.ToString();
        // 닉네임 보고가 아직 안 닿은 찰나에 빈 라벨이 보이지 않게 한다 (PlayerNameTag와 같은 취지)
        m_nicknameText.text = entry.Nickname.IsEmpty ? "접속 중..." : entry.Nickname.ToString();
        SetHost(isHost);
        SetMicMuted(entry.MicMuted);
        SetSpeaking(false); // 실제 상태는 패널이 Vivox에 물어 곧바로 다시 세운다
    }

    /// <summary>정원까지 남은 자리 — 몇 명 더 들어올 수 있는지 보이게 한다.</summary>
    public void BindEmpty()
    {
        PlayerId = string.Empty;
        m_nicknameText.text = "대기 중...";
        SetHost(false);
        SetMicMuted(false);
        SetSpeaking(false);
    }

    public void SetSpeaking(bool on)
    {
        m_speaking = on;

        if (m_speakerIcon != null)
            m_speakerIcon.SetActive(on && !m_micMuted);
    }

    public void SetMicMuted(bool on)
    {
        m_micMuted = on;

        if (m_micMutedIcon != null)
            m_micMutedIcon.SetActive(on);

        SetSpeaking(m_speaking); // 음소거로 바뀌었으면 켜져 있던 발화 아이콘을 내린다
    }

    private void SetHost(bool on)
    {
        if (m_hostIcon != null)
            m_hostIcon.SetActive(on);
    }
}
