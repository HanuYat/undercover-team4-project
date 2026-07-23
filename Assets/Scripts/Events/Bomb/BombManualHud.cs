using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// [임시] 폭탄 해체 매뉴얼의 화면 표현 — 본부가 상호작용하면 현재 폭탄의 규칙표를 펼친다. (#232)
/// 상태·규칙은 <see cref="BombManual"/>이 들고(규칙은 <see cref="BombDevice.Active"/>의 퍼즐에서 읽음),
/// 이 컴포넌트는 표시만 한다 (SignalDecoderHud ↔ SignalDecoder와 같은 역할 분리).
///
/// PlayerReviveHud·SignalDecoderHud의 임시 OnGUI 관례를 따른다 — 정식 UI(#65 계열)로 대체 예정.
/// 여는 동안은 타이핑/이동이 섞이지 않도록 게임플레이 입력을 정지하고 커서를 푼다(SignalDecoderHud와 동일).
/// </summary>
public class BombManualHud : MonoBehaviour
{
    private const int k_drawDepth = -100; // 먹통 오버레이 위에 그린다 (SignalDecoderHud와 동일)

    private BombManual m_manual;
    private PlayerInputHandler m_input;
    private PlayerMovement m_movement;
    private bool m_cursorUnlockedBeforeOpen;
    private bool m_isOpen;

    private GUIStyle m_titleStyle;
    private GUIStyle m_ruleStyle;
    private GUIStyle m_hintStyle;

    /// <summary>매뉴얼이 펼쳐져 있는지 — 열린 동안 이 플레이어의 게임플레이 입력은 정지된다.</summary>
    public bool IsOpen => m_isOpen;

    /// <summary>매뉴얼을 펼친다. 상호작용한 본인의 클라이언트에서만 호출된다(BombManual.Interact 참고).</summary>
    public void Open(BombManual manual, GameObject interactor)
    {
        if (m_isOpen || interactor == null)
            return;

        m_manual = manual;
        m_input = interactor.GetComponent<PlayerInputHandler>();
        m_movement = interactor.GetComponent<PlayerMovement>();

        m_isOpen = true;
        m_input?.SetSuspended(true);

        if (m_movement != null)
        {
            m_cursorUnlockedBeforeOpen = Cursor.lockState == CursorLockMode.None;
            m_movement.SetCursorUnlocked(true);
        }
    }

    /// <summary>매뉴얼을 덮고 입력·커서를 원래대로 되돌린다.</summary>
    public void Close()
    {
        if (!m_isOpen)
            return;

        m_isOpen = false;

        // ?. 금지 — 파괴된 Unity 오브젝트의 fake null을 우회하지 않도록 명시 비교 (SignalDecoderHud와 동일)
        if (m_input != null)
            m_input.SetSuspended(false);
        if (m_movement != null)
            m_movement.SetCursorUnlocked(m_cursorUnlockedBeforeOpen);

        m_input = null;
        m_movement = null;
    }

    private void OnDisable()
    {
        Close(); // 열린 채 파괴·비활성되면 정지된 입력을 여기서 되돌린다
    }

    private void Update()
    {
        if (!m_isOpen)
            return;

        // 열려 있는 동안 ESC 진입 메뉴(일시정지) 오픈을 막는다 — 닫힘 프레임의 파이프라인 스큐까지 덮는다 (#326)
        EscMenuGuard.BlockThisFrame();

        // 연 플레이어가 디스폰되면 되돌릴 대상이 사라진다 — 즉시 닫아 입력이 잠긴 채 남지 않게
        if (m_input == null)
            Close();
    }

    private void OnGUI()
    {
        if (!m_isOpen)
            return;

        EnsureStyles();

        // 닫기는 Esc 전용 — 여는 키(E)를 닫기로도 읽으면 여는 그 입력에 같은 프레임에서 닫혀 UI가 안 뜬다.
        // (SignalDecoderHud도 같은 이유로 닫기를 Esc/Enter만 쓴다)
        Event current = Event.current;
        bool close = false;
        if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
        {
            close = true;
            current.Use();
        }

        int previousDepth = GUI.depth;
        GUI.depth = k_drawDepth;
        Draw();
        GUI.depth = previousDepth;

        if (close)
            Close();
    }

    private void Draw()
    {
        List<string> lines = m_manual != null ? m_manual.GetRuleLines() : null;
        int ruleCount = lines != null ? lines.Count : 1;

        const float width = 580f;
        float height = 96f + ruleCount * 30f;
        Rect box = new Rect(40f, (Screen.height - height) * 0.5f, width, height);

        GUI.Box(box, GUIContent.none);
        GUI.Label(new Rect(box.x + 16f, box.y + 12f, width - 32f, 30f), "폭탄 해체 매뉴얼", m_titleStyle);

        if (lines == null || lines.Count == 0)
        {
            GUI.Label(new Rect(box.x + 16f, box.y + 52f, width - 32f, 26f), "현재 활성 폭탄이 없습니다.", m_ruleStyle);
        }
        else
        {
            for (int i = 0; i < lines.Count; i++)
                GUI.Label(new Rect(box.x + 16f, box.y + 52f + i * 30f, width - 32f, 28f), $"{i + 1}. {lines[i]}", m_ruleStyle);
        }

        GUI.Label(new Rect(box.x + 16f, box.y + height - 26f, width - 32f, 22f), "Esc — 닫기", m_hintStyle);
    }

    private void EnsureStyles()
    {
        if (m_titleStyle == null)
        {
            m_titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
            };
            m_titleStyle.normal.textColor = new Color(1f, 0.85f, 0.3f);
        }

        if (m_ruleStyle == null)
        {
            m_ruleStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
            m_ruleStyle.normal.textColor = new Color(0.95f, 0.95f, 0.95f);
        }

        if (m_hintStyle == null)
        {
            m_hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            m_hintStyle.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
        }
    }
}
