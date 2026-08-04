using TMPro;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 신호 해석기 입력창 — 전원에게 보낼 메시지를 타이핑하는 모달. (#108 → #493)
/// 상호작용한 본인의 클라이언트에서만 열린다(<see cref="SignalDecoder.Interact"/>가 오너 경로에서 호출).
///
/// <b>IMGUI에서 옮겨온 것</b>: 예전에는 <c>GUI.TextField</c>로 그렸고, Enter·Esc를 입력창을 그리기
/// "전"에 가로채야 했다(포커스를 가진 TextField가 키를 먹어버려서). 또 IMGUI와 Input System의 프레임
/// 스큐 때문에 Esc로 닫는 순간 일시정지 메뉴가 겹쳐 뜨는 것을 <see cref="EscMenuGuard"/>로 막아야 했다.
/// <see cref="PanelBase"/>의 ESC 스택에 올라오면서 둘 다 필요 없어졌다 — 스택 최상단이 ESC를
/// 소비하고 진입 메뉴로 넘기지 않는다(UIManagerBase.Update).
///
/// 입력 정지·커서 해제는 <see cref="PanelBase"/>가 다루지 않으므로 여기서 직접 한다
/// (<see cref="HqPanelView"/>와 같은 방침).
/// </summary>
public class SignalInputPanel : PanelBase
{
    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true; // 창처럼 겹치는 모달

    [Header("입력")]
    [SerializeField]
    private TMP_InputField m_field;

    [Header("문구")]
    [Tooltip("창 제목 — WorldTable/World.SignalDecoder.InputTitle")]
    [SerializeField]
    private LocalizedString m_title;

    [SerializeField]
    private TMP_Text m_titleText;

    [Tooltip("조작 안내 — WorldTable/World.SignalDecoder.InputHint ({0}=입력 길이, {1}=최대 길이)")]
    [SerializeField]
    private LocalizedString m_hint;

    [SerializeField]
    private TMP_Text m_hintText;

    private SignalDecoder m_decoder;
    private PlayerInputHandler m_input; // 창을 연 플레이어의 입력 — 닫을 때 되돌린다

    // 커서 Push/Pop 짝을 지키는 래치. PanelBase.OpenPanel/ClosePanel엔 재진입 가드가 없어
    // 같은 값으로 두 번 불릴 수 있는데, 그때 Push만 두 번(또는 Pop만 두 번) 들어가면
    // 전역 요청 수가 어긋나 커서가 영영 풀리거나 영영 잠긴다. (PausePanel·SettlementPanel과 같은 방침, #352)
    private bool m_blocked;

    protected override void Awake()
    {
        base.Awake();

        if (m_field != null)
        {
            // 한 줄 입력이라 Enter가 줄바꿈이 아니라 전송이 된다
            m_field.lineType = TMP_InputField.LineType.SingleLine;
            m_field.characterLimit = SignalDecoder.k_maxMessageLength;
            m_field.onSubmit.AddListener(HandleSubmit);
            m_field.onValueChanged.AddListener(HandleValueChanged);
        }

        m_title.StringChanged += HandleTitleChanged;

        // [순서 주의] 인자를 먼저 넣고 구독한다. StringChanged 구독은 즉시 1회 해석을 일으키는데,
        // 그 시점에 Arguments가 없으면 Smart String이 {0}/{1}을 채우지 못해 FormattingException이 난다.
        ApplyHintArguments(0);
        m_hint.StringChanged += HandleHintChanged;
    }

    protected override void OnDestroy()
    {
        if (m_field != null)
        {
            m_field.onSubmit.RemoveListener(HandleSubmit);
            m_field.onValueChanged.RemoveListener(HandleValueChanged);
        }

        m_title.StringChanged -= HandleTitleChanged;
        m_hint.StringChanged -= HandleHintChanged;

        base.OnDestroy();
    }

    /// <summary>입력창을 연다 — 상호작용한 본인 클라이언트에서만 호출된다.</summary>
    public void Open(SignalDecoder decoder, GameObject interactor)
    {
        if (IsOpened || decoder == null || interactor == null)
            return;

        m_decoder = decoder;
        m_input = interactor.GetComponent<PlayerInputHandler>();

        if (m_field != null)
            m_field.SetTextWithoutNotify(string.Empty);
        RefreshHint(0);

        SetBlocked(true);
        OpenPanel();

        // 활성화된 다음에 포커스를 준다 — 꺼진 오브젝트의 InputField는 포커스를 받지 못한다
        if (m_field != null)
            m_field.ActivateInputField();
    }

    /// <summary>
    /// 닫기 — ESC 스택·전송 완료·씬 정리가 모두 여기로 모인다.
    /// 정지시킨 입력과 커서를 반드시 여기서 되돌린다.
    /// </summary>
    public override void ClosePanel()
    {
        if (!IsOpened)
            return; // 중복 호출로 CursorLock 참조 수가 어긋나지 않게

        base.ClosePanel();

        if (m_field != null)
        {
            m_field.DeactivateInputField();
            m_field.SetTextWithoutNotify(string.Empty);
        }

        SetBlocked(false);

        m_input = null;
        m_decoder = null;
    }

    /// <summary>
    /// 이 컴포넌트가 파괴·비활성되는 마지막 순간의 안전망 — 씬 전환이 대표적이다.
    /// HUD.prefab은 씬마다 새로 생기고 사라지는데, 창이 열린 채 사라지면 정지시킨 입력과
    /// 커서 해제 요청을 되돌릴 주체가 없어진다. 커서가 풀린 채 굳거나(요청이 남아서)
    /// 입력이 잠긴 채 남는다. (구 SignalDecoderHud.OnDisable이 하던 역할)
    /// </summary>
    private void OnDisable()
    {
        ClosePanel();

        // 창이 이미 닫힌 뒤라도 래치가 켜져 있으면 짝이 안 맞은 것이다 — 여기서 확실히 거둔다.
        SetBlocked(false);
    }

    // 커서 해제·입력 정지를 한 쌍으로 묶는다. 래치 덕에 몇 번 불려도 Push/Pop은 1:1로 유지된다.
    private void SetBlocked(bool blocked)
    {
        if (m_blocked == blocked)
            return;

        m_blocked = blocked;

        // 플레이어 조회보다 먼저 — 플레이어가 도중에 사라져도 Push/Pop 짝은 유지돼야 한다 (#352)
        if (blocked)
            CursorLock.PushUnlock();
        else
            CursorLock.PopUnlock();

        // 타이핑 중 WASD가 이동으로 새는 것을 막는다 — 인벤토리 편집 모드의 "이동하면 닫기" 방식은
        // 타이핑에 쓸 수 없어 입력 정지를 따로 쓴다 (PlayerInputHandler.SetSuspended).
        // ?. 금지 — Unity 오브젝트의 ?.는 C# 참조 null만 보고 파괴 판정(fake null)을 우회한다.
        if (m_input != null)
            m_input.SetSuspended(blocked);
    }

    // 창을 연 플레이어가 디스폰(퇴장·씬 전환)되면 정지된 입력을 되돌릴 대상이 사라진다 —
    // 참조가 죽으면 즉시 닫아 입력이 잠긴 채 남지 않게 한다.
    private void Update()
    {
        if (IsOpened && m_input == null)
            ClosePanel();
    }

    // Enter — 먼저 닫는다. 전송이 실패해도 입력이 정지된 채 남지 않게.
    private void HandleSubmit(string text)
    {
        SignalDecoder decoder = m_decoder;
        ClosePanel();

        if (decoder != null)
            decoder.SendSignal(text);
    }

    private void HandleValueChanged(string text) => RefreshHint(text != null ? text.Length : 0);

    // 남은 글자 수는 인자로 넣는다 — 서버가 어차피 자르지만, 입력 단계에서 보여야
    // "쳤는데 잘렸다"가 안 생긴다.
    private void ApplyHintArguments(int length) =>
        m_hint.Arguments = new object[] { length, SignalDecoder.k_maxMessageLength };

    private void RefreshHint(int length)
    {
        ApplyHintArguments(length);
        m_hint.RefreshString(); // 이미 구독 중이므로 다시 포맷만 시킨다
    }

    private void HandleTitleChanged(string localized)
    {
        if (m_titleText != null)
            m_titleText.text = localized;
    }

    private void HandleHintChanged(string localized)
    {
        if (m_hintText != null)
            m_hintText.text = localized;
    }
}
