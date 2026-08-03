using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// [임시] 신호 해석기의 입력창. (#108)
/// 상태는 <see cref="SignalDecoder"/>가 들고, 이 컴포넌트는 표시만 담당한다
/// (DeviceBlackoutEvent ↔ DeviceBlackoutView와 같은 역할 분리).
/// 수신 메시지 표시는 HUD 공용 토스트(<see cref="ToastView"/>)로 넘겼고, 입력창만 IMGUI로 남아 있다 —
/// TMP_InputField 전환은 후속 (#493).
///
/// <b>예전에 IMGUI를 강제하던 제약은 사라졌다</b>: 먹통 오버레이가 uGUI 캔버스를 덮기 때문에
/// 같은 IMGUI로 그려 GUI.depth로 위에 올려야 했는데, 그 화면 덮기는 #434에서 제거됐다
/// (<see cref="DeviceBlackoutView"/>는 이제 음성 왜곡만 한다). 캔버스로 옮겨도 가려지지 않는다.
/// </summary>
public class SignalDecoderHud : MonoBehaviour
{
    [Header("수신 표시")]
    [Tooltip("받은 메시지를 화면에 유지하는 시간(초)")]
    [SerializeField]
    private float m_displaySeconds = 8f;

    [Tooltip("수신 문구 형식 — UITable/signal.received ({0}에 받은 메시지가 들어간다)")]
    [SerializeField]
    private LocalizedString m_receivedFormat;

    private SignalDecoder m_decoder;
    private PlayerInputHandler m_input;   // 입력창을 연 플레이어의 입력 — 닫을 때 되돌린다

    private bool m_isOpen;
    private string m_draft = string.Empty;
    private bool m_focusRequested;

    private GUIStyle m_hintStyle;

    /// <summary>입력창이 열려 있는지 — 열려 있는 동안 이 플레이어의 게임플레이 입력은 정지된다.</summary>
    public bool IsOpen => m_isOpen;

    /// <summary>
    /// 입력창을 연다. 상호작용한 본인의 클라이언트에서만 호출된다(SignalDecoder.Interact 참고).
    /// 타이핑이 이동으로 새지 않도록 게임플레이 입력을 정지하고 커서를 푼다.
    /// </summary>
    public void Open(SignalDecoder decoder, GameObject interactor)
    {
        if (m_isOpen || interactor == null)
            return;

        m_decoder = decoder;
        m_input = interactor.GetComponent<PlayerInputHandler>();

        m_isOpen = true;
        m_draft = string.Empty;
        m_focusRequested = true;

        // 타이핑 중 WASD가 이동으로 새는 것을 막는다 — 인벤토리 편집 모드의 "이동하면 닫기" 방식은
        // 타이핑에 쓸 수 없어 입력 정지를 따로 만들었다 (PlayerInputHandler.SetSuspended).
        m_input?.SetSuspended(true);
        CursorLock.PushUnlock();
    }

    /// <summary>입력창을 닫고 입력·커서를 원래대로 되돌린다.</summary>
    public void Close()
    {
        if (!m_isOpen)
            return;

        m_isOpen = false;
        m_draft = string.Empty;

        // ?. 금지 — Unity 오브젝트의 ?.는 C# 참조 null만 보고 파괴 판정(fake null)을 우회한다.
        // Update의 안전망이 발동하는 상황이 곧 m_input이 파괴된 상황이라, ?.면 파괴된 객체를 호출해 터진다.
        if (m_input != null)
            m_input.SetSuspended(false);
        CursorLock.PopUnlock();

        m_input = null;
    }

    // 입력창이 열린 채 이 HUD가 파괴·비활성되면(씬 전환, 향후 상점의 단말 회수 등) 정지된 입력을
    // 되돌릴 기회가 사라진다 — 마지막 기회로 여기서 닫는다. (Scanner.OnDisable의 채널링 취소 관례)
    private void OnDisable()
    {
        Close();
    }

