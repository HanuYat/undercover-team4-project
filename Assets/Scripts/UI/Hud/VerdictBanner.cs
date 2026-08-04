using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
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

    // 판정 문구는 규약 기반 키(Hud.Verdict.<ArrestVerdict>)라 인스펙터에서 고를 것이 없다.
    // 이름 + 보상은 고를 것이 있으므로 SerializeField로 둔다. (#497 · 문서 §2 결정 (h))
    [Tooltip("이름 + 보상 표시 — Hud.Verdict.Detail ({0}=시민 이름, {1}=보상 금액)")]
    [SerializeField] private LocalizedString m_detailFormat;

    private const string k_hudTable = "HudTable";
    private const string k_verdictPrefix = "Hud.Verdict.";

    public override bool CanCloseWithESC => false;
    public override bool IsStackable => false;

    // 자동 숨김 타이머 취소용 — 새 판정이 오면 재시작, 파괴되면 중단한다.
    private CancellationTokenSource m_hideCts;

    private bool m_detailBound;

    protected override void OnDestroy()
    {
        CancelHide();
        UnbindDetail();
        base.OnDestroy();
    }

    /// <summary>배너가 닫히면 구독도 끊는다 — 안 보이는 문구가 언어 변경에 반응할 이유가 없다.</summary>
    public override void ClosePanel()
    {
        UnbindDetail();
        base.ClosePanel();
    }

    /// <summary>판정 데이터를 채우고 배너를 띄운다. m_displaySeconds초 뒤 자동으로 숨는다.</summary>
    public void Show(VerdictFeedbackData data)
    {
        if (m_titleText != null)
            m_titleText.text = LocalizedStrings.Get(k_hudTable, k_verdictPrefix + data.Verdict);

        if (m_detailText != null)
        {
            // 보상이 없는 판정(오검거·프로필 없는 경범죄)은 이름만 — 붙일 금액이 없다.
            if (data.Reward > 0)
                BindDetail(data);
            else
            {
                UnbindDetail();
                m_detailText.text = data.CitizenName;
            }
        }

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

    // 인자를 먼저 넣어야 구독 시점의 첫 발화부터 이름·금액이 들어간 문장이 나온다 (문서 §2 결정 (e)).
    private void BindDetail(VerdictFeedbackData data)
    {
        if (m_detailFormat == null || m_detailFormat.IsEmpty)
        {
            Debug.LogWarning("VerdictBanner: 이름·보상 문구가 연결되지 않았다", this);
            return;
        }

        UnbindDetail();

        m_detailFormat.Arguments = new object[] { data.CitizenName, data.Reward };
        m_detailFormat.StringChanged += HandleDetailChanged;
        m_detailBound = true;
    }

    private void HandleDetailChanged(string localized)
    {
        if (m_detailText != null)
            m_detailText.text = localized;
    }

    private void UnbindDetail()
    {
        if (!m_detailBound)
            return;

        m_detailFormat.StringChanged -= HandleDetailChanged;
        m_detailBound = false;
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
