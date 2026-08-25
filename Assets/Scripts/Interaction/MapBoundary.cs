using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 맵 경계 — 플레이 가능 영역 밖으로 나간 플레이어를 본부 스폰 지점으로 되돌린다.
/// 콜라이더는 Is Trigger이고 <b>플레이 가능 영역 전체를 감싸는</b> 볼륨이어야 한다 — 안이 정상,
/// 나가는 쪽을 잡는다. 되돌릴 자리는 <see cref="PlayerSpawnManager"/>가 준다(스폰 지점 + 겹침 분산).
///
/// 서버 권위 — 트리거 판정도 위치 복구도 서버(또는 오프라인)에서만 돈다.
/// </summary>
[RequireComponent(typeof(Collider))]
public class MapBoundary : MonoBehaviour
{
    [Tooltip("되돌릴 자리를 주는 스폰 매니저 — 같은 씬의 것을 배선한다")]
    [SerializeField] private PlayerSpawnManager m_spawnManager;

    // 밖으로 나갔지만 아직 못 되돌린 플레이어 — 래그돌 중이면 일어날 때까지 여기 머문다
    private readonly HashSet<PlayerMovement> m_pending = new HashSet<PlayerMovement>();

    private Collider m_volume;

    private void Awake()
    {
        m_volume = GetComponent<Collider>();

        if (m_spawnManager == null)
        {
            Debug.LogError($"[맵 경계] 스폰 매니저 미배선 — {name}의 이탈 복귀가 꺼진다");
            enabled = false;
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (!IsAuthority)
            return;

        PlayerMovement movement = other.GetComponentInParent<PlayerMovement>();
        if (movement != null)
            m_pending.Add(movement); // 실제 이탈인지는 TryReturn이 좌표로 되묻는다
    }

    private void Update()
    {
        if (!IsAuthority || m_pending.Count == 0)
            return;

        m_pending.RemoveWhere(TryReturn);
    }

    // 되돌렸거나 더 볼 필요가 없으면 true — 그 프레임에 목록에서 빠진다.
    private bool TryReturn(PlayerMovement movement)
    {
        if (movement == null)
            return true; // 접속 종료·디스폰

        // ⚠ 래그돌은 CharacterController를 끄는데(PlayerRagdoll), 콜라이더가 꺼져도 OnTriggerExit이 온다 —
        // 맵 안에서 죽었을 뿐이면 좌표는 여전히 경계 안이다. 실제로 나갔는지 좌표로 되묻지 않으면
        // 어디서 죽든 시체가 본부로 끌려간다.
        if (m_volume.bounds.Contains(movement.transform.position))
            return true;

        // 래그돌 중에는 몸의 주인이 캡슐이 아니라 뼈라 순간이동이 도로 끌려간다 — 다시 움직일 수
        // 있게 될 때까지 기다린다. 홈런 비행은 정착 통보가 안 와도 안전장치 타이머가 반드시 푼다(#815).
        PlayerIncapacitation incapacitation = movement.GetComponent<PlayerIncapacitation>();
        if (incapacitation != null && incapacitation.IsIncapacitated)
            return false;

        m_spawnManager.ServerReturnToSpawn(movement);
        Debug.Log($"[맵 경계] 이탈 복귀: {movement.name}");
        return true;
    }

    // 서버(또는 오프라인)에서만 판정한다 — 구역 판정 공통 가드
    private static bool IsAuthority =>
        NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer;
}
