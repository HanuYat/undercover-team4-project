using Unity.Netcode;
using UnityEngine;

/// <summary>
/// NPC 체력 바 프레젠터 — 조준한 NPC의 바만 켠다. (#493)
/// 오너 로컬 전용: 남의 조준으로 내 화면의 바가 켜지면 안 된다.
///
/// 대상은 <see cref="PlayerInteractor.ProbeTarget"/>에서 받는다 — 도달 거리(E 상호작용)와 무관하게
/// 조준 레이가 닿는 곳까지 잡히므로, 테이저 8m로 제압하는 동안에도 바가 유지된다. 자체 Raycast를
/// 쓰던 것을 걷어낸 경위는 #499. 가시선·래그돌 뼈 조준 판정도 그쪽 것을 그대로 따른다.
///
/// 표시물(<see cref="NpcHealthBarView"/>)은 NPC 프리팹의 자식이라 위치는 Transform이 알아서 따라간다.
/// 이 프레젠터는 조준 대상이 <b>바뀔 때만</b> 이전 바를 끄고 새 바를 켠다 — 값 갱신은 바가 스스로 한다.
///
/// 이미 싸움이 끝난 대상(기절해 누움·밧줄 끌림)은 제외한다 — <see cref="IsOutOfFight"/>.
/// </summary>
public class NpcHealthBarPresenter : NetworkBehaviour
{
    // 표시 거리·레이어·가시선은 모두 PlayerInteractor의 조준 레이가 정한다 (m_probeRange, #499)
    private PlayerInteractor m_interactor;

    // 지금 켜 둔 바 — 조준이 바뀌면 끄고 교체한다
    private NpcHealthBarView m_current;

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            enabled = false; // 남의 플레이어 조준으로 내 화면이 바뀌지 않게
            return;
        }

        m_interactor = GetComponentInParent<PlayerInteractor>();
        if (m_interactor == null)
            Debug.LogWarning("NpcHealthBarPresenter: PlayerInteractor를 찾지 못해 체력 바를 띄울 수 없다", this);
    }

    public override void OnNetworkDespawn()
    {
        if (IsOwner)
            HideCurrent(); // 퇴장·씬 전환으로 사라질 때 바가 켜진 채 남지 않게
    }

    private void OnDisable() => HideCurrent();

    // 이벤트가 아니라 매 프레임 조회다 — 조준을 유지한 채로도 생사·밧줄 상태가 바뀌므로
    // IsOutOfFight를 계속 다시 봐야 한다.
    private void Update() // 비오너는 OnNetworkSpawn에서 비활성화되므로 오너만 돈다
    {
        Show(Resolve(m_interactor != null ? m_interactor.ProbeTarget : null));
    }

    // 조준 대상에서 NPC의 체력 바를 찾는다.
    // 콜라이더가 NPC 루트의 자식일 수 있어 부모까지 탐색한다 (ScanResultPresenter와 같은 이유).
    private NpcHealthBarView Resolve(GameObject target)
    {
        if (target == null)
            return null;

        NpcController npc = target.GetComponentInParent<NpcController>();
        if (npc == null)
            return null;

        if (IsOutOfFight(npc))
            return null;

        // 비활성 상태로 프리팹에 들어 있으므로 includeInactive로 찾는다
        return npc.GetComponentInChildren<NpcHealthBarView>(true);
    }

    /// <summary>
    /// 이미 싸움이 끝난 대상인가 — 죽었거나 밧줄에 묶인 중. 남은 체력이 더 이상 판단에 쓰이지 않는다.
    ///
    /// <b>기절은 빼 둔다</b> (#916) — 누운 자세로 판정하면 기절이 사망과 함께 묶여 바가 사라진다.
    /// 쓰러진 몸에 뜨는 빈 바가 "한 대 더 치면 죽는다"를 말해 준다.
    /// </summary>
    private bool IsOutOfFight(NpcController npc) => npc.Death.IsDead || npc.Rope.IsRoped;

    private void Show(NpcHealthBarView view)
    {
        if (view == m_current)
            return; // 같은 대상 — 값 갱신은 바가 스스로 한다

        HideCurrent();

        if (view == null)
            return;

        // 빌보드가 바라볼 카메라를 로컬 플레이어 카메라로 지정 (Camera.main 태그에 의존하지 않게)
        if (m_interactor.AimCamera != null)
            view.SetCamera(m_interactor.AimCamera.transform);

        view.Show();
        m_current = view;
    }

    private void HideCurrent()
    {
        // ?. 금지 — Unity 가짜 null을 못 걸러 파괴된 오브젝트에 호출이 들어간다 (#370 관례)
        if (m_current != null)
            m_current.Hide();

        m_current = null;
    }
}
