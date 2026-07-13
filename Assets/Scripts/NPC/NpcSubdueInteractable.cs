using UnityEngine;

/// <summary>
/// NPC 근접 제압 (#76/#79) — 상호작용 홀드 완료 시 NPC 상태에 따라 갈린다.
/// 도주(Run) 중이면 그 자리에서 체포(Captured), 저항(Attack) 중이면 제압 타격 1회로
/// 게이지를 깎는다 — 여럿이 함께 홀드하면 그만큼 빨리 제압된다 (GDD 7-4 협동 인센티브).
/// PlayerInteractor의 IInteractable 경로를 그대로 사용하므로
/// 홀드 도중 NPC가 사거리·조준을 벗어나면 자연히 실패한다 (추격전·몸싸움 성립).
/// 제압 프롬프트 표시는 상호작용 UI 이슈(#65 계열) 후속 — 지금은 홀드 완료로만 동작.
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

    public void Interact(GameObject interactor)
    {
        // 도주·저항 중일 때만 반응 — 배회 중인 NPC를 홀드로 잡는 것 방지 (체포는 수갑 채널링이 정식 경로)
        switch (m_controller.CurrentState)
        {
            case NpcState.Run:
                // 도주 제압도 서버 권위 — 요청자(플레이어)의 PlayerEscorter를 통해 서버로 넘긴다 (#118).
                // CaptureBySubdue는 서버 가드가 있어 클라에서 직접 부르면 무시되기 때문.
                PlayerEscorter escorter = interactor != null
                    ? interactor.GetComponentInParent<PlayerEscorter>() : null;
                if (escorter != null)
                {
                    Debug.Log($"도주 NPC 제압 요청: {m_controller.name}");
                    escorter.RequestSubdueCapture(m_controller);
                }
                break;

            case NpcState.Attack:
                // 저항 타격은 이미 자체 RPC 경로(RequestSubdueHit → SubdueHitRpc)를 가진다 (#79)
                Debug.Log($"저항 NPC 제압 타격: {m_controller.name}");
                m_controller.RequestSubdueHit();
                break;
        }
    }
}
