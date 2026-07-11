using UnityEngine;

/// <summary>
/// 도주 NPC 근접 제압 (#76) — 도주(Run) 중인 NPC를 조준하고 상호작용 홀드를 완료하면
/// 그 자리에서 체포(Captured)한다. PlayerInteractor의 IInteractable 경로를 그대로 사용하므로
/// 홀드 도중 NPC가 사거리·조준을 벗어나면 자연히 실패한다 (추격전 성립).
/// 제압 프롬프트 표시는 상호작용 UI 이슈(#65 계열) 후속 — 지금은 홀드 완료로만 동작.
/// </summary>
// TODO: 상호작용 네트워크 전환(#55 계열) 시 클라 입력 → ServerRpc 경로로 호출 (지금은 호스트/오프라인 기준)
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
        // 도주 중일 때만 제압 가능 — 배회 중인 NPC를 홀드로 잡는 것 방지 (체포는 수갑 채널링이 정식 경로)
        if (m_controller.CurrentState != NpcState.Run)
            return;

        Debug.Log($"도주 NPC 제압: {m_controller.name}");
        m_controller.CaptureBySubdue();
    }
}
