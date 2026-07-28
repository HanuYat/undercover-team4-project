using Unity.Netcode;
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

    [Header("들리는 범위")]
    [Tooltip("이 거리(m)를 넘으면 벨소리가 들리지 않는다 — 본부 밖 현장까지 새어 나가지 않게 하는 값")]
    [Min(1f)]
    [SerializeField] private float m_audibleDistance = 18f;

    [Tooltip("이 거리(m) 안에서는 최대 음량으로 들린다")]
    [Min(0f)]
    [SerializeField] private float m_fullVolumeDistance = 4f;

    [Tooltip("최대 음량")]
    [Range(0f, 1f)]
    [SerializeField] private float m_maxVolume = 1f;

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

    // 로컬 플레이어의 귀 위치 — 거리 감쇠 기준. 스폰 전엔 없을 수 있어 매번 재확인한다.
    private Transform m_listener;

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
        if (m_phone == null || !m_phone.IsRinging)
            return;

        ApplyDistanceVolume();

        if (m_handle == null)
            return;

        // 멀리서도 "지금 울린다"가 보이는 게 목적이다 — 본부를 비운 사이 돌아왔을 때
        // 소리만으로는 어느 쪽인지 모르므로 물건 자체가 움직여야 한다.
        float t = Time.time * m_shakeSpeed;
        m_handle.localRotation = m_handleBaseRotation * Quaternion.Euler(0f, 0f, Mathf.Sin(t) * m_shakeAngle);
        m_handle.localPosition = m_handleBasePosition + new Vector3(0f, Mathf.Abs(Mathf.Sin(t)) * m_shakeHeight, 0f);
    }

    /// <summary>
    /// 거리에 따라 음량을 직접 깎는다 — 본부 안에서만 들리게 하는 장치. (#102)
    ///
    /// Unity의 3D 감쇠(spatialBlend 1)를 쓰지 않는 이유: 이 프로젝트에는 <b>플레이어를 따라다니는
    /// AudioListener가 없다.</b> 유일한 리스너가 씬 Main Camera에 고정돼 있어 3D로 두면 본부에서도
    /// 100m 밖으로 판정돼 아무 소리도 안 난다. 그래서 소스는 2D로 두고 감쇠만 여기서 계산한다.
    ///
    /// 리스너를 로컬 플레이어에 붙이는 정비가 끝나면 이 함수를 지우고 spatialBlend를 1로 되돌리는 게 맞다.
    /// </summary>
    private void ApplyDistanceVolume()
    {
        if (m_source == null)
            return;

        Transform listener = ResolveListener();
        if (listener == null)
        {
            m_source.volume = 0f; // 들을 사람이 없다 — 소리를 내지 않는다
            return;
        }

        float distance = Vector3.Distance(listener.position, transform.position);

        // 가까우면 최대, 멀어지면 선형으로 줄어 audibleDistance에서 0
        float far = Mathf.Max(m_audibleDistance, m_fullVolumeDistance + 0.01f);
        float t = Mathf.InverseLerp(far, m_fullVolumeDistance, distance);
        m_source.volume = m_maxVolume * Mathf.Clamp01(t);
    }

    // 로컬 플레이어 카메라 → 없으면 메인 카메라 (PlayerNameTag와 같은 관례)
    private Transform ResolveListener()
    {
        if (m_listener != null)
            return m_listener;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.LocalClient != null && nm.LocalClient.PlayerObject != null)
        {
            Camera cam = nm.LocalClient.PlayerObject.GetComponentInChildren<Camera>();
            if (cam != null)
            {
                m_listener = cam.transform; // 플레이어를 잡았을 때만 캐시한다
                return m_listener;
            }
        }

        // 아직 스폰 전 — 폴백은 캐시하지 않는다. 씬 Main Camera는 로드 직후부터 존재하므로
        // 여기서 캐시해 버리면 플레이어가 스폰된 뒤에도 영영 그 고정 위치로 거리를 재게 되고,
        // 본부 한복판에서도 100m 밖으로 판정돼 벨소리가 영구 무음이 된다.
        Camera fallback = Camera.main;
        return fallback != null ? fallback.transform : null;
    }

    private void RestoreHandle()
    {
        if (m_handle == null)
            return;

        m_handle.localPosition = m_handleBasePosition;
        m_handle.localRotation = m_handleBaseRotation;
    }
}
