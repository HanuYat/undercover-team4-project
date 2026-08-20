using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.UI;

/// <summary>
/// 복구 단말의 화면 (#689) — 서버가 내린 코드를 띄우고 키보드로 받은 입력을 제출한다. 표시는 로컬이고
/// 정답 판정·해제는 서버가 한다(<see cref="BlackoutRecoveryTerminal.SubmitCode"/>).
///
/// <b>월드 공간 화면이다</b> — 모달로 띄우면 "컴퓨터 앞에 앉았다"가 아니라 "허공에 창이 떴다"가 된다.
/// <b>입력은 숫자 키다</b> — 마우스로 누르려면 커서를 풀어야 하는데, 커서가 풀리면
/// <see cref="PlayerInteractor.HandleInteract"/>가 E를 막아(#352) 화면에서 나갈 수단이 사라진다.
/// </summary>
public class BlackoutTerminalScreen : MonoBehaviour
{
    private const string k_table = "HudTable";

    // 인덱스가 곧 숫자다. 위쪽 숫자열과 넘패드를 함께 받는다.
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

    // 화면이 한 종류뿐이라 인스펙터 배선 대신 코드에 둔다 (InteractPrompts와 같은 방침).
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

    // 제출한 네 자리를 화면에 남겨 둔 상태 (#762). 제출이 서버 빈도 제한
    // (BlackoutRecoveryTerminal.k_submitCooldown)에 버려지면 코드가 안 바뀌어 입력이 4자리로
    // 잠겼다 — 서버 응답 대신 이 플래그로 다음 입력을 받는다.
    private bool m_submitted;

    private void Awake()
    {
        if (m_terminal == null)
            m_terminal = GetComponentInParent<BlackoutRecoveryTerminal>();
    }

    private void OnEnable()
    {
        m_entry.Clear(); // 화면이 켜지는 순간이 곧 해킹 시작이다

        if (m_terminal != null)
            m_terminal.OnCodeChanged += HandleCodeChanged;

        // 화면은 해킹 내내 켜져 있어 한 번 읽고 끝내면 그 라운드 동안 이전 언어로 남는다.
        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;
        ApplyHint();

        Redraw();
    }

    private void OnDisable()
    {
        LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;

        // ?. 금지 — 파괴된 Unity 오브젝트의 fake null을 우회하지 않게 한다 (HqPanelView 관례)
        if (m_terminal != null)
            m_terminal.OnCodeChanged -= HandleCodeChanged;
    }

    private void HandleLocaleChanged(UnityEngine.Localization.Locale locale) => ApplyHint();

    private void ApplyHint()
    {
        if (m_hintLabel != null)
            m_hintLabel.text = s_hint.GetLocalizedString();
    }

    private void Update()
    {
        UpdateTimerBar();

        // 화면은 전 피어에서 켜져 있다 — 게이트가 없으면 본부에 있지도 않은 사람의 키가 들어간다.
        if (m_terminal == null || !m_terminal.IsLocalFocused || !m_terminal.IsOnline)
            return;

        ReadKeyboard();
    }

    private void UpdateTimerBar()
    {
        if (m_timerBar == null || m_terminal == null)
            return;

        float total = m_terminal.CodeSeconds;
        m_timerBar.fillAmount = total > 0f ? Mathf.Clamp01(m_terminal.RemainingSeconds / total) : 0f;
    }

    private void ReadKeyboard()
    {
        Keyboard keyboard = Keyboard.current; // 없는 구성(원격 데스크톱 등)에서는 조용히 넘어간다
        if (keyboard == null)
            return;

        for (int digit = 0; digit < s_digitRow.Length; digit++)
        {
            if (keyboard[s_digitRow[digit]].wasPressedThisFrame || keyboard[s_numpad[digit]].wasPressedThisFrame)
                Press(digit);
        }

        // 전부 비우는 수단은 두지 않는다 — 제한시간이 지나면 어차피 초기화된다.
        if (keyboard[Key.Backspace].wasPressedThisFrame)
            Erase();
    }

    // 코드가 새로 뽑히면 입력도 비운다 — 이어서 누르면 섞인다.
    private void HandleCodeChanged(int code)
    {
        ClearEntry();
        Redraw();
    }

    private void Press(int digit)
    {
        if (m_submitted)
            ClearEntry(); // 제출 뒤 첫 입력 — 새 시도로 시작한다

        if (m_entry.Count >= BlackoutRecoveryTerminal.k_codeDigits)
            return; // 안전망 — 네 자리를 채우면 곧바로 제출한다

        m_entry.Add(digit);
        Redraw();

        // 자릿수를 채우면 곧바로 제출한다 — 확인 키는 누를 것만 하나 더 는다.
        if (m_entry.Count == BlackoutRecoveryTerminal.k_codeDigits)
            Submit();
    }

    private void Erase()
    {
        // 제출한 것에 Backspace = 다시 넣겠다는 뜻 — 꼬리만 지워 이어 쓰게 두지 않는다
        if (m_submitted)
        {
            ClearEntry();
            Redraw();
            return;
        }

        if (m_entry.Count == 0)
            return;

        m_entry.RemoveAt(m_entry.Count - 1);
        Redraw();
    }

    private void ClearEntry()
    {
        m_entry.Clear();
        m_submitted = false;
    }

    private void Submit()
    {
        int value = 0;
        for (int i = 0; i < m_entry.Count; i++)
            value = value * 10 + m_entry[i];

        // 결과는 여기서 판단하지 않는다 — 맞으면 화면이 꺼지고 틀리면 코드가 새로 뽑혀 다시 그려진다.
        m_submitted = true;
        m_terminal.SubmitCode(value);
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

    // "42"가 아니라 "0042"여야 네 자리를 누르는 것이 자명하다.
    private static string FormatCode(int code)
    {
        if (code < 0)
            return new string('-', BlackoutRecoveryTerminal.k_codeDigits);

        return code.ToString().PadLeft(BlackoutRecoveryTerminal.k_codeDigits, '0');
    }
}
