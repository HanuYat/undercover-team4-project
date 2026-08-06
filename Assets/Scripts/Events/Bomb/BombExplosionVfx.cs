using UnityEngine;

/// <summary>
/// 폭발 이펙트의 수명·섬광 관리 — 폭발 지점에 스폰되는 일회용 연출 오브젝트. (#232 표현 계층)
///
/// <b>순수 로컬 연출이다.</b> 각 피어의 <see cref="BombExplosionView"/>가 자기 화면에 하나씩 스폰하므로
/// 네트워크 동기화가 없다(폭발 사실 자체는 이미 <see cref="BombDevice"/>가 전파했다).
///
/// 폭탄 본체는 폭발 몇 초 뒤 디스폰되므로(<see cref="BombChaseEvent"/>의 잔류 시간), 이 오브젝트는
/// <b>폭탄의 자식이 아니라 월드에 독립적으로</b> 스폰되어 스스로 수명을 끝낸다 — 부모가 사라져도
/// 연기가 중간에 끊기지 않는다.
///
/// 파티클은 프리팹에 미리 구성돼 있고, 이 컴포넌트는 파티클이 못 하는 두 가지만 한다:
/// 점광원 섬광을 감쇠시키는 것과, 다 끝나면 자신을 정리하는 것.
/// </summary>
public class BombExplosionVfx : MonoBehaviour
{
    [Tooltip("폭발 순간의 점광원 — 비우면 자식에서 찾는다")]
    [SerializeField]
    private Light m_flash;

    [Tooltip("섬광 최대 밝기")]
    [SerializeField]
    private float m_flashIntensity = 40f;

    [Tooltip("섬광이 0까지 잦아드는 시간(초)")]
    [SerializeField]
    private float m_flashSeconds = 0.4f;

    [Tooltip("이 시간(초) 뒤 오브젝트를 정리한다 — 가장 긴 파티클 수명보다 길게 잡을 것")]
    [SerializeField]
    private float m_lifetimeSeconds = 5f;

    private float m_elapsed;

    private void Awake()
    {
        if (m_flash == null)
            m_flash = GetComponentInChildren<Light>(true);

        if (m_flash != null)
            m_flash.intensity = m_flashIntensity;
    }

    private void Update()
    {
        m_elapsed += Time.deltaTime;

        // 섬광은 폭발 순간에만 강하고 빠르게 죽는다 — 제곱 감쇠로 '번쩍'하는 느낌을 만든다
        if (m_flash != null && m_flashSeconds > 0f)
        {
            float t = Mathf.Clamp01(m_elapsed / m_flashSeconds);
            float falloff = (1f - t) * (1f - t);
            m_flash.intensity = m_flashIntensity * falloff;
            if (t >= 1f)
                m_flash.enabled = false;
        }

        if (m_elapsed >= m_lifetimeSeconds)
            Destroy(gameObject);
    }
}
