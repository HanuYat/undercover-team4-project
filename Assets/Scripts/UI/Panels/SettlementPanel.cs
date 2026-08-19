using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.UI;

/// <summary>
/// 라운드 정산 패널 (#107, GDD 3-2, #693) — 라운드 결과·팀 자금 증감·최다 오검거 칭호(코믹 스탯)를 보여준다.
/// 표시는 각 클라 로컬. 데이터는 SettlementController가 서버 권위 값으로 채워 <see cref="Show"/>로 넘긴다.
///
/// 연출: 패널(창·배경)은 즉시 뜨고, 결과/자금/칭호/내 몫 4줄은 <see cref="m_textRevealDelay"/>초 뒤에 등장한다.
/// 텍스트가 뜨는 순간부터 <see cref="m_countdownSeconds"/>초 상점 복귀 카운트다운을 화면 중앙 상단에 보여준다.
/// (실제 복귀는 RoundEndResetter가 서버 주도로 처리 — 그 딜레이 = 텍스트 지연 + 카운트다운으로 맞춰 둔다)
///
/// 열려 있는 동안 로컬 플레이어 입력을 정지(PlayerInputHandler.SetSuspended)해 정산 화면 뒤 월드로
/// 이동·시점 입력이 새지 않게 하고, 버튼을 누를 수 있게 커서를 띄운다. 확인 버튼·ESC로 닫으면 되돌린다.
///
/// 닫는 것은 곧 "다 읽었다"는 확인이다 (#509) — 확인 버튼이든 ESC든 SettlementConfirmGate에 보고하고,
/// 전원이 보고하면 카운트다운이 끝나기 전이라도 서버가 복귀를 앞당긴다. 카운트다운 문구에 그 인원을 함께 보여준다.
/// </summary>
public class SettlementPanel : PanelBase
{
    [Header("표시 텍스트 (지연 등장)")]
    [SerializeField]
    private TextMeshProUGUI m_resultText;

    [SerializeField]
    private TextMeshProUGUI m_fundText;

    [SerializeField]
    private TextMeshProUGUI m_topOffenderText;

    [SerializeField]
    private TextMeshProUGUI m_personalText;

    [Header("상점 복귀 카운트다운 (화면 중앙 상단)")]
    [SerializeField]
    private TextMeshProUGUI m_countdownText;

    [Header("연출 타이밍")]
    [Tooltip("패널이 뜬 뒤 4줄 텍스트가 나타나기까지의 지연(초)")]
    [SerializeField]
    private float m_textRevealDelay = 1.5f;

    [Tooltip("텍스트 등장 후 상점 복귀까지 카운트다운(초). RoundEndResetter 딜레이 = 이 값 + 텍스트 지연으로 맞출 것")]
    [SerializeField]
    private float m_countdownSeconds = 10f;

    [Header("닫기 버튼")]
    [SerializeField]
    private Button m_confirmButton;

    // 문구는 채우는 순간 한 번 읽고 끝낸다 — HUD와 달리 StringChanged를 구독하지 않는다.
    // 정산 화면은 10초짜리 결과 요약이고 그 사이 설정 창으로 언어를 바꿀 경로가 없어서다. (#497)
    [Header("문구")]
    [Tooltip("팀 자금 증감 요약 — Settlement.Fund.Summary ({0}=팀 몫 증감, {1}=팀 자금, {2}=할당량)")]
    [SerializeField]
    private LocalizedString m_fundSummary;

    [Tooltip("실패 시 결과 줄에 사유를 흡수 — Settlement.Result.WithReason ({0}=결과, {1}=사유)")]
    [SerializeField]
    private LocalizedString m_resultWithReasonFormat;

    [Tooltip("최다 오검거 칭호 — Settlement.TopOffender.Some ({0}=이름)")]
    [SerializeField]
    private LocalizedString m_topOffenderFormat;

    [Tooltip("개인 몫 — Settlement.Personal.Earned ({0}=이번 판 수익, {1}=개인 자금 잔액)")]
    [SerializeField]
    private LocalizedString m_personalFormat;

    [Tooltip("복귀 카운트다운 — Settlement.Countdown ({0}=도착지, {1}=남은 초, {2}=확인 인원, {3}=총원)")]
    [SerializeField]
    private LocalizedString m_countdownFormat;

