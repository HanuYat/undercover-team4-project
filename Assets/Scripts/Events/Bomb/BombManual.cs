using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 폭탄 해체 매뉴얼 — 본부에 놓인 책. 상호작용하면 <b>현재 폭탄의 규칙표</b>를 펼친다. (#232)
///
/// 본부는 이 규칙을 읽어, 현장이 무전으로 불러주는 폭탄 특징(선 색·일련번호)에 적용해 자를 선을
/// 판단한다 (KTANE 모델). 규칙은 라운드마다 시드로 재배치되므로 외울 수 없고 매번 읽어야 한다.
///
/// <b>정합성.</b> 규칙 문장은 <see cref="BombPuzzle.DescribeRule"/>가 만들고, 서버 정답 판정도 같은
/// 규칙 데이터를 쓴다 — 매뉴얼과 코드가 어긋날 수 없다. 매뉴얼은 <see cref="BombDevice.Active"/>의
/// 퍼즐(모든 피어가 시드로 재구성한 동일 퍼즐)에서 규칙을 읽으므로 별도 동기화가 필요 없다.
///
/// 설치형 고정 오브젝트(GDD 8-4, SignalDecoder와 동일 부류) — 들고 다니지 않는다.
/// 상호작용은 오너 클라(상호작용한 본인)에서만 일어나므로 책 UI는 로컬로 열린다.
/// </summary>
[RequireComponent(typeof(BombManualHud))]
public class BombManual : MonoBehaviour, IInteractable
{
    private BombManualHud m_hud;

    private void Awake()
    {
        m_hud = GetComponent<BombManualHud>();
    }

    // 매뉴얼은 언제나 펼칠 수 있다(책이다). 활성 폭탄이 없으면 펼쳐도 규칙이 비어 있을 뿐이다.
    public bool CanInteract(GameObject interactor) => true;

    /// <summary>
    /// E 상호작용 — 책 UI를 펼친다. PlayerInteractor는 오너 클라에서만 돌므로(SignalDecoder와 동일)
    /// 이 호출도 상호작용한 본인의 클라이언트에서만 일어나 UI는 로컬로 열린다.
    /// </summary>
    public void Interact(GameObject interactor)
    {
        if (m_hud == null)
            m_hud = GetComponent<BombManualHud>(); // 방어적 — 씬 배치본은 Awake에서 잡히지만 만일을 대비
        m_hud.Open(this, interactor);
    }

    /// <summary>현재 폭탄의 규칙표를 사람이 읽는 문장으로 — 책 UI가 이 목록을 그대로 띄운다.</summary>
    public List<string> GetRuleLines()
    {
        List<string> lines = new List<string>();

        BombDevice bomb = BombDevice.Active;
        if (bomb == null || bomb.Puzzle == null)
            return lines;

        IReadOnlyList<BombRule> rules = bomb.Puzzle.Rules;
        for (int i = 0; i < rules.Count; i++)
            lines.Add(BombPuzzle.DescribeRule(rules[i]));

        return lines;
    }
}
