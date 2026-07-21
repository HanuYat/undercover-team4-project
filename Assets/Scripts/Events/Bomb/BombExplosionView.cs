using UnityEngine;

/// <summary>
/// 폭발 넉백 연출(플레이어) — 폭발 시 반경 안의 플레이어를 폭심에서 바깥으로 밀어낸다. (#232 표현 계층)
///
/// 순수 표현이다. 피해·패닉 판정과 NPC 넉백은 서버가 <see cref="BombDevice"/>에서 이미 끝냈고,
/// 이 뷰는 전 피어에 도달하는 <see cref="BombDevice.OnResolved"/>를 구독해
/// <b>각 피어가 자기 소유 플레이어만</b> 민다.
///
/// 플레이어만 여기서 처리하는 이유: 이동 권한이 오너에게 있어(CharacterController + 오너 권한
/// NetworkTransform) 서버가 남의 캐릭터를 옮겨봤자 오너의 다음 위치 전파에 덮여 되돌아간다.
/// 폭발 사실 자체는 이미 동기화돼 있으므로 별도 RPC 없이 각 피어가 같은 결과를 낸다.
/// (반대로 NPC는 이동 권한이 서버에 있어 <see cref="BombDevice"/>가 직접 날린다)
///
/// 세기·반경·감쇠는 전부 <see cref="BombDevice.EvaluateKnockback"/>이 계산한다 — 사람과 시민이
/// 같은 폭발에서 다르게 날아가지 않도록 넉백 식을 장치 한 곳에 둔다.
/// </summary>
public class BombExplosionView : MonoBehaviour
{
    [Tooltip("폭발 지점에 스폰할 이펙트 — 비워두면 넉백만 일어난다")]
    [SerializeField]
    private BombExplosionVfx m_explosionVfx;

    private BombDevice m_device;

    private void OnEnable()
    {
        m_device = GetComponentInParent<BombDevice>();
        if (m_device != null)
            m_device.OnResolved += HandleResolved;
    }

    private void OnDisable()
    {
        if (m_device != null)
            m_device.OnResolved -= HandleResolved;
    }

    private void HandleResolved(bool defused)
    {
        if (defused || m_device == null)
            return; // 해체 성공은 넉백·이펙트 없음

        SpawnVfx();

        // 오너가 아닌 인스턴스에서는 AddKnockback이 스스로 무시하므로 전부 훑어도 안전하다
        // (플레이어는 최대 6명 — SuddenEventUtil이 쓰는 것과 같은 전수 순회 관례).
        PlayerMovement[] players = FindObjectsByType<PlayerMovement>(FindObjectsSortMode.None);
        for (int i = 0; i < players.Length; i++)
        {
            PlayerMovement player = players[i];
            if (player == null)
                continue;

            Vector3 knockback = m_device.EvaluateKnockback(player.transform.position);
            if (knockback != Vector3.zero)
                player.AddKnockback(knockback);
        }
    }

    // 폭발 이펙트는 폭탄의 자식으로 두지 않는다 — 폭탄은 몇 초 뒤 디스폰되므로 연기가 중간에 끊긴다.
    // 월드에 독립 스폰하고 수명은 이펙트가 스스로 관리한다(BombExplosionVfx).
    private void SpawnVfx()
    {
        if (m_explosionVfx == null)
            return;

        Instantiate(m_explosionVfx, transform.position, Quaternion.identity);
    }
}
