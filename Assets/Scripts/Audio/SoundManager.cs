using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 효과음 재생 — 위치 지정 3D 원샷 + <see cref="AudioSource"/> 풀. <see cref="App"/>.Sound로 접근한다. (#478)
///
/// <b>#483의 얇은 선취분이다.</b> BGM·씬 전환 유지·UI(2D)음은 여기 없다 — #483이 이 위에 얹는다.
/// 그래서 AppBootstrap에 상주시킨다(DontDestroyOnLoad): 지금 필요한 건 SFX뿐이지만, 나중에 BGM이
/// 들어올 자리를 미리 잡아 두면 배선을 두 번 하지 않는다.
///
/// <b>전역 음량은 건드리지 않는다</b> — <see cref="GameSettings"/>가 이미 <c>AudioListener.volume</c>으로
/// 배율을 걸고 있다(#225). 이 매니저는 재생만 한다.
///
/// <b>3D 재생은 리스너가 로컬 플레이어에 있어야 의미가 있다</b> (#482). 리스너가 씬 카메라에 고정돼
/// 있으면 모든 소리가 그 지점 기준으로 계산돼 거리감이 사라진다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class SoundManager : CommonManagerBase
{
    [Tooltip("효과음 카탈로그 — 비우면 모든 재생 요청이 무동작한다")]
    [SerializeField] private AudioLibrary m_library;

    // 동시에 울릴 수 있는 효과음 수. 넘치면 가장 오래 울린 것을 뺏으므로 소리가 사라지지는 않고
    // 앞의 것이 잘린다. 짧은 타격음·발소리 기준으로 16이면 넉넉하다.
    [Tooltip("동시 재생 가능한 효과음 개수. 모자라면 가장 오래 재생 중인 소리를 끊고 재사용한다")]
    [Min(1)]
    [SerializeField] private int m_sourceCount = 16;

    private readonly Dictionary<EAudioClip, AudioLibrary.Entry> m_entries = new();
    private AudioSource[] m_sources;

    // 각 소스가 언제 재생을 시작했는지 — 전부 사용 중일 때 뺏을 대상을 고르는 기준.
    private float[] m_startTimes;

    // 배선 사고를 알리되 매 프레임 도배하지 않는다.
    private readonly HashSet<EAudioClip> m_warned = new();

    protected override void Awake()
    {
        base.Awake(); // App.Sound 등록 (R5)

        BuildIndex();
        BuildSources();
    }

    /// <summary>
    /// 효과음을 지정한 월드 좌표에서 한 번 재생한다. 로컬 재생이다 — 부르는 피어에서만 들린다.
    /// 서버 권위 이벤트라면 각 피어가 자기 쪽에서 이 함수를 부르도록 전파할 것.
    /// </summary>
    /// <param name="id">카탈로그 키. <see cref="EAudioClip.None"/>이면 무동작이다</param>
    /// <param name="position">소리가 날 월드 좌표</param>
    public void PlaySfxAt(EAudioClip id, Vector3 position)
    {
        if (id == EAudioClip.None)
            return;

        if (!m_entries.TryGetValue(id, out AudioLibrary.Entry entry))
        {
            WarnOnce(id, $"카탈로그에 {id} 항목이 없다");
            return;
        }

        if (entry.Clip == null)
        {
            WarnOnce(id, $"{id} 항목에 클립이 배정되지 않았다");
            return;
        }

        AudioSource source = RentSource();
        if (source == null)
            return;

        source.transform.position = position;
        source.clip = entry.Clip;
        source.volume = entry.Volume;
        source.minDistance = entry.MinDistance;
        // 최대 거리가 최소보다 작게 배선되면 Unity가 감쇠를 계산하지 못한다 — 사고를 조용히 삼키지 않고 보정한다.
        source.maxDistance = Mathf.Max(entry.MaxDistance, entry.MinDistance + 0.1f);
        source.Play();
    }

    // OnDestroy는 재정의하지 않는다 — 정리할 자체 상태가 없고, 베이스가 App 등록 해제를 이미 한다.

    // ---- 카탈로그 ----

    private void BuildIndex()
    {
        if (m_library == null)
        {
            Debug.LogWarning($"[SoundManager] 효과음 카탈로그가 배정되지 않았다 — 모든 소리가 나지 않는다. {name}에 지정할 것", this);
            return;
        }

        foreach (AudioLibrary.Entry entry in m_library.Entries)
        {
            if (entry == null || entry.Id == EAudioClip.None)
                continue;

            if (!m_entries.TryAdd(entry.Id, entry))
                Debug.LogError($"[SoundManager] 카탈로그에 {entry.Id}가 중복 등록됐다 — 먼저 오는 항목만 쓰인다", m_library);
        }
    }

    // ---- 소스 풀 ----

    // 소스를 자식 오브젝트로 나눠 만드는 이유는 재생 위치가 제각각이기 때문이다 —
    // 한 오브젝트에 AudioSource를 여러 개 붙이면 위치를 따로 줄 수 없다.
    private void BuildSources()
    {
        m_sources = new AudioSource[m_sourceCount];
        m_startTimes = new float[m_sourceCount];

        for (int i = 0; i < m_sourceCount; i++)
        {
            var host = new GameObject($"SfxSource_{i}");
            host.transform.SetParent(transform, false);

            AudioSource source = host.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 1f; // 완전 3D — 거리·방향이 그대로 반영된다 (#482)
            source.rolloffMode = AudioRolloffMode.Linear; // 로그 감쇠는 근거리에서 너무 급격하다
            m_sources[i] = source;
        }
    }

    // 노는 소스를 우선 쓰고, 전부 사용 중이면 가장 오래 재생 중인 것을 뺏는다.
    // 짧은 타격음이라 잘려도 티가 적다 — 대사·BGM이 들어오면 이 정책을 다시 봐야 한다.
    private AudioSource RentSource()
    {
        if (m_sources == null || m_sources.Length == 0)
            return null;

        int oldest = 0;
        for (int i = 0; i < m_sources.Length; i++)
        {
            if (!m_sources[i].isPlaying)
            {
                m_startTimes[i] = Time.time;
                return m_sources[i];
            }

            if (m_startTimes[i] < m_startTimes[oldest])
                oldest = i;
        }

        m_startTimes[oldest] = Time.time;
        return m_sources[oldest];
    }

    // ---- 보조 ----

    private void WarnOnce(EAudioClip id, string reason)
    {
        if (m_warned.Add(id))
            Debug.LogWarning($"[SoundManager] {reason} — 해당 소리를 건너뛴다", this);
    }
}
