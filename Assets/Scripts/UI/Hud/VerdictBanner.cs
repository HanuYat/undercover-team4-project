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
/// 판정에 따라 채움 색을 바꾼다 — 진범=긍정, 오검거=실패, 경범죄=중립이며 값은 공용
/// 색 팔레트(<see cref="UiColorPalette"/>, #951)에서 가져온다. 배경·테두리 배선은
/// 이벤트 알림 토스트(SuddenEventToastView가 쓰는 HUD.prefab의 Toast/Edge 한 쌍)와 같은
/// 스프라이트 구성을 그대로 가져와 톤만 다르다 (#943).
/// </summary>
public class VerdictBanner : PanelBase
{
    [Header("텍스트")]
    [SerializeField] private TextMeshProUGUI m_titleText;   // 판정 문구
    [SerializeField] private TextMeshProUGUI m_detailText;  // 이름 + 보상

    // 이벤트 알림 토스트(SuddenEventToastView)와 같은 배선 방식 — 채움(m_toneTarget)만 판정색을
    // 입히고, 테두리(Edge)는 그 토스트처럼 판정과 무관한 고정 장식색이라 여기서 건드리지 않는다.
    [Header("톤 (판정별 강조 색)")]
    [SerializeField] private Graphic m_toneTarget;          // 카드 채움
    [Range(0f, 1f)]
    [SerializeField] private float m_fillAlpha = 0.95f;
    [Tooltip("판정별 강조 색 — 성공/실패/중립/주의를 공용 팔레트에서 가져온다 (#951)")]
    [SerializeField] private UiColorPalette m_palette;

    [Header("표시 시간")]
    [SerializeField] private float m_displaySeconds = 3f;

    [Header("등장/퇴장 애니메이션 (#943)")]
    [Tooltip("펀치인·페이드에 쓰는 CanvasGroup — 비우면 애니메이션 없이 즉시 표시된다")]
    [SerializeField] private CanvasGroup m_canvasGroup;
    [Tooltip("스케일 애니메이션 대상 — 비우면 패널 루트를 그대로 쓴다")]
    [SerializeField] private RectTransform m_animRoot;
    [SerializeField] private float m_enterSeconds = 0.18f;
    [SerializeField] private float m_exitSeconds = 0.2f;

    private const float k_enterStartScale = 0.9f;

    // 판정 문구는 규약 기반 키(Hud.Verdict.<ArrestVerdict>)라 인스펙터에서 고를 것이 없다.
    // 이름 + 보상은 고를 것이 있으므로 SerializeField로 둔다. (#497 · 문서 §2 결정 (h))
    [Tooltip("이름 + 보상 표시 — Hud.Verdict.Detail ({0}=시민 이름, {1}=보상 금액)")]
    [SerializeField] private LocalizedString m_detailFormat;

    private const string k_hudTable = "HudTable";
    private const string k_verdictPrefix = "Hud.Verdict.";

    public override bool CanCloseWithESC => false;
    public override bool IsStackable => false;

    // 표시 시퀀스(등장 → 대기 → 퇴장) 취소용 — 새 판정이 오면 재시작, 파괴되면 중단한다.
    private CancellationTokenSource m_showCts;

    private bool m_detailBound;

    private RectTransform AnimRoot => m_animRoot != null ? m_animRoot
        : (m_panelRoot != null ? m_panelRoot.GetComponent<RectTransform>() : null);

    protected override void OnDestroy()
    {
        CancelShowSequence();
        UnbindDetail();
        base.OnDestroy();
    }

    /// <summary>배너가 닫히면 구독도 끊는다 — 안 보이는 문구가 언어 변경에 반응할 이유가 없다.</summary>
    public override void ClosePanel()
    {
        UnbindDetail();
        base.ClosePanel();
    }

    /// <summary>판정 데이터를 채우고 배너를 띄운다. m_displaySeconds초 뒤 애니메이션과 함께 자동으로 숨는다.</summary>
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

        Color tone = VerdictToColor(data.Verdict);
        if (m_toneTarget != null)
            m_toneTarget.color = new Color(tone.r, tone.g, tone.b, m_fillAlpha);

        // 판정음은 배너와 같은 자리에서 낸다 — 이 배너 자체가 이미 '검거한 본인에게만' 뜨므로
        // 전파를 새로 고민할 것이 없고, 소리와 화면이 어긋날 여지도 없다.
        // 위치가 없는 확인음이라 2D다 — 옆 사람에게 들리면 "내 판정"이라는 신호가 아니게 된다.
        App.Sound?.PlaySfx2D(VerdictToSound(data.Verdict));

