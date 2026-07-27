using UnityEngine;

/// <summary>
/// 제보 전화 울림 연출 (#102) — 벨소리와 수화기 떨림. 각 피어의 <b>로컬 표현</b>이다.
/// 울림 여부는 TipCallPhone이 서버 권위로 판정해 동기화하고(IsRinging), 여기서는 그 상태를 보고 재생만 한다.
/// 그래서 이 컴포넌트가 없거나 참조가 비어도 게임 로직에는 영향이 없다 — 소리·움직임만 사라진다.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class TipCallPhoneView : MonoBehaviour
{
    [SerializeField] private TipCallPhone m_phone;
    [SerializeField] private AudioSource m_source;

    [Header("수화기 떨림")]
    [Tooltip("울릴 때 덜그럭거릴 부품 — 보통 수화기(Handle). 비우면 소리만 나고 움직임은 없다")]
    [SerializeField] private Transform m_handle;

    [Tooltip("좌우로 기우는 최대 각도(도)")]
    [SerializeField] private float m_shakeAngle = 6f;

    [Tooltip("떨림 속도 — 벨소리 리듬과 맞출 값")]
    [SerializeField] private float m_shakeSpeed = 28f;

    [Tooltip("들썩이는 높이(m)")]
    [SerializeField] private float m_shakeHeight = 0.012f;

    // 원래 자세 — 떨림을 얹기 전 기준이고, 멈출 때 여기로 되돌린다
    private Vector3 m_handleBasePosition;
    private Quaternion m_handleBaseRotation;

    private void Awake()
    {
        if (m_source == null)
            m_source = GetComponent<AudioSource>();

        if (m_handle != null)
        {
            m_handleBasePosition = m_handle.localPosition;
            m_handleBaseRotation = m_handle.localRotation;
        }
    }

    private void OnEnable()
    {
        if (m_phone != null)
            m_phone.OnRingingChanged += Refresh;

        Refresh(); // 스폰 전이거나 다시 켜졌을 때 현재 상태로 맞춘다 (CCTVChannelLabelView와 같은 관례)
    }

    private void OnDisable()
    {
        if (m_phone != null)
            m_phone.OnRingingChanged -= Refresh;

        RestoreHandle();
    }

    private void Refresh()
    {
        if (m_source == null)
            return;

        bool ringing = m_phone != null && m_phone.IsRinging;

        // 이미 울리는 중에 Play를 다시 부르면 벨소리가 처음으로 튄다 — 상태가 바뀔 때만 건드린다
        if (ringing && !m_source.isPlaying)
            m_source.Play();
        else if (!ringing && m_source.isPlaying)
            m_source.Stop();

        if (!ringing)
            RestoreHandle();
    }

    private void Update()
    {
        if (m_handle == null || m_phone == null || !m_phone.IsRinging)
            return;

        // 멀리서도 "지금 울린다"가 보이는 게 목적이다 — 본부를 비운 사이 돌아왔을 때
        // 소리만으로는 어느 쪽인지 모르므로 물건 자체가 움직여야 한다.
        float t = Time.time * m_shakeSpeed;
        m_handle.localRotation = m_handleBaseRotation * Quaternion.Euler(0f, 0f, Mathf.Sin(t) * m_shakeAngle);
        m_handle.localPosition = m_handleBasePosition + new Vector3(0f, Mathf.Abs(Mathf.Sin(t)) * m_shakeHeight, 0f);
    }

    private void RestoreHandle()
    {
        if (m_handle == null)
            return;

        m_handle.localPosition = m_handleBasePosition;
        m_handle.localRotation = m_handleBaseRotation;
    }
}
