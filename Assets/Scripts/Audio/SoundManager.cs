using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 소리 재생 단일 창구 — 효과음(3D/2D 원샷)과 BGM. <see cref="App"/>.Sound로 접근한다. (#478, #483)
///
/// AppBootstrap에 상주한다(DontDestroyOnLoad). <b>BGM이 씬 전환을 넘어 이어지는 근거가 이것이다</b> —
/// 매니저가 씬과 함께 죽으면 곡도 함께 끊긴다. 씬이 바뀌면 <see cref="App.OnSceneLoaded"/>를 받아
/// 카탈로그의 씬 표대로 곡을 갈되, <b>같은 곡이면 건드리지 않는다</b>(타이틀→로비처럼 곡을 공유하는
/// 구간에서 처음부터 다시 시작하면 전환이 오히려 드러난다).
///
/// <b>전역 음량은 건드리지 않는다</b> — <see cref="GameSettings"/>가 이미 <c>AudioListener.volume</c>으로
/// 배율을 걸고 있다(#225). 이 매니저는 재생만 한다.
///
/// <b>3D 재생은 리스너가 로컬 플레이어에 있어야 의미가 있다</b> (#482). 리스너가 씬 카메라에 고정돼
/// 있으면 모든 소리가 그 지점 기준으로 계산돼 거리감이 사라진다.
///
/// <b>2D와 3D를 나누는 기준은 "누구에게 들려야 하는가"다</b> — 세상에서 난 소리는 3D
/// (<see cref="PlaySfxAt"/>), 행위자 본인에게만 들려야 하는 확인음·UI음은 2D
/// (<see cref="PlaySfx2D"/>). 후자를 3D로 내면 위치가 필요 없는 소리에 억지 좌표를 붙이게 되고,
/// 옆 사람에게도 들려 "내가 맞췄다"는 신호가 아니게 된다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class SoundManager : CommonManagerBase
{
    [Tooltip("오디오 카탈로그 — 비우면 모든 재생 요청이 무동작한다")]
    [SerializeField] private AudioLibrary m_library;

    // 동시에 울릴 수 있는 효과음 수. 넘치면 가장 오래 울린 것을 뺏으므로 소리가 사라지지는 않고
    // 앞의 것이 잘린다. 짧은 타격음·발소리 기준으로 16이면 넉넉하다.
    [Tooltip("동시 재생 가능한 효과음 개수. 모자라면 가장 오래 재생 중인 소리를 끊고 재사용한다")]
    [Min(1)]
    [SerializeField] private int m_sourceCount = 16;

    [Tooltip("BGM을 갈아탈 때 겹쳐 넘기는 시간(초). 0이면 즉시 바뀐다")]
    [Min(0f)]
    [SerializeField] private float m_bgmFadeSeconds = 1.2f;

    private readonly Dictionary<EAudioClip, AudioLibrary.Entry> m_entries = new();
    private readonly Dictionary<EBgm, AudioLibrary.BgmEntry> m_bgmEntries = new();
    private readonly Dictionary<EScene, EBgm> m_sceneBgm = new();

    private AudioSource[] m_sources;

    // 각 소스가 언제 재생을 시작했는지 — 전부 사용 중일 때 뺏을 대상을 고르는 기준.
    private float[] m_startTimes;

    // BGM 소스 2개를 번갈아 쓴다 — 크로스페이드는 두 곡이 잠깐 동시에 울려야 성립한다.
    private AudioSource[] m_bgmSources;
    private int m_bgmActive = -1; // 지금 '트는 중'인 소스 (없으면 -1)

    // 페이드는 0~1 진행도로 다루고 실제 볼륨은 곡별 배율을 곱해 낸다 — 곡마다 볼륨이 달라도
    // 페이드에 걸리는 시간은 같아야 한다.
    private float[] m_bgmGain;
    private float[] m_bgmGainTarget;
    private float[] m_bgmVolume;

    // 배선 사고를 알리되 매 프레임 도배하지 않는다.
    private readonly HashSet<EAudioClip> m_warned = new();
    private readonly HashSet<EBgm> m_warnedBgm = new();

    /// <summary>지금 틀고 있는 BGM — 없으면 <see cref="EBgm.None"/>.</summary>
    public EBgm CurrentBgm { get; private set; } = EBgm.None;

    protected override void Awake()
    {
        base.Awake(); // App.Sound 등록 (R5)

        BuildIndex();
        BuildSources();
    }

    // 씬 구독은 Start에서 — 매니저 등록(Awake)이 전부 끝난 뒤에 붙는다 (R6).
    // 구독 전에 이미 들어와 있는 씬이 있으므로(부트스트랩이 뜬 그 씬) 현재 씬을 한 번 반영하고 시작한다.
    private void Start()
    {
        App.OnSceneLoaded += HandleSceneLoaded;
        ApplySceneBgm(App.CurrentScene);
    }

    protected override void OnDestroy()
    {
        App.OnSceneLoaded -= HandleSceneLoaded;
        base.OnDestroy(); // App.Sound 등록 해제 (R5)
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
        source.spatialBlend = 1f; // 완전 3D — 거리·방향이 그대로 반영된다 (#482)
        source.clip = entry.Clip;
        source.volume = entry.Volume;
        source.minDistance = entry.MinDistance;
        // 최대 거리가 최소보다 작게 배선되면 Unity가 감쇠를 계산하지 못한다 — 사고를 조용히 삼키지 않고 보정한다.
        source.maxDistance = Mathf.Max(entry.MaxDistance, entry.MinDistance + 0.1f);
        source.Play();
    }

    /// <summary>
    /// 효과음을 위치 없이(2D) 한 번 재생한다 — <b>듣는 사람 본인에게만</b> 의미가 있는 소리용.
    /// UI 클릭, 조준 확인음, "내가 맞췄다" 히트마커처럼 세상에 난 소리가 아닌 것들이다.
    /// 거리·방향 감쇠가 없어 리스너 위치와 무관하게 같은 크기로 들린다.
    /// </summary>
    /// <param name="id">카탈로그 키. <see cref="EAudioClip.None"/>이면 무동작이다</param>
    public void PlaySfx2D(EAudioClip id)
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

        // 2D는 거리 항목을 쓰지 않는다 — 감쇠가 없으므로 min/maxDistance가 결과에 관여하지 않는다.
        source.spatialBlend = 0f;
        source.clip = entry.Clip;
        source.volume = entry.Volume;
        source.Play();
    }

    // ---- BGM ----

    /// <summary>
    /// BGM을 건다 — 이미 같은 곡이면 아무것도 하지 않는다(처음부터 다시 시작하지 않는다).
    /// 다른 곡이면 <see cref="m_bgmFadeSeconds"/>에 걸쳐 겹쳐 넘긴다.
    /// </summary>
    /// <param name="id">카탈로그 키. <see cref="EBgm.None"/>이면 <see cref="StopBgm"/>과 같다</param>
    public void PlayBgm(EBgm id)
    {
        if (id == CurrentBgm)
            return;

        if (id == EBgm.None)
        {
            StopBgm();
            return;
        }

        if (!m_bgmEntries.TryGetValue(id, out AudioLibrary.BgmEntry entry))
        {
            WarnOnceBgm(id, $"카탈로그에 BGM {id} 항목이 없다");
            return;
        }

        // 클립 미배정은 사고가 아니라 '아직 안 채움'이다 — 조용히 무음으로 두되 현재 곡은 끈다.
        // 여기서 그냥 돌아가면 이전 곡이 새 씬까지 따라와 더 헷갈린다.
        if (entry.Clip == null)
        {
            StopBgm();
            CurrentBgm = id; // 같은 씬을 다시 요청해도 재시도하지 않게 기록은 남긴다
            return;
        }

        int next = m_bgmActive == 0 ? 1 : 0;

        m_bgmSources[next].clip = entry.Clip;
        m_bgmSources[next].volume = 0f;
        m_bgmSources[next].Play();

        m_bgmVolume[next] = entry.Volume;
        m_bgmGain[next] = 0f;
        m_bgmGainTarget[next] = 1f;

        if (m_bgmActive >= 0)
            m_bgmGainTarget[m_bgmActive] = 0f; // 이전 곡은 물러난다 — 다 빠지면 Update가 멈춘다

        m_bgmActive = next;
        CurrentBgm = id;
    }

    /// <summary>BGM을 끈다 — 페이드 아웃 후 정지한다.</summary>
    public void StopBgm()
    {
        if (m_bgmActive >= 0)
            m_bgmGainTarget[m_bgmActive] = 0f;

        m_bgmActive = -1;
        CurrentBgm = EBgm.None;
    }

    // 페이드 진행. 곡이 하나도 안 울릴 때는 아무 일도 하지 않는다.
    //
    // unscaledDeltaTime을 쓰는 이유 — 일시정지·정산 연출에서 timeScale이 0이 되면 스케일 시간으로는
    // 페이드가 그 자리에 멈춰 곡이 어정쩡하게 반쯤 겹친 채 남는다.
    private void Update()
    {
        if (m_bgmSources == null)
            return;

        float step = m_bgmFadeSeconds > 0f
            ? Time.unscaledDeltaTime / m_bgmFadeSeconds
            : 1f; // 페이드 0 = 즉시

        for (int i = 0; i < m_bgmSources.Length; i++)
        {
            if (Mathf.Approximately(m_bgmGain[i], m_bgmGainTarget[i]))
                continue;

            m_bgmGain[i] = Mathf.MoveTowards(m_bgmGain[i], m_bgmGainTarget[i], step);
            m_bgmSources[i].volume = m_bgmGain[i] * m_bgmVolume[i];

            // 다 빠진 소스는 멈춰 둔다 — 볼륨 0으로 계속 도는 소스를 남기지 않는다.
            if (m_bgmGain[i] <= 0f && m_bgmSources[i].isPlaying)
                m_bgmSources[i].Stop();
        }
    }

    // ---- 씬 연동 ----

    private void HandleSceneLoaded(EScene scene) => ApplySceneBgm(scene);

    // 씬 표에 없는 씬은 무음이다. 표가 통째로 비어 있어도(음원 배정 전) 조용히 무음이 될 뿐이라
    // 경고하지 않는다 — 클립 미배정과 같은 취급이다.
    private void ApplySceneBgm(EScene scene)
    {
        PlayBgm(m_sceneBgm.TryGetValue(scene, out EBgm bgm) ? bgm : EBgm.None);
    }

    // ---- 카탈로그 ----

    private void BuildIndex()
    {
        if (m_library == null)
        {
            Debug.LogWarning($"[SoundManager] 오디오 카탈로그가 배정되지 않았다 — 모든 소리가 나지 않는다. {name}에 지정할 것", this);
            return;
        }

        foreach (AudioLibrary.Entry entry in m_library.Entries)
        {
            if (entry == null || entry.Id == EAudioClip.None)
                continue;

            if (!m_entries.TryAdd(entry.Id, entry))
                Debug.LogError($"[SoundManager] 카탈로그에 {entry.Id}가 중복 등록됐다 — 먼저 오는 항목만 쓰인다", m_library);
        }

        foreach (AudioLibrary.BgmEntry entry in m_library.BgmEntries)
        {
            if (entry == null || entry.Id == EBgm.None)
                continue;

            if (!m_bgmEntries.TryAdd(entry.Id, entry))
                Debug.LogError($"[SoundManager] 카탈로그에 BGM {entry.Id}가 중복 등록됐다 — 먼저 오는 항목만 쓰인다", m_library);
        }

        foreach (AudioLibrary.SceneBgmEntry entry in m_library.SceneBgmEntries)
        {
            if (entry == null || entry.Scene == EScene.None)
                continue;

            if (!m_sceneBgm.TryAdd(entry.Scene, entry.Bgm))
                Debug.LogError($"[SoundManager] 씬 표에 {entry.Scene}이 중복 등록됐다 — 먼저 오는 항목만 쓰인다", m_library);
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
            // spatialBlend는 재생할 때마다 정한다 — 같은 풀을 3D(PlaySfxAt)와 2D(PlaySfx2D)가 함께 쓴다.
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear; // 로그 감쇠는 근거리에서 너무 급격하다
            m_sources[i] = source;
        }

        BuildBgmSources();
    }

    // BGM은 효과음 풀에서 빌리지 않는다 — 루프로 계속 물고 있어야 하는데 풀은 오래된 것을 뺏는
    // 정책이라, 효과음이 몰리는 순간 BGM이 잘려 나간다. 크로스페이드를 위해 2개를 둔다.
    private void BuildBgmSources()
    {
        m_bgmSources = new AudioSource[2];
        m_bgmGain = new float[2];
        m_bgmGainTarget = new float[2];
        m_bgmVolume = new float[2];

        for (int i = 0; i < m_bgmSources.Length; i++)
        {
            var host = new GameObject($"BgmSource_{i}");
            host.transform.SetParent(transform, false);

            AudioSource source = host.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = true;
            source.spatialBlend = 0f; // 2D — 화면 밖 어디에서 나는 소리가 아니다
            source.volume = 0f;
            m_bgmSources[i] = source;
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

    private void WarnOnceBgm(EBgm id, string reason)
    {
        if (m_warnedBgm.Add(id))
            Debug.LogWarning($"[SoundManager] {reason} — 해당 BGM을 건너뛴다", this);
    }
}
