using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 라운드 정산 패널 (#107, GDD 3-2) — 라운드 결과·팀 자금 증감·이번 판 최다 오검거(코믹 스탯)를 보여준다.
/// 표시는 각 클라 로컬. 데이터는 SettlementController가 서버 권위 값으로 채워 <see cref="Show"/>로 넘긴다.
///
/// 연출: 패널(창·배경)은 즉시 뜨고, 결과/자금/오검거 3줄은 <see cref="m_textRevealDelay"/>초 뒤에 등장한다.
/// 텍스트가 뜨는 순간부터 <see cref="m_countdownSeconds"/>초 상점 복귀 카운트다운을 화면 중앙 상단에 보여준다.
/// (실제 복귀는 RoundEndResetter가 서버 주도로 처리 — 그 딜레이 = 텍스트 지연 + 카운트다운으로 맞춰 둔다)
///
/// 열려 있는 동안 로컬 플레이어 입력을 정지(PlayerInputHandler.SetSuspended)해 정산 화면 뒤 월드로
/// 이동·시점 입력이 새지 않게 하고, 버튼을 누를 수 있게 커서를 띄운다. 확인 버튼·ESC로 닫으면 되돌린다.
/// </summary>
public class SettlementPanel : PanelBase
{
    [Header("표시 텍스트 (지연 등장)")]
    [SerializeField]
    private TextMeshProUGUI m_resultText;

    [SerializeField]
    private TextMeshProUGUI m_reasonText;

    [SerializeField]
    private TextMeshProUGUI m_fundText;

    [SerializeField]
    private TextMeshProUGUI m_topOffenderText;

    [Header("상점 복귀 카운트다운 (화면 중앙 상단)")]
    [SerializeField]
    private TextMeshProUGUI m_countdownText;

    [Header("연출 타이밍")]
    [Tooltip("패널이 뜬 뒤 3줄 텍스트가 나타나기까지의 지연(초)")]
    [SerializeField]
    private float m_textRevealDelay = 1.5f;

    [Tooltip("텍스트 등장 후 상점 복귀까지 카운트다운(초). RoundEndResetter 딜레이 = 이 값 + 텍스트 지연으로 맞출 것")]
    [SerializeField]
    private float m_countdownSeconds = 10f;

    [Header("닫기 버튼")]
    [SerializeField]
    private Button m_confirmButton;

    [Header("배경 딤 (패널과 함께 켜고 끔)")]
    [Tooltip("정산 화면 뒤를 어둡게 덮는 풀스크린 오버레이 — 패널이 열리면 켜지고 닫히면 꺼진다")]
    [SerializeField]
    private GameObject m_background;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    // 열기 직전의 커서 잠금 상태 — 닫을 때 이 상태로 되돌린다.
    private bool m_cursorUnlockedBeforeOpen;

    // 로컬 플레이어 입력을 정지 중인지 — 자동복귀(OnDestroy) 시 대칭 복구 판단용.
    private bool m_playerBlocked;

    // 텍스트 지연 등장 + 카운트다운 시퀀스 취소용 — 닫히거나 파괴되면 중단한다.
    private CancellationTokenSource m_revealCts;

    protected override void Awake()
    {
        base.Awake();
        // 배경 딤·연출 대상들도 패널과 같은 시작 상태(닫힘)로 맞춘다.
        if (m_background != null)
            m_background.SetActive(false);
        SetResultTextsVisible(false);
        SetCountdownVisible(false);
        if (m_confirmButton != null)
            m_confirmButton.onClick.AddListener(ClosePanel);
    }

    protected override void OnDestroy()
    {
        CancelReveal();

        if (m_playerBlocked)
            SetLocalPlayerBlocked(false);
        if (m_confirmButton != null)
            m_confirmButton.onClick.RemoveListener(ClosePanel);
        base.OnDestroy();
    }

    /// <summary>정산 데이터를 채우고 패널을 연다. 3줄 텍스트는 지연 후 등장한다.</summary>
    public void Show(SettlementData data)
    {
        if (m_resultText != null)
            m_resultText.text = data.Result == RoundResult.Success ? "라운드 성공!" : "게임 오버";

        if (m_reasonText != null)
            m_reasonText.text = ReasonToText(data.Reason);

        if (m_fundText != null)
            // 자금 잔액·증감 + 이번 라운드 유치장 정산 내역(#340). 증감(FundDelta)이 곧 이번 라운드 정산액이다.
            m_fundText.text =
                $"팀 자금  {data.FundBalance:N0}원  ({data.FundDelta:+#,##0;-#,##0;0})"
                + $"\n수감 정산: 현상수배 {data.CriminalCount} · 경범죄 {data.MisdemeanorCount}";

        if (m_topOffenderText != null)
            m_topOffenderText.text =
                data.TopOffenderCount > 0
                    ? $"이번 판 최다 오검거: {data.TopOffenderName} ({data.TopOffenderCount}회)"
                    : "이번 판 오검거 없음 — 깨끗한 수사!";

        SetResultTextsVisible(false); // 창은 바로 뜨되 텍스트·카운트다운은 지연 등장
        SetCountdownVisible(false);
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

            for (int sec = Mathf.CeilToInt(m_countdownSeconds); sec > 0; sec--)
            {
                if (m_countdownText != null)
                    m_countdownText.text = $"상점 복귀까지 {sec}초";
                await UniTask.Delay(TimeSpan.FromSeconds(1), ignoreTimeScale: true, cancellationToken: ct);
            }
            if (m_countdownText != null)
                m_countdownText.text = "상점 복귀까지 0초";
        }
        catch (OperationCanceledException)
        {
            // 패널이 닫히거나 파괴됨 — 연출 중단(가시성 정리는 ClosePanel/Awake가 담당)
        }
    }

    // 종료 사유 문구.
    private static string ReasonToText(RoundEndReason reason)
    {
        return reason switch
        {
            RoundEndReason.QuotaMet => "검거 할당량 달성",
            RoundEndReason.TimeOver => "제한시간 초과 — 검거 할당량 미달",
            RoundEndReason.AllPlayersDown => "플레이어 전원 다운",
            _ => string.Empty,
        };
    }

    // 결과 텍스트 4줄의 표시를 한꺼번에 켜고 끈다. (카운트다운은 별도 — 닫아도 남긴다)
    private void SetResultTextsVisible(bool visible)
    {
        if (m_resultText != null)
            m_resultText.gameObject.SetActive(visible);
        if (m_reasonText != null)
            m_reasonText.gameObject.SetActive(visible);
        if (m_fundText != null)
            m_fundText.gameObject.SetActive(visible);
        if (m_topOffenderText != null)
            m_topOffenderText.gameObject.SetActive(visible);
    }

    private void SetCountdownVisible(bool visible)
    {
        if (m_countdownText != null)
            m_countdownText.gameObject.SetActive(visible);
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
        if (m_background != null)
            m_background.SetActive(true);
        base.OpenPanel();
        SetLocalPlayerBlocked(true);
    }

    public override void ClosePanel()
    {
        // 카운트다운은 일부러 남긴다 — 창·배경을 닫아도 상점 복귀까지 남은 시간을 계속 보여준다.
        // (시퀀스를 취소하지 않으므로 카운트다운은 계속 돌고, 결과 4줄은 창이 꺼지며 함께 숨는다)
        if (m_background != null)
            m_background.SetActive(false);
        SetLocalPlayerBlocked(false);
        base.ClosePanel();
    }

    // 로컬 플레이어(오너)의 입력 정지 + 커서 해제를 함께 처리한다.
    // 정산 화면 뒤 월드로 이동·시점이 새지 않게 입력을 멈추고(SetSuspended), 버튼을 누를 수 있게
    // 커서를 띄운다(SetCursorUnlocked — SignalDecoderHud 관례). 닫을 때는 열기 직전의 커서 상태로 되돌린다.
    // ponytail: 호스트는 라운드 종료 후 RoundManager.GameplayFrozen(Phase==Ended)으로도 이동이 막혀 있어
    //           닫아도 계속 못 움직인다(원격 클라는 GameplayFrozen이 false라 닫으면 움직임). 페이즈 클라 동기화(#43)가
    //           붙으면 자연히 일관돼진다 — 그 전까진 호스트 한정 잔상으로 둔다.
    private void SetLocalPlayerBlocked(bool blocked)
    {
        m_playerBlocked = blocked;
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

        if (blocked)
        {
            // 열기 직전 커서 상태를 기억해 닫을 때 되돌린다 (InventoryBarView·SignalDecoderHud 관례)
            m_cursorUnlockedBeforeOpen = Cursor.lockState == CursorLockMode.None;
            movement.SetCursorUnlocked(true);
            movement.SetIgnoreRoundEndFreeze(false); // 정산 열려 있는 동안은 그대로 정지
        }
        else
        {
            movement.SetCursorUnlocked(m_cursorUnlockedBeforeOpen);
            // 정산을 닫으면 라운드 종료 freeze를 무시하고 움직일 수 있게 한다 (호스트도 — 다운 상태면 여전히 잠김) (#107)
            movement.SetIgnoreRoundEndFreeze(true);
        }
    }
}
