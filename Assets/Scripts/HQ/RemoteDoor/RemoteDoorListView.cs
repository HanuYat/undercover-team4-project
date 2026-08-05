using System.Text;
using TMPro;
using UnityEngine;

/// <summary>
/// 본부 모니터의 원격 개방 문 목록 — 문마다 한 줄로 이름과 상태를 찍고 선택된 줄에 표시를 붙인다. (#489)
/// 콘솔 상태를 구독만 하는 표시 전용 (<see cref="CCTVChannelLabelView"/>와 같은 구조, #362).
///
/// 행 프리팹을 쓰지 않는다 — 문 목록은 씬에 고정이라 TMP_Text 하나에 여러 줄로 찍으면 충분하고,
/// 그래야 모니터 메시 위에 월드 스페이스 텍스트로 그대로 올릴 수 있다.
/// </summary>
public class RemoteDoorListView : MonoBehaviour
{
    [SerializeField]
    private RemoteDoorConsole m_console;

    [SerializeField]
    private TMP_Text m_label;

    [Tooltip("한 줄 형식 — {0}=선택 표시, {1}=문 이름, {2}=상태")]
    [SerializeField]
    private string m_rowFormat = "{0} {1} — {2}";

    [Tooltip("선택된 줄 앞에 붙는 표시")]
    [SerializeField]
    private string m_selectedMarker = "▶";

    private readonly StringBuilder m_builder = new StringBuilder();

    private void OnEnable()
    {
        if (m_console != null)
            m_console.OnConsoleChanged += Refresh;
        Refresh(); // 스폰 전이거나 다시 켜졌을 때 현재 상태로 맞춘다
    }

    private void OnDisable()
    {
        if (m_console != null)
            m_console.OnConsoleChanged -= Refresh;
    }

    private void Refresh()
    {
        if (m_label == null)
            return;

        if (m_console == null || !m_console.IsSpawned)
        {
            m_label.text = string.Empty; // 아직 네트워크 스폰 전 — 표시할 상태가 없다
            return;
        }

        int count = m_console.DoorCount;
        if (count == 0)
        {
            m_label.text = "등록된 문 없음";
            return;
        }

        m_builder.Clear();
        for (int i = 0; i < count; i++)
        {
            InteractableDoor door = m_console.GetDoor(i);
            string marker = i == m_console.SelectedIndex ? m_selectedMarker : " ";

            if (i > 0)
                m_builder.AppendLine();

            m_builder.AppendFormat(
                m_rowFormat,
                marker,
                door != null ? door.DoorLabel : "(미배선)",
                DescribeState(door)
            );
        }

        m_label.text = m_builder.ToString();
    }

    private static string DescribeState(InteractableDoor door)
    {
        if (door == null)
            return "-";
        if (door.IsOpen)
            return "열림";

        // 닫혀 있을 때만 잠금이 의미가 있다 — 본부가 열어 둔 문은 잠금과 무관하게 "열림"이다
        return door.IsLocked ? "잠김" : "닫힘";
    }
}
