using TMPro;
using UnityEngine;

/// <summary>
/// 화면 중앙 상단의 라운드 남은 시간 표시(mm:ss). HUD 캔버스에 부착한다.
/// 온라인 세션에서는 RoundTimerSync(서버 시계 기준 종료 시각)를 읽어 늦게 접속해도 정확하고,
/// 그 외(오프라인·아직 미스폰)에는 로컬 진실값인 RoundManager.RemainingSeconds로 폴백한다.
/// 표시 전용 — 시간 초과 판정에는 관여하지 않는다.
/// </summary>
public class RoundTimerUI : MonoBehaviour
{
    [Header("참조 (비우면 씬에서 자동 탐색)")]
    [SerializeField]
    private RoundTimerSync m_timerSync;

    private RoundManager Round => App.Game.Round;

    [Header("표시")]
    [Tooltip("mm:ss를 표시할 TextMeshProUGUI — 화면 중앙 상단에 앵커해 배치")]
    [SerializeField]
    private TextMeshProUGUI m_timerText;

    [Tooltip("표시 루트 — 판·테두리·숫자를 함께 켜고 끈다 (LocalizedMessageView와 같은 구조, #894)")]
    [SerializeField]
    private CanvasGroup m_group;

    // 마지막으로 표시한 초 — 초가 바뀔 때만 문자열을 다시 만들어 매 프레임 GC 할당을 피한다
    private int m_lastShownSeconds = int.MinValue;

    private void Awake()
    {
        if (m_timerSync == null)
            m_timerSync = FindFirstObjectByType<RoundTimerSync>();

        if (m_timerText == null)
            Debug.LogWarning("RoundTimerUI: 타이머 텍스트가 연결되지 않아 표시할 수 없다", this);

        SetVisible(false);
    }

    private void Update()
    {
        if (m_timerText == null)
            return;

        if (!TryGetRemainingSeconds(out float remaining))
        {
            SetVisible(false);
            m_lastShownSeconds = int.MinValue;
            return;
        }

        SetVisible(true);

        // Ceil — 시작 직후 3:00부터 보이고, 정확히 다 됐을 때만 0:00이 된다
        int shown = Mathf.CeilToInt(remaining);
        if (shown == m_lastShownSeconds)
            return;

        m_lastShownSeconds = shown;
        m_timerText.text = $"{shown / 60:00}:{shown % 60:00}";
    }

    // 남은 시간을 구한다 — 온라인은 동기화 컴포넌트, 오프라인은 RoundManager 직접.
    // 폴백은 권위 피어(서버·오프라인)에서만 탄다 — 클라의 Phase·RemainingSeconds는 대입되지 않는 값이라
    // 읽어봐야 항상 "미시작"이고, 읽는 것 자체가 Phase의 서버 전용 경고를 매 프레임 밟는다.
    private bool TryGetRemainingSeconds(out float seconds)
    {
        if (m_timerSync != null && m_timerSync.TryGetRemainingSeconds(out seconds))
            return true;

        seconds = 0f;
        if (
            Round == null
            || !Round.IsPhaseAuthority
            || Round.Phase != RoundPhase.InProgress
            || float.IsPositiveInfinity(Round.RemainingSeconds)
        )
            return false;

        seconds = Mathf.Max(0f, Round.RemainingSeconds);
        return true;
    }

    // 루트 오브젝트를 껐다 켠다 — 판까지 같이 사라져야 한다. 이 스크립트는 그 루트 밖(HUD 루트)에
    // 있어야 자기 Update가 멈추지 않는다 (LocalizedMessageView.SetVisible과 같은 방침).
    private void SetVisible(bool visible)
    {
        if (m_group == null)
            return;

        if (m_group.gameObject.activeSelf != visible)
            m_group.gameObject.SetActive(visible);
    }
}
