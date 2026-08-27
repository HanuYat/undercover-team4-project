using TMPro;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 전원 준비 대기 표시 (#410) — 아직 준비를 보고하지 않은 동료가 있는 동안 "대기 중 (2/4)"를 띄운다.
///
/// 로딩 화면 뒤에서 다 같이 기다리지 않고 준비된 사람부터 씬에 들어오게 한 대신(InGameManager),
/// "왜 아직 라운드가 시작되지 않는가"를 이 표시가 설명한다. 서버가 SceneReadyGate를 열면
/// (전원 보고 또는 타임아웃) 사라지고, 그 뒤 라운드 시작 지연이 흐른다.
///
/// 표시 전용 — 인원 수는 SceneReadyGate가 서버 권위로 동기화한 값이라 클라이언트에서도 그대로 읽는다.
/// (RoundFundBoard와 같은 구조)
/// </summary>
public class ReadyWaitHud : MonoBehaviour
{
    private SceneReadyGate Gate => App.Game.ReadyGate;

    [Header("표시")]
    [Tooltip("대기 상태를 표시할 TextMeshProUGUI")]
    [SerializeField]
    private TextMeshProUGUI m_waitText;

    [Tooltip("표시 루트 — 판·테두리·문구를 함께 켜고 끈다 (LocalizedMessageView와 같은 구조, #894)")]
    [SerializeField]
    private CanvasGroup m_group;

    // 코드가 대입하는 자리라 라벨에 LocalizeStringEvent를 붙일 수 없다 — 서로 덮어쓴다. (#497)
    [Tooltip("표시 형식 — Hud.Ready.Waiting ({0}=준비된 인원, {1}=전체 인원)")]
    [SerializeField]
    private LocalizedString m_waitFormat;

    // 마지막으로 표시한 값 — 바뀔 때만 문자열을 다시 만들어 불필요한 GC 할당을 피한다
    private int m_lastReady = int.MinValue;
    private int m_lastExpected = int.MinValue;

    private bool m_bound;

    private void OnEnable()
    {
        if (m_waitText == null)
        {
            Debug.LogWarning("[ReadyWaitHud] 대기 텍스트가 연결되지 않아 표시할 수 없다.", this);
            return;
        }

        SetVisible(false); // 게이트 상태를 확인하기 전에는 띄우지 않는다.
    }

    private void OnDisable()
    {
        // 꺼진 HUD가 언어 변경에 반응하지 않게 — 다시 켜질 때 값이 바뀌면 Update가 다시 건다
        Unbind();
        m_lastReady = int.MinValue;
        m_lastExpected = int.MinValue;
    }

    // 매 프레임 폴링 — 게이트 값은 NetworkVariable이고 변경 이벤트를 따로 열어 두지 않았다.
    // 대기 구간은 길어야 몇 초이고, 값이 바뀔 때만 문자열을 다시 만든다.
    private void Update()
    {
        SceneReadyGate gate = Gate;

        // 게이트가 없거나(오프라인·씬 직접 Play) 이미 열렸으면 표시할 것이 없다.
        // ExpectedCount 0은 아직 초기 동기화 전이라는 뜻이라 "(0/0)"을 띄우지 않는다.
        if (gate == null || !gate.IsSpawned || gate.IsOpen || gate.ExpectedCount <= 0)
        {
            SetVisible(false);
            return;
        }

        SetVisible(true);

        int ready = gate.ReadyCount;
        int expected = gate.ExpectedCount;
        if (ready == m_lastReady && expected == m_lastExpected)
            return; // 값이 바뀌지 않았다

        m_lastReady = ready;
        m_lastExpected = expected;
        Bind(ready, expected);
    }

    // 인원이 바뀔 때마다 인자를 갈아끼우고 다시 구독한다 — 인자를 먼저 넣어야 구독 시점의
    // 첫 발화부터 숫자가 들어간 문장이 나온다 (SessionCodePanel과 같은 관례).
    private void Bind(int ready, int expected)
    {
        if (m_waitFormat == null || m_waitFormat.IsEmpty)
        {
            Debug.LogWarning("[ReadyWaitHud] 대기 문구가 연결되지 않았다.", this);
            return;
        }

        Unbind();

        m_waitFormat.Arguments = new object[] { ready, expected };
        m_waitFormat.StringChanged += HandleStringChanged;
        m_bound = true;
    }

    private void HandleStringChanged(string localized)
    {
        if (m_waitText != null)
            m_waitText.text = localized;
    }

    private void Unbind()
    {
        if (!m_bound)
            return;

        m_waitFormat.StringChanged -= HandleStringChanged;
        m_bound = false;
    }

    // 루트 오브젝트를 껐다 켠다 — 판까지 같이 사라져야 한다. 이 컴포넌트는 HUD 루트에 있어
    // 이 토글에 영향받지 않는다 (LocalizedMessageView.SetVisible과 같은 방침).
    private void SetVisible(bool visible)
    {
        if (m_group == null)
            return;

        if (m_group.gameObject.activeSelf != visible)
            m_group.gameObject.SetActive(visible);
    }
}
