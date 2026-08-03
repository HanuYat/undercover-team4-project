using Unity.Netcode;
using UnityEngine;

/// <summary>
/// NPC 체력 바 프레젠터 — 조준한 NPC의 바만 켠다. (#493)
/// 오너 로컬 전용: 남의 조준으로 내 화면의 바가 켜지면 안 된다.
///
/// <b>자체 Raycast를 쓰는 이유</b>: <see cref="PlayerInteractor"/>의 조준 레이는 3m다(E 상호작용
/// 사거리). 그런데 제압은 테이저 8m에서도 일어나므로 그 레이를 재사용하면 원거리로 쏘는 동안
/// 체력 바가 사라진다 — 정작 "제압이 얼마나 남았나"가 필요한 순간에 정보가 없어진다.
/// 카메라 기준·레이 구성은 PlayerInteractor.UpdateTarget과 같은 방식이다.
///
/// 레이 길이와 상호작용 도달 거리를 하나로 합치는 정리(PlayerInteractor.Range가 지금 둘을 겸한다)는
/// 별건이다 — 줍기·구조·운반 도달 거리가 같은 값을 읽고 있어 함께 넓어지기 때문이다.
///
/// 표시물(<see cref="NpcHealthBarView"/>)은 NPC 프리팹의 자식이라 위치는 Transform이 알아서 따라간다.
/// 이 프레젠터는 조준 대상이 <b>바뀔 때만</b> 이전 바를 끄고 새 바를 켠다 — 값 갱신은 바가 스스로 한다.
/// </summary>
public class NpcHealthBarPresenter : NetworkBehaviour
{
    [Tooltip("체력 바를 띄울 최대 거리(m) — 테이저 사거리(8m)보다 넉넉하게 잡는다")]
    [SerializeField]
    private float m_probeRange = 20f;

    [Tooltip(
        "조준 판정에 포함할 레이어 — NPC 레이어(Interactable)와 시야를 막는 레이어(Default)를 함께 넣는다. "
            + "벽을 빼면 레이가 벽을 통과해 뒤에 있는 NPC의 바가 보인다"
    )]
    [SerializeField]
    private LayerMask m_probeMask = ~0;

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

    private void Update() // 비오너는 OnNetworkSpawn에서 비활성화되므로 오너만 돈다
    {
        Camera camera = m_interactor != null ? m_interactor.AimCamera : null;
        if (camera == null)
        {
            HideCurrent();
            return;
        }

        Show(Probe(camera));
    }

    // 카메라 정면으로 한 번 쏘고, 맞은 것에서 NPC의 체력 바를 찾는다.
    // 콜라이더가 NPC 루트의 자식일 수 있어 부모까지 탐색한다 (ScanResultPresenter와 같은 이유).
    private NpcHealthBarView Probe(Camera camera)
    {
        Transform origin = camera.transform;
        var ray = new Ray(origin.position, origin.forward);

        if (!Physics.Raycast(ray, out RaycastHit hit, m_probeRange, m_probeMask))
            return null;

        NpcController npc = hit.collider.GetComponentInParent<NpcController>();
        if (npc == null)
            return null;

        // 비활성 상태로 프리팹에 들어 있으므로 includeInactive로 찾는다
        return npc.GetComponentInChildren<NpcHealthBarView>(true);
    }

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