    // 결과·종료 사유·복귀 도착지는 규약 기반 키(접두 + enum 이름)라 인스펙터에서 고를 것이 없다.
    // 규약은 RoundResult·RoundEndReason 선언부의 [LocalizedEnum]에 적혀 있다. (문서 §2 결정 (h))
    private const string k_table = "SettlementTable";
    private const string k_resultPrefix = "Settlement.Result.";
    private const string k_returnPrefix = "Settlement.Return.";
    private const string k_reasonPrefix = "Settlement.Reason.";

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    // 로컬 플레이어 입력을 정지 중인지 — 자동복귀(OnDestroy) 시 대칭 복구 + 커서 Push/Pop 1:1 판단용.
    private bool m_playerBlocked;

    // 텍스트 지연 등장 + 카운트다운 시퀀스 취소용 — 닫히거나 파괴되면 중단한다.
    private CancellationTokenSource m_revealCts;

    private PlayerWallet m_wallet;

    // 확인 인원이 바뀌어도 문구를 다시 그려야 해서 남은 초를 들고 있는다 (초 갱신과 인원 갱신이 따로 온다).
    private int m_secondsLeft;

    // 결과 3줄이 뜬 뒤부터 확인을 받는다 — 버튼과 ESC가 같은 구간을 쓴다.
    private bool m_confirmEnabled;

    // 확인 보고는 이번 정산에 한 번만 — 닫기 경로가 둘(확인 버튼·ESC)이라 래치를 둔다.
    private bool m_confirmReported;

    private SettlementConfirmGate Gate => App.Game.SettlementGate;

    protected override void Awake()
    {
        base.Awake();
        // 연출 대상들도 패널과 같은 시작 상태(닫힘)로 맞춘다 (딤 배경은 PanelBase가 맞춘다).
        SetResultTextsVisible(false);
        SetCountdownVisible(false);
        SetConfirmEnabled(false); // 결과가 뜨기 전에는 못 누른다 (#509)
        if (m_confirmButton != null)
            m_confirmButton.onClick.AddListener(ClosePanel);
    }

    protected override void OnDestroy()
    {
        CancelReveal();
        UnbindWallet();
        UnbindGate();

        if (m_playerBlocked)
            SetLocalPlayerBlocked(false);
        if (m_confirmButton != null)
            m_confirmButton.onClick.RemoveListener(ClosePanel);
        base.OnDestroy();
    }

    // 이번 정산의 도착지 — 성공은 상점, 실패는 로비(새 판). RoundEndResetter의 분기와 맞춘다 (#395).
    private string m_returnLabel = string.Empty;

    // 오검거 0회면 칭호 줄 자체를 숨긴다 (#693) — 지연 등장 시점에도 다시 켜지지 않게 기억해 둔다.
    private bool m_hasTopOffender;

    /// <summary>정산 데이터를 채우고 패널을 연다. 4줄 텍스트는 지연 후 등장한다.</summary>
    public void Show(SettlementData data)
    {
        // None(종료 전)으로 열릴 일은 없지만, 들어와도 키가 없는 조회로 새지 않게 실패로 접는다.
        RoundResult result = data.Result == RoundResult.Success ? RoundResult.Success : RoundResult.Failure;

        m_returnLabel = LocalizedStrings.Get(k_table, k_returnPrefix + result);

        if (m_resultText != null)
        {
            string resultBase = LocalizedStrings.Get(k_table, k_resultPrefix + result);
            // 실패 원인은 별도 줄 대신 결과 줄에 흡수한다 — "종료 사유" 줄을 없애며 유일한 정보처였던
            // 실패 원인이 사라지지 않게 하기 위함 (#693 검토포인트 2).
            m_resultText.text =
                result == RoundResult.Failure && data.Reason != RoundEndReason.None
                    ? m_resultWithReasonFormat.GetLocalizedString(resultBase, ReasonToText(data.Reason))
                    : resultBase;
        }

        if (m_fundText != null)
            // 할당량은 경찰서 납부분이라 총 수익에서 떼고 남은 초과분만 팀 몫이 된다 (#395).
            // 압축 표기(팀 몫 증감 → 팀 자금, 할당량 납부분 괄호)로도 그 근거가 보이게 한다 (#693 검토포인트 1).
            m_fundText.text = m_fundSummary.GetLocalizedString(
                data.FundDelta,
                data.FundBalance,
                data.TargetFund
            );

        m_hasTopOffender = data.TopOffenderCount > 0;
        if (m_topOffenderText != null)
            m_topOffenderText.text = m_hasTopOffender
                ? m_topOffenderFormat.GetLocalizedString(data.TopOffenderName)
                : string.Empty;

        BindWallet();
        BindGate();

        m_confirmReported = false;
        m_secondsLeft = Mathf.CeilToInt(m_countdownSeconds);

        SetResultTextsVisible(false); // 창은 바로 뜨되 텍스트·카운트다운은 지연 등장
        SetCountdownVisible(false);
        SetConfirmEnabled(false); // 확인은 결과가 뜬 뒤부터 — 못 읽고 넘기는 것을 막는다 (#509)
        OpenPanel();

        CancelReveal();
        m_revealCts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
        RevealAndCountdownAsync(m_revealCts.Token).Forget();
    }

