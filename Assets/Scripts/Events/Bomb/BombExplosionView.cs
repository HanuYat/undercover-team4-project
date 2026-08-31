using UnityEngine;

/// <summary>
/// 폭발 연출 — 폭심에 이펙트를 띄우고 폭발음을 낸다. (#232 표현 계층)
///
/// 순수 표현이다. 전 피어에 도달하는 <see cref="BombDevice.OnExploded"/>를 구독해 각자 한 번씩 낸다.
///
/// <b>넉백은 더 이상 여기서 하지 않는다</b> — 예전에는 각 피어가 자기 오너 캐릭터를
/// <c>PlayerMovement.AddKnockback</c>으로 밀었지만, 지금은 죽었든 살았든 서버가
/// <see cref="BombBlast"/>에서 래그돌로 날린다. 그래서 세기 계산도 가림 판정도 이 뷰에는 없다.
/// </summary>
public class BombExplosionView : MonoBehaviour
{
    [Tooltip("폭발 지점에 스폰할 이펙트 — 비워두면 소리만 난다")]
    [SerializeField]
    private BombExplosionVfx m_explosionVfx;

    [Tooltip("폭발음 — 카탈로그에 클립이 없으면 조용히 무음")]
    [SerializeField]
    private EAudioClip m_explosionSound = EAudioClip.BombExplosion;

    private BombDevice m_device;

    private void OnEnable()
    {
        m_device = GetComponentInParent<BombDevice>();
        if (m_device != null)
            m_device.OnExploded += HandleExploded;
    }

    private void OnDisable()
    {
        if (m_device != null)
            m_device.OnExploded -= HandleExploded;
    }

    private void HandleExploded()
    {
        if (m_device == null)
            return;

        SpawnVfx();

        // 폭심에서 3D로 낸다. OnExploded가 이미 전 피어에 도달하므로 각자 자기 쪽에서 한 번씩 내면 되고,
        // 별도 RPC가 필요 없다.
        App.Sound?.PlaySfxAt(m_explosionSound, m_device.transform.position);
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