    /// <summary>수신한 메시지를 화면에 띄운다. 전 피어에서 호출된다.</summary>
    public void ShowMessage(string message)
    {
        // 받은 본문은 플레이어가 친 글이라 번역 대상이 아니다 — 형식 문구만 테이블에서 오고
        // 본문은 Smart String 인자({0})로 끼워 넣는다. 인자를 먼저 넣어야 구독 시점의
        // 첫 발화부터 올바른 문장이 나온다.
        m_receivedFormat.Arguments = new object[] { message };
        App.UI.SignalMessage?.Show(m_receivedFormat, m_displaySeconds);
    }

    // 입력창을 연 플레이어가 디스폰(퇴장·씬 전환)되면 정지된 입력을 되돌릴 대상이 사라진다 —
    // 참조가 죽으면 즉시 닫아 입력이 잠긴 채 남지 않게 한다.
    private void Update()
    {
        if (!m_isOpen)
            return;

        // 열려 있는 동안 ESC 진입 메뉴(일시정지) 오픈을 막는다 — IMGUI↔Input System 프레임 스큐까지 덮는다 (#326)
        EscMenuGuard.BlockThisFrame();

        if (m_input == null)
            Close();
    }

    private void OnGUI()
    {
        if (!m_isOpen)
            return;

        EnsureStyles();
        DrawInput();
    }

    private void DrawInput()
    {
        // [중요] Enter/Esc는 TextField를 그리기 "전"에 읽어야 한다.
        // 포커스를 가진 GUI.TextField가 KeyDown을 소비해(Event.current.type이 Used로 바뀜)
        // 뒤에서 읽으면 두 키가 영원히 안 들어온다. 읽는 즉시 Use()로 가로채 TextField에 넘기지 않는다
        // (안 그러면 Enter가 텍스트에 섞이거나 Esc가 포커스만 풀고 창은 남는다).
        // 게임플레이 입력이 정지된 상태라 InputAction 경로로는 어차피 안 들어오므로 IMGUI 이벤트로 직접 받는다.
        Event current = Event.current;
        bool submit = false;
        bool cancel = false;

        if (current.type == EventType.KeyDown)
        {
            switch (current.keyCode)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    submit = true;
                    current.Use();
                    break;

                case KeyCode.Escape:
                    cancel = true;
                    current.Use();
                    break;
            }
        }

        const float width = 640f;
        const float height = 96f;
        Rect box = new Rect((Screen.width - width) * 0.5f, Screen.height * 0.6f, width, height);

        GUI.Box(box, "신호 해석기 — 전원에게 메시지 전송");

        Rect field = new Rect(box.x + 12f, box.y + 34f, box.width - 24f, 24f);
        GUI.SetNextControlName("SignalDecoderInput");

        // 서버가 어차피 자르지만, 입력 단계에서 막아야 "쳤는데 잘렸다"가 안 생긴다.
        m_draft = GUI.TextField(field, m_draft, SignalDecoder.k_maxMessageLength);

        GUI.Label(
            new Rect(box.x + 12f, box.y + 62f, box.width - 24f, 22f),
            $"Enter 전송 · Esc 취소   ({m_draft.Length}/{SignalDecoder.k_maxMessageLength})",
            m_hintStyle);

        // 연 직후 한 번만 포커스를 준다 — 매 프레임 주면 캐럿 이동·선택이 계속 초기화된다.
        if (m_focusRequested)
        {
            GUI.FocusControl("SignalDecoderInput");
            m_focusRequested = false;
        }

        // 상태 변경은 이번 프레임 그리기를 마친 뒤에 한다 — 그리는 도중 닫으면 남은 컨트롤이 사라진다.
        if (submit)
        {
            string message = m_draft;
            Close(); // 먼저 닫는다 — 전송이 실패해도 입력이 정지된 채 남지 않게
            m_decoder?.SendSignal(message);
        }
        else if (cancel)
        {
            Close();
        }
    }

    private void EnsureStyles()
    {
        if (m_hintStyle == null)
        {
            m_hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            m_hintStyle.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
        }
    }
}
