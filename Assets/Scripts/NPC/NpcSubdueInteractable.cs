using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// NPC의 상호작용키(E) 반응 (#76/#91/#398/#513) — 누르는 즉시 NPC 상태에 따라 갈린다.
/// 체포(Captured) 상태면 <b>밧줄을 푼다</b> — 좌클릭 3초 홀드에서 E 한 번으로 옮겼다 (#513).
/// 단 그 대상이 반출된 수감자면(#517) 풀 줄이 애초에 없으므로 <b>밧줄 없는 추종</b>을 재개한다 — 반출 흐름 왕복.
/// 남이 끌고 있는(Escorted) 대상에 내 줄이 걸려 있으면 <b>내 줄만 뺀다</b> — 줄다리기에서 손 떼기 (#398/#513).
///
/// <b>E와 좌클릭의 역할이 갈렸다</b> (#513): 밧줄 좌클릭은 <b>줄을 거는 쪽</b>(묶기·합류·재개),
/// E는 <b>손을 떼는 쪽</b>이다.
///
/// 여기로 오는 것은 <b>겨냥한</b> E뿐이다 — 겨냥이 비어 있으면 끌던 대상 전원을 놓는 쪽으로 간다
/// (<see cref="PlayerInteractor"/> → <c>RequestUnropeAll</c>, #638). 그래서 이 경로는 "여러 명을 끌 때
/// 한 명만 고른다"와 "안 끌고 있는 대상(놓아둔 신병·수감자)을 다룬다"가 역할로 남는다.
/// 수감(Jailed) 상태에는 E가 반응하지 않는다 — 반출이 사라지면서 유치장 안 신병은 손댈 수 없다.
/// PlayerInteractor의 IInteractable 경로를 그대로 사용하므로
/// NPC가 사거리·조준을 벗어나면 자연히 실패한다.
/// 프롬프트 표시는 상호작용 UI 이슈(#65 계열) 후속.
///
/// <b>E는 신병 조작 전용 키가 됐다</b> — 때리는 것도 잡는 것도 하지 않는다.
/// 두 단계로 걷혔다: 도주형 전용 3초 제압 홀드(#332 → #436 제거),
/// 그리고 배회·도주·저항에 대한 제압 타격(#79/#366 → #438 제거).
/// 이제 체력을 깎는 것은 <see cref="Baton"/>(진압봉), 즉시 무력화는 <see cref="Taser"/>(#292),
/// 신병 확보는 <see cref="Rope"/>(밧줄, #369)가 각각 맡는다.
///
/// 클래스·파일명에 남은 "Subdue"(제압)는 <b>이름만 남은 이력</b>이다 — 프리팹이 스크립트를
/// 파일 GUID로 참조하므로 개명 churn을 피해 그대로 뒀다. 실제 역할은 위 두 갈래뿐이다.
/// </summary>
[RequireComponent(typeof(NpcController))]
public class NpcSubdueInteractable : MonoBehaviour, IInteractable
{
    private NpcController m_controller;

    private void Awake()
    {
        m_controller = GetComponent<NpcController>();
    }

    /// <summary>E 상호작용이 실제로 동작하는 상태인지 — 조준 피드백(윤곽선) 판정용. (#184)
    /// 상태를 먼저 본다: 조준 대상마다 매 프레임 도는 경로라 Escorted가 아니면 escorter 조회조차
    /// 하지 않는다(인자가 즉시 평가되므로 CanRejoinOwnRope 안의 상태 검사로는 늦다).</summary>
    public bool CanInteract(GameObject interactor) =>
        NpcStateRules.HasInteractKeyAction(m_controller.CurrentState)
        || (
            m_controller.CurrentState == NpcState.Escorted
            && CanRejoinOwnRope(FindTethers(interactor))
        );

    // 요청은 명령 허브로, "내 줄인가" 판정은 연결 상태로 — 둘은 서로 다른 컴포넌트다.
    private static PlayerEscortCommands FindCommands(GameObject interactor) =>
        interactor != null ? interactor.GetComponentInParent<PlayerEscortCommands>() : null;

