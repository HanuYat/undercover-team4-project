using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Localization;
using UnityEngine.UI;

/// <summary>
/// 복구 단말의 화면 (#689) — 서버가 내린 복구 코드를 띄우고, 키보드로 받은 입력을 단말에 제출한다.
///
/// <b>월드 공간 화면이다</b> — 전체화면 모달(<see cref="PanelBase"/>)이 아니다. 카메라가 실제로 화면
/// 앞으로 옮겨 오므로(<see cref="PlayerTerminalFocus"/>) 모니터에 붙은 캔버스가 그대로 크게 보인다.
/// 모달로 띄우면 "컴퓨터 앞에 앉았다"가 아니라 "허공에 창이 떴다"가 된다.
///
/// <b>입력은 숫자 키다</b> — 화면 안 키패드를 마우스로 누르는 방식이었는데, 그러려면 커서를 풀어야
/// 하고 커서가 풀리면 <see cref="PlayerInteractor.HandleInteract"/>가 E를 통째로 막는다(#352).
/// 그러면 화면에서 나갈 수단이 화면 안 버튼밖에 남지 않아, 그 버튼이 죽으면 갇힌다. 숫자 키로 받으면
/// 커서를 잠근 채로 둘 수 있어 <b>E가 그대로 나가기</b>가 된다 — 탈출구가 하나 더 늘어난 것이 아니라
/// 다른 상호작용과 같은 규칙으로 돌아온 것이다.
///
/// 순수 로컬 표시다 — 입력은 각자 화면에서 받고, 정답 판정과 해제는 서버가 한다
/// (<see cref="BlackoutRecoveryTerminal.SubmitCode"/>).
/// </summary>
public class BlackoutTerminalScreen : MonoBehaviour
{
    private const string k_table = "HudTable";

    // 인덱스가 곧 숫자다. 위쪽 숫자열과 넘패드를 함께 받는다 — 어느 쪽을 누르든 같은 값이어야 한다.
    private static readonly Key[] s_digitRow =
    {
        Key.Digit0, Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4,
        Key.Digit5, Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9,
    };

    private static readonly Key[] s_numpad =
    {
        Key.Numpad0, Key.Numpad1, Key.Numpad2, Key.Numpad3, Key.Numpad4,
        Key.Numpad5, Key.Numpad6, Key.Numpad7, Key.Numpad8, Key.Numpad9,
    };

    // 안내 문구는 인스펙터 배선 대신 코드에 둔다 — 화면이 한 종류뿐이고 문구를 고치려고
    // 프리팹을 열 이유가 없다 (<see cref="InteractPrompts"/>와 같은 방침).
    private static readonly LocalizedString s_hint = new LocalizedString(k_table, "Hud.Terminal.HackHint");

    [Header("연결")]
    [Tooltip("비우면 부모에서 찾는다 — 프리팹 안에서 쓰는 것이 기본이라 대개 비워 둔다")]
    [SerializeField] private BlackoutRecoveryTerminal m_terminal;

    [Header("표시")]
    [Tooltip("서버가 내린 복구 코드를 그대로 띄운다")]
    [SerializeField] private TextMeshProUGUI m_codeLabel;

    [Tooltip("지금까지 누른 숫자")]
    [SerializeField] private TextMeshProUGUI m_entryLabel;

    [Tooltip("본부가 무엇을 해야 하는지 — 코드는 몰라도 이 줄만 보면 알 수 있어야 한다")]
    [SerializeField] private TextMeshProUGUI m_hintLabel;

    [Tooltip("남은 시간 게이지 — 0이 되면 서버가 코드를 새로 뽑는다 (Filled 이미지)")]
    [SerializeField] private Image m_timerBar;

    private readonly List<int> m_entry = new List<int>();

    private void Awake()
    {
        if (m_terminal == null)
            m_terminal = GetComponentInParent<BlackoutRecoveryTerminal>();
    }

    private void OnEnable()
    {
        // 화면이 켜지는 순간이 곧 해킹 시작이다 — 이전 시도의 입력이 남아 있으면 안 된다.
        m_entry.Clear();

        if (m_terminal != null)
            m_terminal.OnCodeChanged += HandleCodeChanged;

        if (m_hintLabel != null)
            m_hintLabel.text = s_hint.GetLocalizedString();

        Redraw();
    }

