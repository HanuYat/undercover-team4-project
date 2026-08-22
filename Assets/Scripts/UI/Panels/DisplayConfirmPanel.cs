using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.UI;

/// <summary>
/// 창 모드·해상도 적용 확인창 (#796) — [유지]를 누르지 않으면 카운트다운이 끝날 때 이전 값으로 되돌린다.
/// 잘못 고른 화면은 아무것도 눌러 볼 수 없는 상태가 되므로, 되돌림을 사람이 아니라 시간이 맡는다.
/// 설계 결정 (e) — docs/design/settings-ui.md
///
/// 저장은 [유지]에서만 일어난다(<see cref="GameSettings.KeepDisplay"/>) — 되돌림·ESC·창 파괴는
/// 화면만 되돌리고 저장값은 건드리지 않는다.
/// </summary>
public class DisplayConfirmPanel : PanelBase
{
    // 흔한 관례대로 15초. 화면이 깨졌는지 확인하기에 충분하고, 참고 기다리기엔 길다.
    private const float k_revertSeconds = 15f;

    [Header("문구")]
    [SerializeField]
    private TMP_Text m_countdownText;

    [Tooltip("남은 초를 채울 서식 — Settings.DisplayConfirm.Countdown ({0} = 남은 초)")]
    [SerializeField]
    private LocalizedString m_countdownFormat;

    [Header("버튼")]
    [SerializeField]
    private Button m_keepButton; // 유지

    [SerializeField]
    private Button m_revertButton; // 되돌리기

    private EWindowMode m_previousMode;
    private Vector2Int m_previousResolution;
    private Action m_onReverted;

    // 아직 유지/되돌림이 갈리지 않았다 — ESC나 씬 전환으로 닫혀도 되돌려야 하는지의 기준.
    private bool m_pending;

    private CancellationTokenSource m_countdownCts;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    protected override void Awake()
    {
        base.Awake();
        if (m_keepButton != null)
            m_keepButton.onClick.AddListener(HandleKeepClicked);
        if (m_revertButton != null)
            m_revertButton.onClick.AddListener(ClosePanel); // 닫히면 아래에서 되돌아간다
    }

    protected override void OnDestroy()
    {
        // 확인창이 뜬 채 씬이 넘어가면 ClosePanel을 못 타므로 여기서도 되돌린다 —
        // 저장은 안 됐지만 화면은 새 값으로 걸려 있어 그대로 두면 확인 없이 굳는다.
        Revert();
        CancelCountdown();

        if (m_keepButton != null)
            m_keepButton.onClick.RemoveListener(HandleKeepClicked);
        if (m_revertButton != null)
            m_revertButton.onClick.RemoveListener(ClosePanel);

        base.OnDestroy();
    }

    /// <summary>
    /// 이미 적용된 화면을 확인받는다 (#796). 인자는 <b>적용 직전</b> 값 —
    /// 되돌릴 때 그대로 다시 건다. <paramref name="onReverted"/>는 되돌린 뒤 설정 창 표시를 맞추는 데 쓴다.
    /// </summary>
    public void Begin(EWindowMode previousMode, Vector2Int previousResolution, Action onReverted)
    {
        m_previousMode = previousMode;
        m_previousResolution = previousResolution;
        m_onReverted = onReverted;
        m_pending = true;

        OpenPanel();

        CancelCountdown();
        m_countdownCts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
        CountdownAsync(m_countdownCts.Token).Forget();
    }

    public override void ClosePanel()
    {
        // 닫힘 = 확인하지 않음이다. 유지를 누른 경로는 이미 m_pending을 내려 두고 온다.
        Revert();
        CancelCountdown();
        base.ClosePanel();
    }

    private void HandleKeepClicked()
    {
        m_pending = false;
        GameSettings.KeepDisplay();
        ClosePanel();
    }

    // 실시간 기준으로 1초씩 센다 — 정산 freeze 등으로 timeScale이 멈춰도 되돌림은 흘러야 한다.
    private async UniTaskVoid CountdownAsync(CancellationToken ct)
    {
        try
        {
            for (int remaining = Mathf.CeilToInt(k_revertSeconds); remaining > 0; remaining--)
            {
                ShowRemaining(remaining);
                await UniTask.Delay(TimeSpan.FromSeconds(1), ignoreTimeScale: true, cancellationToken: ct);
            }

            ClosePanel(); // 확인하지 못했다 — 되돌린다
        }
        catch (OperationCanceledException)
        {
            // 유지·되돌리기·파괴로 중단됨
        }
    }

    private void ShowRemaining(int seconds)
    {
        if (m_countdownText == null || m_countdownFormat == null || m_countdownFormat.IsEmpty)
            return;

        // 초마다 한 번만 읽는다(매 프레임 아님) — 구독 대신 직접 조회라 언어를 바꿔도 다음 초에 따라온다.
        m_countdownFormat.Arguments = new object[] { seconds };
        m_countdownText.text = m_countdownFormat.GetLocalizedString();
    }

    private void Revert()
    {
        if (!m_pending)
            return;

        m_pending = false;
        GameSettings.ApplyDisplay(m_previousMode, m_previousResolution);
        m_onReverted?.Invoke();
        m_onReverted = null;
    }

    private void CancelCountdown()
    {
        if (m_countdownCts == null)
            return;

        m_countdownCts.Cancel();
        m_countdownCts.Dispose();
        m_countdownCts = null;
    }
}
