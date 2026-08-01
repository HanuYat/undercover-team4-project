using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 검거 판정 결과 토스트 배너 (#306) — 본부 인계 판정 결과(진범 검거/오검거/경범죄)를 화면에 잠깐 띄운다.
/// 데이터는 ArrestVerdictFeedback가 서버 권위 값으로 채워 <see cref="Show"/>로 넘긴다. 표시는 검거한
/// 플레이어 로컬에서만 이뤄진다(전파 대상 좁힘은 ArrestVerdictFeedback가 담당).
///
/// 게임 중 검거마다 반복 발생하므로 입력을 멈추지 않는 비차단 HUD다(정산 패널과 달리 모달 아님):
/// ESC 스택에 쌓지 않고(IsStackable=false), <see cref="m_displaySeconds"/>초 뒤 자동으로 숨는다.
/// 판정에 따라 톤 색을 바꾼다 — 진범=긍정, 오검거=경고, 경범죄=중립.
/// </summary>
public class VerdictBanner : PanelBase
{
    [Header("텍스트")]
    [SerializeField] private TextMeshProUGUI m_titleText;   // 판정 문구
    [SerializeField] private TextMeshProUGUI m_detailText;  // 이름 + 보상

    [Header("톤 (판정별 강조 색)")]
    [SerializeField] private Graphic m_toneTarget;          // 색을 입힐 대상(배경/테두리 등)
    [SerializeField] private Color m_positiveColor = new Color(0.20f, 0.70f, 0.35f); // 진범 검거
    [SerializeField] private Color m_negativeColor = new Color(0.80f, 0.25f, 0.25f); // 오검거
    [SerializeField] private Color m_neutralColor  = new Color(0.45f, 0.45f, 0.50f); // 경범죄

    [Header("표시 시간")]
    [SerializeField] private float m_displaySeconds = 3f;

    public override bool CanCloseWithESC => false;
    public override bool IsStackable => false;

    // 자동 숨김 타이머 취소용 — 새 판정이 오면 재시작, 파괴되면 중단한다.
    private CancellationTokenSource m_hideCts;

    protected override void OnDestroy()
    {
        CancelHide();
        base.OnDestroy();
    }

    /// <summary>판정 데이터를 채우고 배너를 띄운다. m_displaySeconds초 뒤 자동으로 숨는다.</summary>
    public void Show(VerdictFeedbackData data)
    {
        if (m_titleText != null)
            m_titleText.text = VerdictToTitle(data.Verdict);

        if (m_detailText != null)
            m_detailText.text = data.Reward > 0
                ? $"{data.CitizenName}  —  보상 {data.Reward:N0}원"
                : data.CitizenName;

        if (m_toneTarget != null)
            m_toneTarget.color = VerdictToColor(data.Verdict);

        OpenPanel();

        CancelHide();
        m_hideCts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
        AutoHideAsync(m_hideCts.Token).Forget();
    }

    // 실시간 기준(정산 freeze로 timeScale이 건드려져도 흐르게) N초 뒤 숨김.
    private async UniTaskVoid AutoHideAsync(CancellationToken ct)
    {
        try
        {
            await UniTask.Delay(TimeSpan.FromSeconds(m_displaySeconds), ignoreTimeScale: true, cancellationToken: ct);
            ClosePanel();
        }
        catch (OperationCanceledException)
        {
            // 새 판정으로 재시작되거나 파괴됨 — 이전 타이머는 조용히 중단
        }
    }

    private void CancelHide()
    {
        if (m_hideCts == null)
            return;
        m_hideCts.Cancel();
        m_hideCts.Dispose();
        m_hideCts = null;
    }

    // 판정 문구 — ArrestJudge.LogVerdict의 tag 매핑과 동일.
    private static string VerdictToTitle(ArrestVerdict verdict)
    {
        return verdict switch
        {
            ArrestVerdict.WantedCriminal => "현상수배범 검거!",
            ArrestVerdict.Misdemeanor => "경범죄 처리",
            _ => "오검거",
        };
    }

    private Color VerdictToColor(ArrestVerdict verdict)
    {
        return verdict switch
        {
            ArrestVerdict.WantedCriminal => m_positiveColor,
            ArrestVerdict.Misdemeanor => m_neutralColor,
            _ => m_negativeColor,
        };
    }
}
