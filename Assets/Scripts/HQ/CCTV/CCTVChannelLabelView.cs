using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

public class CCTVChannelLabelView : MonoBehaviour
{
    [SerializeField] private CCTVSwitcher m_switcher;
    [SerializeField] private TMP_Text m_label;

    // 상태에 따라 다섯 문구 중 하나를 고르는 자리라 인스펙터에 둘 것이 없다 — 조회는 코드가 한다. (#497)
    // 언어 변경 갱신은 문구마다 구독하는 대신 로케일 변경 한 곳에 걸고 Refresh로 통째로 다시 채운다
    // (ShopStand와 같은 방식). 본부 모니터는 라운드 내내 떠 있어 언어 변경을 볼 수 있는 자리다.
    private const string k_table = "HqTable";

    private void OnEnable()
    {
        if (m_switcher != null) m_switcher.OnDisplayChanged += Refresh;
        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;
        Refresh(); // 스폰 전이거나 다시 켜졌을 때 현재 상태로 맞춘다
    }

    private void OnDisable()
    {
        if (m_switcher != null) m_switcher.OnDisplayChanged -= Refresh;

        // 종료 중에는 설정 에셋을 되살리지 않는다 — HasSettings로 먼저 확인한다 (ShopStand 관례)
        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;
    }

    private void HandleLocaleChanged(Locale locale) => Refresh();

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
            m_label.text = LocalizedStrings.Get(k_table, "Hq.Cctv.NoChannel");
            return;
        }

        // 먹통을 전원보다 먼저 본다.
        if (m_switcher.IsExternallyJammed)
        {
            m_label.text = LocalizedStrings.Get(k_table, "Hq.Cctv.NoSignal");
            return;
        }

        if (!m_switcher.IsPowered)
        {
            // 다시 켜면 돌아갈 채널을 남겨둔다.
            m_label.text = LocalizedStrings.Get(k_table, "Hq.Cctv.PowerOff", channel);
            return;
        }

        // 노드 미배선 카메라에서 "CH3 · " 처럼 구분자만 남는 것을 막는다
        string location = m_switcher.CurrentLocationLabel;
        bool ir = m_switcher.IsInfrared; // (#677)
        m_label.text = string.IsNullOrEmpty(location)
            ? LocalizedStrings.Get(k_table, ir ? "Hq.Cctv.ChannelIr" : "Hq.Cctv.Channel", channel)
            : LocalizedStrings.Get(
                k_table,
                ir ? "Hq.Cctv.ChannelWithLocationIr" : "Hq.Cctv.ChannelWithLocation",
                channel,
                location
            );
    }
}