    // 텍스트 지연 등장 → 등장 순간부터 카운트다운. 실시간 기준(freeze로 timeScale이 건드려져도 흐르게).
    private async UniTaskVoid RevealAndCountdownAsync(CancellationToken ct)
    {
        try
        {
            await UniTask.Delay(
                TimeSpan.FromSeconds(m_textRevealDelay),
                ignoreTimeScale: true,
                cancellationToken: ct
            );

            SetResultTextsVisible(true);
            SetCountdownVisible(true);
            SetConfirmEnabled(true);

            for (int sec = Mathf.CeilToInt(m_countdownSeconds); sec > 0; sec--)
            {
                SetCountdownSeconds(sec);
                await UniTask.Delay(TimeSpan.FromSeconds(1), ignoreTimeScale: true, cancellationToken: ct);
            }
            SetCountdownSeconds(0);
        }
        catch (OperationCanceledException)
        {
            // 패널이 닫히거나 파괴됨 — 연출 중단(가시성 정리는 ClosePanel/Awake가 담당)
        }
    }

    private void SetCountdownSeconds(int seconds)
    {
        m_secondsLeft = seconds;
        RefreshCountdownText();
    }

    // 남은 초 + 확인 인원(n/총원)을 한 문구로 그린다 (#509). 게이트가 없는 씬(테스트)은 혼자 있는 것으로 센다.
    private void RefreshCountdownText()
    {
        if (m_countdownText == null)
            return;

        SettlementConfirmGate gate = Gate;
        int confirmed = gate != null ? gate.ConfirmedCount : 0;
        int expected = gate != null ? gate.ExpectedCount : 1;

        m_countdownText.text = m_countdownFormat.GetLocalizedString(
            m_returnLabel,
            m_secondsLeft,
            confirmed,
            expected
        );
    }

    // 실패 결과 줄에 흡수할 사유 문구 — 규약 키 Settlement.Reason.<RoundEndReason>. (다운·기능 정지(Die) 혼재는 #364)
    // None은 라운드 종료 전이라 표시할 사유가 없다 — 키도 두지 않는다.
    private static string ReasonToText(RoundEndReason reason)
    {
        return reason == RoundEndReason.None
            ? string.Empty
            : LocalizedStrings.Get(k_table, k_reasonPrefix + reason);
    }

    // 개인 몫은 SettlementData에 없다 — 전원에게 가는 브로드캐스트라 실으면 "본인만"이 깨진다.
    // 원격 클라는 NetworkVariable이 정산 메시지보다 늦게 올 수 있어 구독해 둔다.
    private void BindWallet()
    {
        UnbindWallet();

        m_wallet = PlayerWallet.Local;
        if (m_wallet != null)
            m_wallet.RoundEarnedVar.OnValueChanged += HandleRoundEarnedChanged;

        RefreshPersonalText();
    }

    private void UnbindWallet()
    {
        if (m_wallet != null)
            m_wallet.RoundEarnedVar.OnValueChanged -= HandleRoundEarnedChanged;

        m_wallet = null;
    }

    private void HandleRoundEarnedChanged(int previous, int current) => RefreshPersonalText();

    // 확인 인원은 서버가 세어 복제한다 — 바뀔 때마다 카운트다운 문구를 다시 그린다. (#509)
    // 창을 닫아도 카운트다운은 남으므로 구독은 파괴될 때까지 유지한다.
    private void BindGate()
    {
        UnbindGate();

        if (Gate != null)
            Gate.OnCountsChanged += RefreshCountdownText;
    }

    private void UnbindGate()
    {
        if (Gate != null)
            Gate.OnCountsChanged -= RefreshCountdownText;
    }

