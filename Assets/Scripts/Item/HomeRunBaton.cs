using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 홈런 진압봉 아이템 — <see cref="Baton"/>의 조준·서버 판정 골격(부채꼴 스피어캐스트)을 그대로
/// 재사용하는 근접 무기. (GDD 7-4, #815)
///
/// <b>데미지가 아니라 발사가 본체다.</b> 프리팹 <c>m_damage</c>는 0으로 둔다 — HP를 깎지 않으므로
/// 죽이지 않고, 그래서 밧줄 검거·수감 흐름과 원리적으로 충돌하지 않는다. 맞은 대상은 대신
/// <b>기절 오버레이 + 래그돌 임펄스</b>로 타격 방향으로 날아간다.
///
/// <b>NPC와 동료(플레이어)는 다른 문으로 날아간다.</b> NPC는 살아 있는 동안에도 래그돌이 붙을 수
/// 있어(<see cref="NpcKnockback.ServerLaunchRagdoll"/>) 물리로 날아가지만, 살아 있는 플레이어는
/// 래그돌이 붙지 않으므로(<see cref="PlayerRagdoll"/>은 다운·사망 전용) 캡슐 넉백
/// (<see cref="PlayerMovement.AddKnockback"/>)으로 대신한다. 둘의 연출이 다른 것은 사양이다.
/// </summary>
public class HomeRunBaton : Baton
{
    [Header("홈런 진압봉 (#815)")]
    [Tooltip("발사 수평 속도(m/s).")]
    [SerializeField]
    private float m_launchSpeed = 14f;

    [Tooltip(
        "상승 속도 = 수평 속도 × 이 값. 1.0이 45°로 사거리 최대다 — 크게 잡으면 높이 뜨는 대신 "
            + "가까이 떨어진다 (BombBlastProfile.m_ragdollLiftRatio와 같은 노브)"
    )]
    [Range(0f, 3f)]
    [SerializeField]
    private float m_liftRatio = 1f;

    // 정점(m) ≈ (m_launchSpeed × m_liftRatio)² / 19.6 — 기본값 기준 약 10m. 비행 시간은 대략
    // 2 × 상승속도 / 중력이다. 여기 6초는 그보다 넉넉히 길게 잡은 값이다 — 짧으면 NpcStun.Tick의
    // 타이머가 먼저 끝나 ServerReattachToNavMesh가 아직 공중인 몸을 그 자리에서 세운다(§6-2).
    [Tooltip(
        "착지 후까지 누워 있는 시간(초). 비행 시간보다 넉넉히 길게 잡을 것 — 짧으면 공중에서 "
            + "기상 모션이 나가는 사고가 난다"
    )]
    [SerializeField]
    private float m_stunSeconds = 6f;

    [Tooltip("동료(플레이어)에게 적용할 세기 배율. 캡슐 넉백이라 래그돌과 같은 세기면 과하다")]
    [Range(0f, 2f)]
    [SerializeField]
    private float m_playerLaunchScale = 0.6f;

    protected override string WeaponLogName => "홈런 진압봉";

    /// <summary>
    /// 유효타 확정 뒤 — NPC는 래그돌 발사, 동료는 캡슐 넉백. 데미지·반응 이후에 불리므로
    /// 여기서는 순수하게 "날린다"만 담당한다. (#815)
    /// </summary>
    protected override void ServerOnHitLanded(
        NpcController npc,
        PlayerHealth player,
        Vector3 swingDirection,
        Transform holder
    )
    {
        Vector3 horizontal = new Vector3(swingDirection.x, 0f, swingDirection.z);
        if (horizontal.sqrMagnitude < 0.0001f)
        {
            // 수직에 가깝게 조준한 경우(위·아래) — 수평 성분이 없으면 발사 방향을 만들 수 없으므로
            // 소지자가 보는 방향을 폴백으로 쓴다.
            horizontal = new Vector3(holder.forward.x, 0f, holder.forward.z);
        }
        horizontal.Normalize();

        if (npc != null)
        {
            Vector3 impulse =
                horizontal * m_launchSpeed + Vector3.up * (m_launchSpeed * m_liftRatio);
            npc.Knockback.ServerLaunchRagdoll(impulse, m_stunSeconds, holder);
            return;
        }

        if (player != null)
        {
            Vector3 knockback =
                horizontal * (m_launchSpeed * m_playerLaunchScale)
                + Vector3.up * (m_launchSpeed * m_liftRatio * m_playerLaunchScale);
            ServerLaunchPlayer(player, knockback);
        }
    }

    // ---- 동료 넉백 (#815) ----
    //
    // PlayerMovement.AddKnockback은 오너 로컬이다 — 이동 권한이 오너라 서버가 남의 캐릭터를 직접
    // 밀면 다음 위치 전파에 덮인다. TrafficVehicle.ServerHitPlayer와 같은 패턴: 오프라인은 로컬
    // 직접 호출, 세션 중이면 피격자 오너에게만 RPC를 보내 그쪽에서 스스로 적용한다.
    // RPC는 이 아이템 자신의 NetworkObject로 보낸다 — 대상(플레이어)이 아니라 때린 무기가 발신자다.
    private void ServerLaunchPlayer(PlayerHealth player, Vector3 knockback)
    {
        if (!IsSpawned)
        {
            player.GetComponent<PlayerMovement>()?.AddKnockback(knockback);
            return;
        }

        LaunchPlayerRpc(knockback, RpcTarget.Single(player.OwnerClientId, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void LaunchPlayerRpc(Vector3 knockback, RpcParams rpcParams)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.LocalClient.PlayerObject == null)
        {
            return;
        }

        nm.LocalClient.PlayerObject.GetComponent<PlayerMovement>()?.AddKnockback(knockback);
    }
}
