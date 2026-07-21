using TMPro;
using UnityEngine;

/// <summary>
/// 폭탄 일련번호 표시 — 케이스에 각인된 번호를 월드 텍스트로 보여준다. (#232 표현 계층)
///
/// <b>이 표시가 없으면 퍼즐이 성립하지 않는다.</b> 규칙표에는 "일련번호 끝자리가 홀수면" 같은 조건이
/// 섞여 나오는데(<see cref="BombRuleCondition.SerialOdd"/>), 본부 매뉴얼(<see cref="BombManualHud"/>)은
/// 규칙만 보여줄 뿐 번호를 모른다 — 현장이 폭탄에서 읽어 무전으로 불러주는 것이 설계상 정보 흐름이다.
///
/// 순수 표현이다. 번호는 시드로 재구성된 <see cref="BombDevice.Puzzle"/>에서 읽으므로 전 피어가 같은
/// 값을 보고, 별도 동기화가 필요 없다 — 무장(퍼즐 확정) 시점에 한 번만 갱신하면 된다.
/// </summary>
public class BombSerialView : MonoBehaviour
{
    [Tooltip("일련번호를 그릴 텍스트 — 비우면 이 오브젝트의 TMP_Text를 쓴다")]
    [SerializeField]
    private TMP_Text m_label;

    [Tooltip("표시 형식 — {0}에 일련번호가 들어간다")]
    [SerializeField]
    private string m_format = "SN-{0}";

    [Tooltip("무장 전(퍼즐 미확정) 표시")]
    [SerializeField]
    private string m_idleText = "SN-***";

    private BombDevice m_device;

    private void Awake()
    {
        if (m_label == null)
            m_label = GetComponent<TMP_Text>();
    }

    private void OnEnable()
    {
        m_device = GetComponentInParent<BombDevice>();
        if (m_device != null)
            m_device.OnPuzzleReady += Refresh;
        Refresh(); // 이미 무장한 폭탄(늦은 활성·재접속)이면 현재 번호를 즉시 반영
    }

    private void OnDisable()
    {
        if (m_device != null)
            m_device.OnPuzzleReady -= Refresh;
    }

    private void Refresh()
    {
        if (m_label == null)
            return;

        BombPuzzle puzzle = m_device != null ? m_device.Puzzle : null;
        m_label.text = puzzle != null ? string.Format(m_format, puzzle.Serial) : m_idleText;
    }
}
