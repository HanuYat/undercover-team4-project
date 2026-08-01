using TMPro;
using UnityEngine;

/// <summary>
/// 내 마이크 상태 HUD (#430) — 음소거 아이콘 + 음소거 중 무전 키를 눌렀을 때의 안내.
///
/// <b>타인의 음소거는 그리지 않는다.</b> 전원 표시는 로비 로스터(#429)가 맡고, 게임 중에는 표시
/// 경로를 두지 않기로 결정했다 — PlayerNameTag에 음소거 NetworkVariable을 하나 더 두면 같은
/// 상태를 나르는 경로가 둘이 된다. 게임 중 필요해지면 그때 경로를 합쳐서 별건으로.
///
/// 상태는 GameSettings.MicMuted 하나에서만 읽는다 — HUD가 자기 사본을 들지 않는다.
/// </summary>
public class MicStatusHud : MonoBehaviour
{
    [SerializeField] private GameObject m_mutedIcon; // 내 음소거 표시

    [Header("음소거 중 무전 시도 안내")]
    [SerializeField] private GameObject m_hintRoot;
    [SerializeField] private TextMeshProUGUI m_hintText;
    [SerializeField] private string m_hintMessage = "마이크가 꺼져 있습니다";

    [Tooltip("안내가 화면에 남는 시간(초)")]
    [SerializeField] private float m_hintSeconds = 2f;

    private float m_hintHideTime;

    private VivoxManager Vivox => App.Net.Vivox; // 매니저는 필드에 캐싱하지 않는다 (R8)

    private void OnEnable()
    {
        GameSettings.OnMicMutedChanged += HandleMicMutedChanged;

        if (Vivox != null)
            Vivox.OnMutedTalkAttempt += ShowHint;

        // 구독 전에 이미 정해져 있던 값을 한 번 반영한다 — HUD는 오너 스폰 시 런타임 생성되므로
        // 마이크를 꺼둔 채로 게임에 들어오면 이 1회 반영이 없으면 아이콘이 꺼진 채 시작한다.
        HandleMicMutedChanged(GameSettings.MicMuted);
        HideHint();
    }

    private void OnDisable()
    {
        GameSettings.OnMicMutedChanged -= HandleMicMutedChanged;

        if (Vivox != null)
            Vivox.OnMutedTalkAttempt -= ShowHint;
    }

    private void HandleMicMutedChanged(bool muted)
    {
        if (m_mutedIcon != null)
            m_mutedIcon.SetActive(muted);

        // 마이크를 다시 켰으면 안내를 붙잡아 둘 이유가 없다
        if (!muted)
            HideHint();
    }

    private void ShowHint()
    {
        if (m_hintText != null)
            m_hintText.text = m_hintMessage;

        if (m_hintRoot != null)
            m_hintRoot.SetActive(true);

        // 일시정지(timeScale=0) 중에 눌러도 사라져야 하므로 unscaled 시간을 쓴다
        m_hintHideTime = Time.unscaledTime + m_hintSeconds;
    }

    private void HideHint()
    {
        if (m_hintRoot != null)
            m_hintRoot.SetActive(false);
    }

    private void Update()
    {
        if (m_hintRoot == null || !m_hintRoot.activeSelf) return;
        if (Time.unscaledTime < m_hintHideTime) return;

        HideHint();
    }
}