    private void OnDisable()
    {
        // ?. 금지 — 파괴된 Unity 오브젝트의 fake null을 우회하지 않게 한다 (HqPanelView 관례)
        if (m_terminal != null)
            m_terminal.OnCodeChanged -= HandleCodeChanged;
    }

    private void Update()
    {
        UpdateTimerBar();

        // 입력은 이 단말을 보고 있는 로컬 플레이어만 한다 — 화면 자체는 전 피어에서 켜져 있으므로
        // 이 게이트가 없으면 본부에 있지도 않은 사람의 키 입력이 코드를 밀어 넣는다.
        if (m_terminal == null || !m_terminal.IsLocalFocused || !m_terminal.IsOnline)
            return;

        ReadKeyboard();
    }

    // 남은 시간은 서버 시각에서 파생되므로 어느 피어에서 봐도 같은 값이다.
    private void UpdateTimerBar()
    {
        if (m_timerBar == null || m_terminal == null)
            return;

        float total = m_terminal.CodeSeconds;
        m_timerBar.fillAmount = total > 0f ? Mathf.Clamp01(m_terminal.RemainingSeconds / total) : 0f;
    }

    private void ReadKeyboard()
    {
        // 키보드가 없는 구성(원격 데스크톱 등)에서는 조용히 넘어간다 (SuddenEventDevHotkeys 관례)
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        for (int digit = 0; digit < s_digitRow.Length; digit++)
        {
            if (keyboard[s_digitRow[digit]].wasPressedThisFrame || keyboard[s_numpad[digit]].wasPressedThisFrame)
                Press(digit);
        }

        // 한 자리 지우기 — 전부 비우는 수단은 따로 두지 않는다. 제한시간이 지나면 어차피 초기화된다.
        if (keyboard[Key.Backspace].wasPressedThisFrame)
            Erase();
    }

    // 코드가 새로 뽑히면(발급·오입력·제한시간 만료) 입력도 비운다 — 이어서 누르면 섞인다.
    private void HandleCodeChanged(int code)
    {
        m_entry.Clear();
        Redraw();
    }

    private void Press(int digit)
    {
        if (m_entry.Count >= BlackoutRecoveryTerminal.k_codeDigits)
            return;

        m_entry.Add(digit);
        Redraw();

        // 자릿수를 채우면 곧바로 제출한다 — 확인 키를 따로 두면 누를 것이 하나 더 늘 뿐이다.
        if (m_entry.Count == BlackoutRecoveryTerminal.k_codeDigits)
            Submit();
    }

    private void Erase()
    {
        if (m_entry.Count == 0)
            return;

        m_entry.RemoveAt(m_entry.Count - 1);
        Redraw();
    }

    private void Submit()
    {
        int value = 0;
        for (int i = 0; i < m_entry.Count; i++)
            value = value * 10 + m_entry[i];

        m_terminal.SubmitCode(value);

        // 결과를 여기서 판단하지 않는다 — 맞았으면 해킹이 풀려 화면이 꺼지고, 틀렸으면 서버가 코드를
        // 새로 뽑아 HandleCodeChanged가 입력을 비운다. 어느 쪽이든 상태 변화가 화면을 다시 그린다.
    }

    private void Redraw()
    {
        if (m_codeLabel != null)
            m_codeLabel.text = FormatCode(m_terminal != null ? m_terminal.Code : -1);

        if (m_entryLabel == null)
            return;

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < BlackoutRecoveryTerminal.k_codeDigits; i++)
            sb.Append(i < m_entry.Count ? m_entry[i].ToString() : "_");

        m_entryLabel.text = sb.ToString();
    }

    // 코드는 자릿수를 채워 보여 준다 — "42"가 아니라 "0042"여야 네 자리를 누르는 것이 자명하다.
    private static string FormatCode(int code)
    {
        if (code < 0)
            return new string('-', BlackoutRecoveryTerminal.k_codeDigits);

        return code.ToString().PadLeft(BlackoutRecoveryTerminal.k_codeDigits, '0');
    }
}