    private static PlayerEscorter FindTethers(GameObject interactor) =>
        interactor != null ? interactor.GetComponentInParent<PlayerEscorter>() : null;

    /// <summary>
    /// 남이 계속 끌고 있는(Escorted) 대상이라도 <b>내 줄이 걸려 있으면</b> E로 그 줄을 뺄 수 있는가 —
    /// 줄다리기에서 손을 떼는 경로다. 복귀는 밧줄 좌클릭이다. (#398/#513)
    ///
    /// 상태 순수 함수인 <see cref="NpcStateRules"/>에 둘 수 없다 — "누구의 줄인가"는 요청자마다 다르다.
    /// 그리고 그 조건이 곧 <b>탈취 차단</b>이다: 남의 신병에는 내 줄이 없다.
    /// 이미 끌고 있으면 제외한다 — 그때 E는 '놓기'로 가로채진다(PlayerInteractor).
    /// </summary>
    private bool CanRejoinOwnRope(PlayerEscorter tethers) =>
        m_controller.CurrentState == NpcState.Escorted
        && tethers != null
        && tethers.IsTetheredTo(m_controller)
        && !tethers.IsDraggingNpc(m_controller);

    /// <summary>
    /// 조준 안내 (#664) — 아래 <see cref="Interact"/>의 갈래를 그대로 따라간다. 어긋나면 거짓말이
    /// 되므로 저쪽을 고치면 여기도 함께 고칠 것.
    /// 내가 끌고 있는 대상은 여기 오지 않는다 — 그 E는 PlayerInteractor가 '놓기'로 먼저 가져간다(#638).
    /// </summary>
    public LocalizedString PromptLabel(GameObject interactor)
    {
        switch (m_controller.CurrentState)
        {
            case NpcState.Escorted:
                return CanRejoinOwnRope(FindTethers(interactor))
                    ? InteractPrompts.NpcUnropeMine
                    : null;

            case NpcState.Captured:
                return InteractPrompts.NpcUnrope;
        }

        return null;
    }

    public void Interact(GameObject interactor)
    {
        PlayerEscortCommands escorter = FindCommands(interactor);

        // 신병이 걸린 두 상태에서만 반응한다. 배회·도주·저항은 #438에서 빠졌다 — 그 상태의
        // NPC에게 E는 아무 일도 하지 않으며, CanInteract가 false라 윤곽선도 뜨지 않는다.
        // 이 switch의 분기 집합은 NpcStateRules.HasInteractKeyAction + CanRejoinOwnRope와 반드시
        // 일치해야 한다 (#184 — Escorted 분기만 상태가 아니라 요청자의 줄로 갈린다, #398)
        switch (m_controller.CurrentState)
        {
            case NpcState.Escorted:
                // 남이 계속 끄는 중인 대상에서 내 줄만 뺀다 — 줄다리기에서 손 떼기 (#398/#513).
                // 복귀는 밧줄 좌클릭이다. 서버가 줄 소유·사거리를 다시 검증하므로 여기 검사는 조기 차단일 뿐이다.
                if (CanRejoinOwnRope(FindTethers(interactor)))
                {
                    Debug.Log($"E 입력 — 내 줄 빼기 요청(줄다리기 이탈): {m_controller.name}");
                    escorter?.RequestUnrope(m_controller);
                }
                break;

            case NpcState.Captured:
                // 놓아둔 신병의 밧줄을 푼다 — 재개는 밧줄 좌클릭으로 옮겼다 (#513).
                // 남이 묶어 둔 대상도 풀 수 있다(오검거 구제·방해 수단) — 그 판정은 CanUnrope가 쥔다.
                // 서버 직접 호출은 가드에 막힌다 — 요청 API로 서버에 넘긴다 (#118).
                Debug.Log($"E 입력 — 밧줄 풀기 요청: {m_controller.name}");
                escorter?.RequestUnrope(m_controller);
                break;

        }
    }
}
