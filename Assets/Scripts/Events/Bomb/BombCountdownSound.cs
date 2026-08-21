using UnityEngine;

/// <summary>
/// 폭탄 카운트다운 삐 소리 — 남은 시간에 따라 느린 삐와 빠른 삐를 갈아 낸다. (#232 표현 계층)
///
/// <b>소리가 곧 정보다</b> — 폭탄은 등 뒤에서 쫓아와 화면 표시가 안 읽히므로, 삐 간격이 남은 시간을
/// 3D 음향이 방향을 대신 말한다. <b>루프라 효과음 풀을 쓸 수 없어</b>(풀은 오래된 소리를 뺏는다)
/// 자기 <see cref="AudioSource"/>로 직접 튼다 — <see cref="RunawayVehicle"/> 엔진음과 같은 이유다.
/// <b>빠른 삐는 폭탄이 멈추는 순간에 맞춘다</b> — 잠금 시간(<see cref="BombDevice"/>, 기본 3초) 아래로
/// 내려가면 폭심이 확정되고 그때가 마지막 기회다. 값은 장치 쪽과 같게 두는 것이 기본이다.
/// 매 프레임 장치를 읽는 것은 <see cref="BombTimerView"/>와 같다 — RPC 없이 각 피어가 같은 소리를 낸다.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class BombCountdownSound : MonoBehaviour
{
    [Tooltip("평상시 카운트 — 느린 삐(루프). 카탈로그에 클립이 없으면 조용히 무음")]
    [SerializeField]
    private EAudioClip m_slowSound = EAudioClip.BombCountdownSlow;

    [Tooltip("임박 — 빠른 삐(루프). 카탈로그에 클립이 없으면 조용히 무음")]
    [SerializeField]
    private EAudioClip m_fastSound = EAudioClip.BombCountdownFast;

    [Tooltip("남은 시간이 이 값(초) 이하면 빠른 삐로 바꾼다. 폭탄이 그 자리에 멈추는 시각" +
             "(BombDevice의 잠금 시간, 기본 3초)과 맞춰 두면 소리가 '폭심이 확정됐다'를 알린다")]
    [Min(0f)]
    [SerializeField]
    private float m_fastThresholdSeconds = 3f;

    private BombDevice m_device;
    private AudioSource m_source;
    private EAudioClip m_playing = EAudioClip.None;

    private void Awake()
    {
        m_device = GetComponentInParent<BombDevice>();

        m_source = GetComponent<AudioSource>();
        m_source.playOnAwake = false;
        m_source.loop = true;
        m_source.spatialBlend = 1f; // 완전 3D — 어느 쪽에서 쫓아오는지가 정보의 절반이다
        m_source.rolloffMode = AudioRolloffMode.Linear;
    }

    private void OnEnable()
    {
        if (m_device != null)
            m_device.OnExploded += HandleExploded;
    }

    private void OnDisable()
    {
        if (m_device != null)
            m_device.OnExploded -= HandleExploded;

        Stop();
    }

    // 폭발음과 겹치지 않게 그 순간 바로 끊는다. Update의 IsCountingDown 폴링만 믿으면 최대 한 프레임이
    // 남는데, 하필 그 프레임이 폭발음이 시작되는 프레임이라 둘이 합산돼 풀스케일을 넘고 소리가 찢어진다.
    private void HandleExploded() => Stop();

    private void Update()
    {
        // 카운트다운 중이 아닌 국면(등장·대기·폭발 후)은 전부 무음이다. 상자에서 나오기 전부터
        // 삐 소리가 나면 무장 시각이 어긋나 보이고, 터진 뒤에 남으면 잔해가 계속 우는 꼴이 된다.
        if (m_device == null || !m_device.IsCountingDown)
        {
            Stop();
            return;
        }

        Play(m_device.RemainingSeconds <= m_fastThresholdSeconds ? m_fastSound : m_slowSound);
    }

    // 이미 같은 소리가 돌고 있으면 건드리지 않는다 — 매 프레임 다시 걸면 삐가 시작만 반복하며 안 울린다
    private void Play(EAudioClip id)
    {
        if (m_playing == id)
            return;

        AudioLibrary.Entry entry = App.Sound?.GetSfxEntry(id);
        if (entry?.Clip == null)
        {
            Stop(); // 카탈로그 미배정 — 이전 소리는 끊어 두고 조용히 넘어간다
            return;
        }

        m_source.clip = entry.Clip;
        m_source.volume = entry.Volume;
        m_source.minDistance = entry.MinDistance;
        // 최대 거리가 최소보다 작게 배선되면 Unity가 감쇠를 계산하지 못한다 (SoundManager와 같은 보정)
        m_source.maxDistance = Mathf.Max(entry.MaxDistance, entry.MinDistance + 0.1f);
        m_source.Play();
        m_playing = id;
    }

    private void Stop()
    {
        if (m_playing == EAudioClip.None)
            return;

        m_source.Stop();
        m_source.clip = null;
        m_playing = EAudioClip.None;
    }
}
