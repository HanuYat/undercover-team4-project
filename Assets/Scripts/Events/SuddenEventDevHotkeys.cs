using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 돌발 이벤트 개발자 단축키 — <b>에디터 전용</b>. 추첨을 기다리지 않고 F1~F12로 즉시 발동시킨다.
///
/// <b>F9는 비워 둔다</b> — <see cref="SecretFavorBroker"/>의 청탁 발행 키다(팀 확정 2026-08-13).
/// 그래서 열둘 중 열하나만 쓰고, F10부터는 인덱스가 하나씩 앞당겨진다 (F8=8번째, F10=9번째).
///
/// 순서 기준은 <see cref="SuddenEventManager"/>의 <b>인스펙터 이벤트 풀</b>이다. 꺼 둔 항목은 풀에
/// 아예 안 들어가므로 번호가 밀린다 — 그래서 외우라고 만들지 않고 Play 시작 때 실제 매핑을 콘솔에
/// 한 번 찍는다. 풀을 건드렸으면 그 로그만 보면 된다.
///
/// 발동은 <see cref="SuddenEventManager.ForceTrigger"/>가 하므로 <b>서버(또는 오프라인)에서만</b>
/// 듣는다. MPPM 클론에서 눌러도 아무 일도 일어나지 않는다 — 키 입력은 로컬이고 발생은 서버 판정이라,
/// SecretFavorBroker의 개발용 키와 같은 규칙이다.
///
/// 이미 진행 중이거나 조건(CanTrigger)이 안 맞는 이벤트는 무시되고 그 이유가 콘솔에 남는다.
/// </summary>
[RequireComponent(typeof(SuddenEventManager))]
public class SuddenEventDevHotkeys : MonoBehaviour
{
#if UNITY_EDITOR
    // 순서가 곧 이벤트 풀 인덱스다. F9는 청탁(SecretFavorBroker) 몫이라 목록에서 빠져 있다.
    private static readonly Key[] k_keys =
    {
        Key.F1, Key.F2, Key.F3, Key.F4, Key.F5, Key.F6, Key.F7, Key.F8,
        Key.F10, Key.F11, Key.F12,
    };

    [Tooltip("끄면 단축키가 듣지 않는다 — 같은 키를 쓰는 다른 테스트를 할 때 잠깐 내린다")]
    [SerializeField] private bool m_enabled = true;

    [Tooltip("Play 시작 시 어떤 키에 무슨 이벤트가 물렸는지 콘솔에 찍는다")]
    [SerializeField] private bool m_logMappingOnStart = true;

    private SuddenEventManager m_manager;

    private void Awake() => m_manager = GetComponent<SuddenEventManager>();

    private void Start()
    {
        if (m_logMappingOnStart)
            LogMapping();
    }

    private void Update()
    {
        if (!m_enabled || m_manager == null)
            return;

        // 키보드가 없는 구성(원격 데스크톱 등)에서는 조용히 넘어간다
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        for (int i = 0; i < k_keys.Length; i++)
        {
            if (keyboard[k_keys[i]].wasPressedThisFrame)
                m_manager.ForceTrigger(i);
        }
    }

    /// <summary>지금 어떤 키에 무엇이 물렸는지 콘솔에 찍는다 — 풀 순서를 바꿨을 때 확인용.</summary>
    [ContextMenu("Debug/단축키 매핑 찍기")]
    private void LogMapping()
    {
        if (m_manager == null)
            m_manager = GetComponent<SuddenEventManager>();

        var sb = new System.Text.StringBuilder(
            "[돌발이벤트] 개발자 단축키 매핑 (F9는 청탁 몫이라 건너뜀)");

        for (int i = 0; i < k_keys.Length; i++)
        {
            string eventName = m_manager.EventNameAt(i);
            if (eventName == null)
                break; // 풀이 여기서 끝났다 — 남은 키에는 물릴 이벤트가 없다

            sb.AppendLine();
            sb.Append($"  {k_keys[i]} -> {eventName}");
        }

        if (m_manager.EventCount == 0)
        {
            sb.AppendLine();
            sb.Append("  ⚠ 이벤트 풀이 비어 있다 — SuddenEventManager의 인스펙터 리스트를 확인할 것");
        }
        else if (m_manager.EventCount > k_keys.Length)
        {
            sb.AppendLine();
            sb.Append($"  ⚠ 이벤트 {m_manager.EventCount}개 중 앞의 {k_keys.Length}개만 물렸다");
            sb.Append(" — 키가 모자라니 풀 순서를 바꿔서 테스트할 것");
        }

        Debug.Log(sb.ToString(), this);
    }
#endif
}
