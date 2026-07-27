using UnityEngine;

/// <summary>
/// NPC의 상호작용키(E) 반응 (#76/#79/#91) — 누르는 즉시 NPC 상태에 따라 갈린다.
/// 도주(Run) 중이면 3초 제압 홀드를 시작한다(#332 — 붙어서 유지해야 체포, 뗌·이탈이면 무산).
/// 배회(Idle/Walk)·저항(Attack) 중이면 타격 1회로 체력을 깎는다 — 여럿이 함께 누르면 그만큼
/// 빨리 기절시킨다 (GDD 7-4 협동 인센티브, #366).
/// 체포(Captured) 상태면 재연행을 시작한다 — 연행 동작을 수갑 클릭에서 E로 이관 (#91).
/// PlayerInteractor의 IInteractable 경로를 그대로 사용하므로
/// NPC가 사거리·조준을 벗어나면 자연히 실패한다 (추격전·몸싸움 성립).
/// 제압 프롬프트 표시는 상호작용 UI 이슈(#65 계열) 후속.
/// </summary>
// TODO: 상호작용 네트워크 전환(#55 계열) 시 도주 제압도 클라 입력 → ServerRpc 경로로 호출
//       (저항 타격은 RequestSubdueHit이 자체 RPC 경로를 가진다, #79)
[RequireComponent(typeof(NpcController))]
public class NpcSubdueInteractable : MonoBehaviour, IInteractable
{
    private NpcController m_controller;

    private void Awake()
    {
        m_controller = GetComponent<NpcController>();
    }

    /// <summary>E 상호작용이 실제로 동작하는 상태인지 — 조준 피드백(윤곽선) 판정용. (#184)</summary>
    public bool CanInteract(GameObject interactor) =>
        NpcStateRules.HasSubdueInteraction(m_controller.CurrentState);

    public void Interact(GameObject interactor)
    {
        // 배회·도주·저항·체포 상태에서 반응한다 (체포는 수갑 채널링이 정식 경로).
        // 배회가 열린 것은 체력이 지속형이 되면서다 (#366)
        // 이 switch의 분기 집합은 NpcStateRules.HasSubdueInteraction과 반드시 일치해야 한다 (#184)
        switch (m_controller.CurrentState)
        {
            case NpcState.Run:
                // 도주 제압도 서버 권위 — 요청자(플레이어)의 PlayerEscorter를 통해 서버로 넘긴다 (#118).
                // CaptureBySubdue는 서버 가드가 있어 클라에서 직접 부르면 무시되기 때문.
                PlayerEscorter escorter =
                    interactor != null ? interactor.GetComponentInParent<PlayerEscorter>() : null;
                if (escorter != null)
                {
                    Debug.Log($"도주 NPC 제압 홀드 시작 요청: {m_controller.name}");
                    escorter.RequestSubdueCapture(m_controller);
                }
                break;

            case NpcState.Idle:
            case NpcState.Walk:
            case NpcState.Attack:
                // 타격은 자체 RPC 경로(RequestSubdueHit → SubdueHitRpc)를 가진다 (#79).
                // 배회(Idle/Walk)도 같은 경로다 — 체력이 지속형이 되면서 저항 중이 아니어도
                // 때려서 깎을 수 있다 (#366). 상태 게이트는 TakeDamage가 CanBeStunned로 건다.
                Debug.Log($"NPC 제압 타격: {m_controller.name}");
                m_controller.RequestSubdueHit();
                break;

            case NpcState.Captured:
                // 체포되어 멈춘 NPC를 E로 다시 끌기 시작 — 좌클릭은 같은 대상에서 '풀어주기'라 재개는 E다 (#91/#369).
                // 서버 직접 호출은 가드에 막힌다 — 요청 API로 서버에 넘긴다 (#118).
                // 중복 확보 가드(동시 1명)·밧줄 소지·사거리는 서버가 처리한다
                Debug.Log($"E 입력 — 밧줄 끌기 재개 요청: {m_controller.name}");
                interactor.GetComponentInParent<PlayerEscorter>()?.RequestRopeResume(m_controller);
                break;
        }
    }
}
