using TMPro;
using UnityEngine;

public class CCTVChannelLabelView : MonoBehaviour
{
    [SerializeField] private CCTVSwitcher m_switcher;
    [SerializeField] private TMP_Text m_label;

    private void OnEnable()
    {
        if (m_switcher != null) m_switcher.OnDisplayChanged += Refresh;
        Refresh(); // 스폰 전이거나 다시 켜졌을 때 현재 상태로 맞춘다
    }

    private void OnDisable()
    {
        if (m_switcher != null) m_switcher.OnDisplayChanged -= Refresh;
    }

    private void Refresh()
    {
        if (m_label == null) return;

        if (m_switcher == null || !m_switcher.IsSpawned)
        {
            m_label.text = string.Empty; // 아직 네트워크 스폰 전 — 표시할 상태가 없다
            return;
        }

        int channel = m_switcher.CurrentIndex + 1; // 표시용 1-based

        if (m_switcher.ChannelCount == 0)
        {
            m_label.text = "채널 없음";
            return;
        }

        // 먹통을 전원보다 먼저 본다.
        if (m_switcher.IsExternallyJammed)
        {
            m_label.text = "⚠ 신호 없음";
            return;
        }

        if (!m_switcher.IsPowered)
        {
            m_label.text = $"전원 차단 · CH{channel}"; // 다시 켜면 돌아갈 채널을 남겨둔다.
            return;
        }

        // 노드 미배선 카메라에서 "CH3 · " 처럼 구분자만 남는 것을 막는다
        string location = m_switcher.CurrentLocationLabel;
        m_label.text = string.IsNullOrEmpty(location)
            ? $"CH{channel}"
            : $"CH{channel} · {location}";
    }
}
