using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 이벤트 NPC 소멸 연출 (#310) — 정리(despawn) 직전, 사라지는 자리에 몸체 잔상(<see cref="NpcDespawnGhost"/>)을
/// 남겨 글리치로 디매터리얼라이즈되는 그림을 만든다. 돌발 이벤트 스폰물(난동자·난동꾼·침입자·괴한)이
/// 눈앞에서 소리 없이 증발하는 어색함을 세계관(사이버펑크) 연출로 가린다.
/// <see cref="SuddenEventUtil.DespawnOrDestroy"/>가 이 컴포넌트를 발견하면 자동으로 재생하므로,
/// 이벤트 NPC 프리팹에 붙이고 잔상 머티리얼만 배선하면 된다 — 이벤트 코드는 수정할 필요가 없다.
///
/// 전파는 <see cref="SuddenEventManager.Announce"/>와 같은 ClientRpc 패턴이다: 잔상 오브젝트를 네트워크에
/// 싣지 않고(프리팹 등록 불필요) 각 피어가 자기 화면에 로컬로 생성한다. RPC는 뒤이은 despawn 메시지보다
/// 먼저 배송되므로 원격 클라에서도 NPC가 사라지기 직전 포즈·위치로 잔상을 뜰 수 있다.
/// </summary>
public class NpcDespawnVfx : NetworkBehaviour
{
    [Tooltip("몸체 잔상(고스트)에 씌울 머티리얼 — NPC의 현재 포즈를 베이크해 플리커시키며 사라지게 한다. 비우면 연출 없음")]
    [SerializeField] private Material m_ghostMaterial;

    /// <summary>
    /// 소멸 연출 재생 — despawn '직전'에 서버(또는 오프라인)에서만 호출한다.
    /// 머티리얼이 배선되지 않았으면 조용히 무동작(연출은 선택 사항).
    /// </summary>
    public void ServerPlay()
    {
        if (m_ghostMaterial == null)
            return;

        SpawnLocal(); // 서버(호스트)·오프라인 로컬 재생
        if (IsSpawned && IsServer)
            PlayClientRpc();
    }

    [ClientRpc]
    private void PlayClientRpc()
    {
        // 호스트는 위에서 이미 재생했다 — 원격 클라에서만 중계 (SuddenEventManager.AnnounceEventClientRpc와 동일)
        if (IsServer)
            return;
        SpawnLocal();
    }

    // 이 시점에는 NPC가 아직 파괴되지 않았다(RPC가 despawn 메시지보다 먼저 배송) — 잔상 베이크가 가능한 이유
    private void SpawnLocal()
    {
        NpcDespawnGhost.Spawn(transform, m_ghostMaterial);
    }
}