    private void RefreshPersonalText()
    {
        if (m_personalText == null) return;

        int earned = m_wallet != null ? m_wallet.RoundEarned : 0;
        int balance = m_wallet != null ? m_wallet.Balance : 0;
        m_personalText.text = m_personalFormat.GetLocalizedString(earned, balance);
    }

    // 결과 텍스트 4줄의 표시를 한꺼번에 켜고 끈다. (카운트다운은 별도 — 닫아도 남긴다)
    // 칭호 줄은 오검거 0회면 지연 등장 이후에도 계속 숨긴다 (#693).
    private void SetResultTextsVisible(bool visible)
    {
        if (m_resultText != null)
            m_resultText.gameObject.SetActive(visible);
        if (m_fundText != null)
            m_fundText.gameObject.SetActive(visible);
        if (m_topOffenderText != null)
            m_topOffenderText.gameObject.SetActive(visible && m_hasTopOffender);
        if (m_personalText != null)
            m_personalText.gameObject.SetActive(visible);
    }

    private void SetCountdownVisible(bool visible)
    {
        if (m_countdownText != null)
            m_countdownText.gameObject.SetActive(visible);
    }

    // 결과가 뜨기 전에는 확인을 받지 않는다 (#509) — 버튼을 잠그고, 같은 구간의 ESC도 확인으로 세지 않는다.
    private void SetConfirmEnabled(bool enabled)
    {
        m_confirmEnabled = enabled;
        if (m_confirmButton != null)
            m_confirmButton.interactable = enabled;
    }

    // 창을 닫는 것 = "다 읽었다" (#509). 확인 버튼이든 ESC든 같은 신호로 서버에 보고한다.
    // 열려 있고 확인 가능한 구간일 때만 — 씬 정리로 다시 불려도 새지 않게.
    private void ReportConfirmed()
    {
        if (m_confirmReported || !m_confirmEnabled || !IsOpened)
            return;

        m_confirmReported = true;
        if (Gate != null)
            Gate.ReportSelfConfirmed();
    }

    private void CancelReveal()
    {
        if (m_revealCts == null)
            return;
        m_revealCts.Cancel();
        m_revealCts.Dispose();
        m_revealCts = null;
    }

    public override void OpenPanel()
    {
        base.OpenPanel();
        SetLocalPlayerBlocked(true);
    }

    public override void ClosePanel()
    {
        ReportConfirmed();

        // 카운트다운은 일부러 남긴다 — 창·배경을 닫아도 상점 복귀까지 남은 시간을 계속 보여준다.
        // (시퀀스를 취소하지 않으므로 카운트다운은 계속 돌고, 결과 4줄은 창이 꺼지며 함께 숨는다)
        SetLocalPlayerBlocked(false);
        UnbindWallet();
        base.ClosePanel();
    }

    // 로컬 플레이어(오너)의 입력 정지 + 커서 해제를 함께 처리한다.
    // 정산 화면 뒤 월드로 이동·시점이 새지 않게 입력을 멈추고(SetSuspended), 버튼을 누를 수 있게
    // 커서를 푼다(CursorLock.PushUnlock — 닫을 때 Pop).
    private void SetLocalPlayerBlocked(bool blocked)
    {
        // 같은 값으로 두 번 불려도(PanelBase.OpenPanel엔 재진입 가드가 없다) 커서 Push/Pop이 어긋나지 않게 한다 (#352)
        if (m_playerBlocked == blocked)
            return;

        m_playerBlocked = blocked;

        // 플레이어 조회보다 먼저 — 라운드 종료 디스폰으로 플레이어가 사라져도 Push/Pop 짝은 유지돼야 한다 (#352)
        if (blocked)
            CursorLock.PushUnlock();
        else
            CursorLock.PopUnlock();

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.LocalClient == null || nm.LocalClient.PlayerObject == null)
            return;

        GameObject player = nm.LocalClient.PlayerObject.gameObject;

        PlayerInputHandler input = player.GetComponent<PlayerInputHandler>();
        if (input != null)
            input.SetSuspended(blocked);

        PlayerMovement movement = player.GetComponent<PlayerMovement>();
        if (movement == null)
            return;

        // 정산을 닫으면 라운드 종료 freeze를 무시하고 움직일 수 있게 한다 (호스트도 — 다운 상태면 여전히 잠김) (#107)
        movement.SetIgnoreRoundEndFreeze(!blocked);
    }
}