        CancelShowSequence();
        m_showCts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
        PlayShowSequenceAsync(m_showCts.Token).Forget();
    }

    // 등장(펀치인) → 표시 유지 → 퇴장(페이드) → 실제 닫기를 한 시퀀스로 잇는다. 전부 실시간 기준
    // (ignoreTimeScale과 같은 뜻으로 Time.unscaledDeltaTime을 쓴다)이라 정산 freeze로 timeScale이
    // 건드려져도 흐른다 — 기존 AutoHideAsync가 지키던 성질을 애니메이션에도 그대로 유지한다.
    private async UniTaskVoid PlayShowSequenceAsync(CancellationToken ct)
    {
        try
        {
            if (m_canvasGroup != null)
                m_canvasGroup.alpha = 0f;

            RectTransform rt = AnimRoot;
            if (rt != null)
                rt.localScale = Vector3.one * k_enterStartScale;

            OpenPanel();

            await AnimateAsync(m_enterSeconds, k_enterStartScale, 1f, 0f, 1f, ct);

            await UniTask.Delay(TimeSpan.FromSeconds(m_displaySeconds), ignoreTimeScale: true, cancellationToken: ct);

            await AnimateAsync(m_exitSeconds, 1f, 1f, 1f, 0f, ct);

            ClosePanel();
        }
        catch (OperationCanceledException)
        {
            // 새 판정으로 재시작되거나 파괴됨 — 이전 시퀀스는 조용히 중단
        }
    }

    // scale·alpha를 각각 [fromScale→toScale], [fromAlpha→toAlpha]로 seconds 동안 선형 보간한다.
    // CanvasGroup·AnimRoot 중 비어 있는 쪽은 건드리지 않는다(둘 다 선택 사항).
    private async UniTask AnimateAsync(
        float seconds, float fromScale, float toScale, float fromAlpha, float toAlpha, CancellationToken ct)
    {
        RectTransform rt = AnimRoot;

        if (seconds <= 0f)
        {
            if (rt != null)
                rt.localScale = Vector3.one * toScale;
            if (m_canvasGroup != null)
                m_canvasGroup.alpha = toAlpha;
            return;
        }

        float elapsed = 0f;
        while (elapsed < seconds)
        {
            ct.ThrowIfCancellationRequested();

            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / seconds);

            if (rt != null)
                rt.localScale = Vector3.one * Mathf.Lerp(fromScale, toScale, t);
            if (m_canvasGroup != null)
                m_canvasGroup.alpha = Mathf.Lerp(fromAlpha, toAlpha, t);

            await UniTask.Yield(PlayerLoopTiming.Update, ct);
        }

        if (rt != null)
            rt.localScale = Vector3.one * toScale;
        if (m_canvasGroup != null)
            m_canvasGroup.alpha = toAlpha;
    }

    private void CancelShowSequence()
    {
        if (m_showCts == null)
            return;
        m_showCts.Cancel();
        m_showCts.Dispose();
        m_showCts = null;
    }

    // 인자를 먼저 넣어야 구독 시점의 첫 발화부터 이름·금액이 들어간 문장이 나온다 (문서 §2 결정 (e)).
    private void BindDetail(VerdictFeedbackData data)
    {
        if (m_detailFormat == null || m_detailFormat.IsEmpty)
        {
            Debug.LogWarning("VerdictBanner: 이름·보상 문구가 연결되지 않았다", this);

            // 그냥 돌아가면 직전 문구가 그대로 남는다 — 씬에 넣어 둔 디자인용 예시 문구까지 살아남아
            // 배선 사고가 '멀쩡한 판정 결과'로 보인다. 금액은 붙일 수 없어도 이름은 진짜 값을 낸다.
            m_detailText.text = data.CitizenName;
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

    // 갈리는 기준은 '계상됐는가'다 — 계상 안 되는 판정(오검거·생포 조건 불충족)만 실패음. (#766)
    private static EAudioClip VerdictToSound(ArrestVerdict verdict)
    {
        return verdict.IsCredited() ? EAudioClip.UiSuccess : EAudioClip.UiFail;
    }

    // 판정 → 팔레트의 의미 색. 배선이 빠지면 흰색으로 뜬다 — 판정별 구분이 사라져 눈에 띄므로
    // 조용히 넘기지 않고 경고까지 남긴다.
    private Color VerdictToColor(ArrestVerdict verdict)
    {
        if (m_palette == null)
        {
            Debug.LogWarning("VerdictBanner: 색 팔레트가 연결되지 않았다", this);
            return Color.white;
        }

        return verdict switch
        {
            ArrestVerdict.WantedCriminal => m_palette.Positive,
            ArrestVerdict.Misdemeanor => m_palette.Neutral,
            ArrestVerdict.ConditionUnmet => m_palette.Caution,
            _ => m_palette.Negative,
        };
    }

}
