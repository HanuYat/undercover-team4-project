using UnityEngine;

/// <summary>
/// 유치장 경보등 사이렌 — <see cref="JailAlarmBeacon"/>의 <b>해제 시도</b> 경보에만 운다. (#311/#493)
///
/// 빛과 따로 두는 이유는 <b>3D 사이렌이 곧 "경보등 쪽을 봐라"라는 신호</b>여서다 — 본부 담당자가
/// 다른 곳을 보고 있어도 소리로 먼저 알아채고 고개를 돌린다. 감쇠 거리 밖의 현장 요원에게는
/// 들리지 않으므로 무전으로 알려야 한다는 분업은 그대로다.
///
/// <b>탈옥(자물쇠 열림)에는 울리지 않는다</b> — 사이렌은 아직 막을 수 있는 동안 재촉하는 소리다.
/// 이미 열린 뒤는 빨간 점멸이 맡는다.
///
/// 루프라 효과음 풀을 쓸 수 없어(풀은 오래된 소리를 뺏는다) 자기 <see cref="AudioSource"/>로
/// 직접 튼다 — <see cref="BombCountdownSound"/>와 같은 이유다.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class JailAlarmBeaconSound : MonoBehaviour
{
    [Tooltip("해제 시도 경보가 도는 동안 계속 낼 소리(루프). 카탈로그에 클립이 없으면 조용히 무음")]
    [SerializeField]
    private EAudioClip m_sound = EAudioClip.JailAlarm;

    [Tooltip("대상 경보등 (비우면 자신·부모에서 찾는다)")]
    [SerializeField]
    private JailAlarmBeacon m_beacon;

    private AudioSource m_source;
    private bool m_playing;

    private void Awake()
    {
        if (m_beacon == null)
            m_beacon = GetComponentInParent<JailAlarmBeacon>();

        m_source = GetComponent<AudioSource>();
        m_source.playOnAwake = false;
        m_source.loop = true;
        m_source.spatialBlend = 1f; // 완전 3D — 본부 어느 쪽에서 우는지가 정보다
        m_source.rolloffMode = AudioRolloffMode.Linear;

        if (m_beacon == null)
        {
            Debug.LogWarning("JailAlarmBeaconSound: JailAlarmBeacon을 찾지 못해 사이렌이 울리지 않는다", this);
            enabled = false;
        }
    }

    private void OnEnable() => GameSettings.OnSfxVolumeChanged += HandleSfxVolumeChanged;

    private void OnDisable()
    {
        GameSettings.OnSfxVolumeChanged -= HandleSfxVolumeChanged;
        Stop();
    }

    // 사이렌은 계속 도는 루프라 슬라이더를 끄는 동안 스스로 되읽어야 한다
    private void HandleSfxVolumeChanged(float _)
    {
        if (m_source != null && m_playing)
            m_source.volume = SoundManager.SfxVolumeOf(App.Sound?.GetSfxEntry(m_sound));
    }

    // 경보등의 상태를 매 프레임 읽는다 — 자물쇠 신호는 이미 전 피어에 오므로 각자 자기 쪽에서 운다.
    // 시도 중에 탈옥이 성사되면 그 프레임에 멎는다 (경보등은 빨강으로 넘어간다)
    private void Update()
    {
        if (m_beacon.IsAttemptAlarming)
            Play();
        else
            Stop();
    }

    // 이미 울고 있으면 건드리지 않는다 — 매 프레임 다시 걸면 시작만 반복하며 안 울린다
    private void Play()
    {
        if (m_playing)
            return;

        AudioLibrary.Entry entry = App.Sound?.GetSfxEntry(m_sound);
        if (entry?.Clip == null)
            return; // 카탈로그 미배정 — 조용히 넘어간다

        m_source.clip = entry.Clip;
        m_source.volume = SoundManager.SfxVolumeOf(entry);
        m_source.minDistance = entry.MinDistance;
        // 최대 거리가 최소보다 작게 배선되면 Unity가 감쇠를 계산하지 못한다 (SoundManager와 같은 보정)
        m_source.maxDistance = Mathf.Max(entry.MaxDistance, entry.MinDistance + 0.1f);
        m_source.Play();
        m_playing = true;
    }

    private void Stop()
    {
        if (!m_playing)
            return;

        m_source.Stop();
        m_source.clip = null;
        m_playing = false;
    }
}
