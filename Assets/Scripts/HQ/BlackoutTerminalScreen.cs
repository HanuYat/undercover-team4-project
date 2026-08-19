using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 복구 단말의 화면 (#689) — 서버가 내린 복구 코드를 띄우고, 키패드로 받은 입력을 단말에 제출한다.
///
/// <b>월드 공간 화면이다</b> — 전체화면 모달(<see cref="PanelBase"/>)이 아니다. 카메라가 실제로 화면
/// 앞으로 옮겨 오므로(<see cref="PlayerTerminalFocus"/>) 모니터에 붙은 캔버스가 그대로 크게 보인다.
/// 모달로 띄우면 "컴퓨터 앞에 앉았다"가 아니라 "허공에 창이 떴다"가 된다.
///
/// <b>키패드는 코드가 만든다</b> — 프리팹에 버튼 12개를 배선해 두면 배치를 바꿀 때마다 손으로 다시
/// 이어야 하고, 이 절차의 형태 자체가 아직 확정 전이다(이슈: "프로토타입 후 확정"). 지금은 컨테이너
/// 하나만 두고 자식을 만들어 넣는다 — 형태가 정해지면 그때 프리팹으로 굳히면 된다.
///
/// 순수 로컬 표시다 — 입력은 각자 화면에서 받고, 정답 판정과 해제는 서버가 한다
/// (<see cref="BlackoutRecoveryTerminal.SubmitCode"/>).
/// </summary>
public class BlackoutTerminalScreen : MonoBehaviour
{
    // 지우기·나가기가 차지하는 자리 — 숫자 10개 뒤에 붙는다.
    private const int k_digitCount = 10;

    [Header("연결")]
    [Tooltip("비우면 부모에서 찾는다 — 프리팹 안에서 쓰는 것이 기본이라 대개 비워 둔다")]
    [SerializeField] private BlackoutRecoveryTerminal m_terminal;

    [Header("표시")]
    [Tooltip("서버가 내린 복구 코드를 그대로 띄운다")]
    [SerializeField] private TextMeshProUGUI m_codeLabel;

    [Tooltip("지금까지 누른 숫자")]
    [SerializeField] private TextMeshProUGUI m_entryLabel;

    [Tooltip("키패드 버튼이 생성될 자리 — GridLayoutGroup을 붙여 두면 배치가 자동으로 잡힌다")]
    [SerializeField] private RectTransform m_keypadRoot;

    [Tooltip("버튼 하나의 글자 크기")]
    [SerializeField] private float m_buttonFontSize = 48f;

    [Tooltip("버튼 바탕색 — 기본 Image는 흰색이라 그냥 두면 흰 바탕에 흰 글자가 된다")]
    [SerializeField] private Color m_buttonColor = new Color(0.12f, 0.18f, 0.22f, 1f);

    [Tooltip("버튼 글자색")]
    [SerializeField] private Color m_buttonTextColor = new Color(0.85f, 0.92f, 0.95f, 1f);

    [Tooltip("이 화면의 캔버스 — 비우면 자기·부모에서 찾는다. 키패드 클릭이 이 캔버스의 카메라를 탄다")]
    [SerializeField] private Canvas m_canvas;

    private readonly List<int> m_entry = new List<int>();

    private void Awake()
    {
        if (m_terminal == null)
            m_terminal = GetComponentInParent<BlackoutRecoveryTerminal>();

        if (m_canvas == null)
            m_canvas = GetComponentInParent<Canvas>();

        BuildKeypad();
    }

    private void OnEnable()
    {
        // 화면이 켜지는 순간이 곧 먹통 시작이다 — 이전 시도의 입력이 남아 있으면 안 된다.
        m_entry.Clear();

        if (m_terminal != null)
            m_terminal.OnCodeChanged += HandleCodeChanged;

        Redraw();
    }

    private void OnDisable()
    {
        // ?. 금지 — 파괴된 Unity 오브젝트의 fake null을 우회하지 않게 한다 (HqPanelView 관례)
        if (m_terminal != null)
            m_terminal.OnCodeChanged -= HandleCodeChanged;
    }

    /// <summary>
    /// 클릭을 받을 카메라를 물려 둔다 — 월드 공간 캔버스는 이벤트 카메라 없이는 버튼이 눌리지 않는다.
    ///
    /// 비워 두면 <see cref="UnityEngine.UI.GraphicRaycaster"/>가 <c>Camera.main</c>으로 폴백하는데,
    /// <c>Player.prefab</c>의 시점 카메라는 <b>Untagged</b>라 그 폴백이 null이다 — 화면은 멀쩡히 보이는데
    /// 아무 버튼도 안 눌리는 형태로 드러난다 (<see cref="WeatherSkyRig"/>가 겪은 것과 같은 함정).
    ///
    /// 한 번 잡고 끝내지 않는다 — 관전 전환(#590)·CCTV로 카메라가 꺼지면 다른 것을 다시 잡아야 한다.
    /// 화면은 먹통 중에만 켜져 있으므로 이 폴링도 그동안만 돈다.
    /// </summary>
    private void Update()
    {
        if (m_canvas == null)
            return;

        if (m_canvas.worldCamera != null && m_canvas.worldCamera.isActiveAndEnabled)
            return;

        m_canvas.worldCamera = FindViewCamera();
    }

    // 로컬 플레이어의 시점 카메라 — 세션 밖(테스트 씬)에서는 태그된 카메라로 버틴다.
    private static Camera FindViewCamera()
    {
        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null || !manager.IsListening || manager.LocalClient.PlayerObject == null)
            return Camera.main;

        // 몸통 바로 아래 시점 카메라가 먼저 잡힌다 — 뷰모델 카메라는 그 자식이다.
        foreach (Camera camera in manager.LocalClient.PlayerObject.GetComponentsInChildren<Camera>(true))
        {
            if (camera.isActiveAndEnabled)
                return camera;
        }

        return Camera.main;
    }

    // 코드가 새로 뽑히면(발급·오입력 재발급) 입력도 비운다 — 틀린 뒤 이어서 누르면 섞인다.
    private void HandleCodeChanged(int code)
    {
        m_entry.Clear();
        Redraw();
    }

    private void BuildKeypad()
    {
        if (m_keypadRoot == null)
            return;

        for (int i = 0; i < k_digitCount; i++)
        {
            int digit = i; // 클로저가 루프 변수를 잡지 않게 복사한다
            MakeButton(digit.ToString(), () => Press(digit));
        }

        MakeButton("지우기", ClearEntry);
        MakeButton("나가기", Leave);
    }

    private void MakeButton(string label, UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(m_keypadRoot, false);
        go.GetComponent<Image>().color = m_buttonColor;

        var text = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        text.transform.SetParent(go.transform, false);

        var rect = (RectTransform)text.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        var tmp = text.GetComponent<TextMeshProUGUI>();
        tmp.text = label;
        tmp.fontSize = m_buttonFontSize;
        tmp.color = m_buttonTextColor;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false; // 글자가 클릭을 먹으면 버튼이 안 눌린다

        go.GetComponent<Button>().onClick.AddListener(onClick);
    }

    private void Press(int digit)
    {
        if (m_terminal == null || !m_terminal.IsOnline)
            return;

        if (m_entry.Count >= BlackoutRecoveryTerminal.k_codeDigits)
            return;

        m_entry.Add(digit);
        Redraw();

        // 자릿수를 채우면 곧바로 제출한다 — 확인 버튼을 따로 두면 누를 것이 하나 더 늘 뿐이다.
        if (m_entry.Count == BlackoutRecoveryTerminal.k_codeDigits)
            Submit();
    }

    private void ClearEntry()
    {
        m_entry.Clear();
        Redraw();
    }

    // 화면에서 나간다 — 카메라를 1인칭으로 되돌린다. 포커스 쪽이 커서·이동 잠금도 함께 푼다.
    private void Leave()
    {
        PlayerTerminalFocus focus = FindFocusInUse();
        if (focus != null)
            focus.Release();
    }

    private void Submit()
    {
        int value = 0;
        for (int i = 0; i < m_entry.Count; i++)
            value = value * 10 + m_entry[i];

        m_terminal.SubmitCode(value);

        // 결과를 여기서 판단하지 않는다 — 맞았으면 먹통이 풀려 화면이 꺼지고, 틀렸으면 서버가 코드를
        // 새로 뽑아 HandleCodeChanged가 입력을 비운다. 어느 쪽이든 상태 변화가 화면을 다시 그린다.
    }

    // 이 화면을 보고 있는 로컬 플레이어의 포커스 — 나가기 버튼이 쓴다.
    // 화면은 상호작용한 본인에게만 의미가 있으므로 지금 이 단말을 보고 있는 것만 찾는다.
    private PlayerTerminalFocus FindFocusInUse()
    {
        PlayerTerminalFocus[] all = FindObjectsByType<PlayerTerminalFocus>(FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != null && all[i].Terminal == m_terminal)
                return all[i];
        }

        return null;
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
