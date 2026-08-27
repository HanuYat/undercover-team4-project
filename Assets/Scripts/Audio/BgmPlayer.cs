using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// BGM 재생 — 씬별 곡 전환과 크로스페이드. <see cref="SoundManager"/>가 소유한다. (#483)
///
/// <b>매니저가 아니라 협력자다.</b> App에 등록하지 않는다 — 참조자가 <see cref="SoundManager"/> 하나뿐이고
/// 같은 오브젝트에 붙어 있어 R3(두 도메인 이상에서 참조)에 미달한다. 필요하면 <c>App.Sound.Bgm</c>으로 닿는다.
///
/// <b>효과음과 갈라 둔 이유는 구동 주체가 다르기 때문이다.</b> 효과음은 불릴 때만 도는 원샷 풀이고,
/// BGM은 페이드 때문에 매 프레임 <see cref="Update"/>를 쓰며 씬 전환을 스스로 구독한다. 한 클래스에 두면
/// "요청이 올 때 도는 것"과 "계속 도는 것"이 섞여, 소리를 하나 추가할 때마다 둘 다 읽어야 한다.
///
/// <b>씬 전환을 넘어 이어지는 근거는 상주다</b> — <see cref="SoundManager"/>가 AppBootstrap에 있어
/// (DontDestroyOnLoad) 이 컴포넌트도 함께 산다. 씬과 함께 죽으면 곡도 끊긴다.
/// 씬이 바뀌면 씬 표대로 곡을 갈되 <b>같은 곡이면 건드리지 않는다</b> — 곡을 공유하는 두 씬 사이에서
/// 처음부터 다시 시작하면 전환이 오히려 드러난다.
/// </summary>
public class BgmPlayer : MonoBehaviour
{
    [Tooltip("곡을 갈아탈 때 겹쳐 넘기는 시간(초). 0이면 즉시 바뀐다")]
    [Min(0f)]
    [SerializeField] private float m_fadeSeconds = 1.2f;

    private readonly Dictionary<EBgm, AudioLibrary.BgmEntry> m_entries = new();
    private readonly Dictionary<EScene, EBgm> m_sceneBgm = new();

    // 배선 사고를 알리되 매 프레임 도배하지 않는다.
    private readonly HashSet<EBgm> m_warned = new();

    // 소스 2개를 번갈아 쓴다 — 크로스페이드는 두 곡이 잠깐 동시에 울려야 성립한다.
    private AudioSource[] m_sources;
    private int m_active = -1; // 지금 '트는 중'인 소스 (없으면 -1)

    // 페이드는 0~1 진행도로 다루고 실제 볼륨은 곡별 배율을 곱해 낸다 — 곡마다 볼륨이 달라도
    // 페이드에 걸리는 시간은 같아야 한다.
    private float[] m_gain;
    private float[] m_gainTarget;
    private float[] m_volume;

    /// <summary>지금 틀고 있는 곡 — 없으면 <see cref="EBgm.None"/>.</summary>
    public EBgm Current { get; private set; } = EBgm.None;

    /// <summary>
    /// 카탈로그를 받아 준비한다 — <see cref="SoundManager"/>가 자기 Awake에서 부른다.
    /// 카탈로그 참조를 이쪽에도 직렬화하지 않는 이유는 배선 지점을 하나로 두기 위해서다:
    /// 인스펙터에 칸이 둘이면 한쪽만 채운 구성이 만들어진다.
    /// </summary>
    public void Initialize(AudioLibrary library)
    {
        BuildSources();
        BuildIndex(library);
    }

    // 씬 구독은 Start에서 — 매니저 등록(Awake)이 전부 끝난 뒤에 붙는다 (R6).
    // 구독 전에 이미 들어와 있는 씬이 있으므로(부트스트랩이 뜬 그 씬) 현재 씬을 한 번 반영하고 시작한다.
    private void Start()
    {
        App.OnSceneLoaded += HandleSceneLoaded;
        ApplyScene(App.CurrentScene);
    }

    private void OnDestroy() => App.OnSceneLoaded -= HandleSceneLoaded;

    /// <summary>
    /// 곡을 건다 — 이미 같은 곡이면 아무것도 하지 않는다(처음부터 다시 시작하지 않는다).
    /// 다른 곡이면 <see cref="m_fadeSeconds"/>에 걸쳐 겹쳐 넘긴다.
    /// </summary>
    /// <param name="id">카탈로그 키. <see cref="EBgm.None"/>이면 <see cref="Stop"/>과 같다</param>
    public void Play(EBgm id)
    {
        if (id == Current)
            return;

        if (id == EBgm.None)
        {
            Stop();
            return;
        }

        if (!m_entries.TryGetValue(id, out AudioLibrary.BgmEntry entry))
        {
            WarnOnce(id, $"카탈로그에 BGM {id} 항목이 없다");
            return;
        }

        // 클립 미배정은 사고가 아니라 '아직 안 채움'이다 — 조용히 무음으로 두되 현재 곡은 끈다.
        // 여기서 그냥 돌아가면 이전 곡이 새 씬까지 따라와 더 헷갈린다.
        if (entry.Clip == null)
        {
            Stop();
            Current = id; // 같은 씬을 다시 요청해도 재시도하지 않게 기록은 남긴다
            return;
        }

        if (m_sources == null)
            return;

        int next = m_active == 0 ? 1 : 0;

        m_sources[next].clip = entry.Clip;
        m_sources[next].volume = 0f;
        m_sources[next].Play();

        m_volume[next] = entry.Volume;
        m_gain[next] = 0f;
        m_gainTarget[next] = 1f;

        if (m_active >= 0)
            m_gainTarget[m_active] = 0f; // 이전 곡은 물러난다 — 다 빠지면 Update가 멈춘다

        m_active = next;
        Current = id;
    }

    /// <summary>곡을 끈다 — 페이드 아웃 후 정지한다.</summary>
    public void Stop()
    {
        if (m_active >= 0)
            m_gainTarget[m_active] = 0f;

        m_active = -1;
        Current = EBgm.None;
    }

    // 페이드 진행. 곡이 하나도 안 울릴 때는 아무 일도 하지 않는다.
    //
    // unscaledDeltaTime을 쓰는 이유 — 일시정지·정산 연출에서 timeScale이 0이 되면 스케일 시간으로는
    // 페이드가 그 자리에 멈춰 곡이 어정쩡하게 반쯤 겹친 채 남는다.
    private void Update()
    {
        if (m_sources == null)
            return;

        float step = m_fadeSeconds > 0f
            ? Time.unscaledDeltaTime / m_fadeSeconds
            : 1f; // 페이드 0 = 즉시

        for (int i = 0; i < m_sources.Length; i++)
        {
            if (Mathf.Approximately(m_gain[i], m_gainTarget[i]))
                continue;

            m_gain[i] = Mathf.MoveTowards(m_gain[i], m_gainTarget[i], step);
            ApplyVolume(i);

            // 다 빠진 소스는 멈춰 둔다 — 볼륨 0으로 계속 도는 소스를 남기지 않는다.
            if (m_gain[i] <= 0f && m_sources[i].isPlaying)
                m_sources[i].Stop();
        }
    }

    /// <summary>설정의 배경음 음량을 지금 울리는 곡에 다시 건다 — 슬라이더를 끄는 동안 불린다.
    /// 페이드가 끝나면 Update가 볼륨을 다시 쓰지 않으므로 여기서 밀어 넣어야 즉시 반영된다.</summary>
    public void ApplyVolume()
    {
        if (m_sources == null)
            return;

        for (int i = 0; i < m_sources.Length; i++)
            ApplyVolume(i);
    }

    private void ApplyVolume(int index) =>
        m_sources[index].volume = m_gain[index] * m_volume[index] * GameSettings.BgmVolume;

    private void HandleSceneLoaded(EScene scene) => ApplyScene(scene);

    // 씬 표에 없는 씬은 무음이다. 표가 통째로 비어 있어도(음원 배정 전) 조용히 무음이 될 뿐이라
    // 경고하지 않는다 — 클립 미배정과 같은 취급이다.
    private void ApplyScene(EScene scene)
    {
        Play(m_sceneBgm.TryGetValue(scene, out EBgm bgm) ? bgm : EBgm.None);
    }

    private void BuildIndex(AudioLibrary library)
    {
        if (library == null)
            return; // 카탈로그 누락 경고는 SoundManager가 한 번만 낸다

        foreach (AudioLibrary.BgmEntry entry in library.BgmEntries)
        {
            if (entry == null || entry.Id == EBgm.None)
                continue;

            if (!m_entries.TryAdd(entry.Id, entry))
                Debug.LogError($"[BgmPlayer] 카탈로그에 BGM {entry.Id}가 중복 등록됐다 — 먼저 오는 항목만 쓰인다", library);
        }

        foreach (AudioLibrary.SceneBgmEntry entry in library.SceneBgmEntries)
        {
            if (entry == null || entry.Scene == EScene.None)
                continue;

            if (!m_sceneBgm.TryAdd(entry.Scene, entry.Bgm))
                Debug.LogError($"[BgmPlayer] 씬 표에 {entry.Scene}이 중복 등록됐다 — 먼저 오는 항목만 쓰인다", library);
        }
    }

    // 효과음 풀에서 빌리지 않는다 — 루프로 계속 물고 있어야 하는데 풀은 오래된 것을 뺏는
    // 정책이라, 효과음이 몰리는 순간 BGM이 잘려 나간다.
    private void BuildSources()
    {
        m_sources = new AudioSource[2];
        m_gain = new float[2];
        m_gainTarget = new float[2];
        m_volume = new float[2];

        for (int i = 0; i < m_sources.Length; i++)
        {
            var host = new GameObject($"BgmSource_{i}");
            host.transform.SetParent(transform, false);

            AudioSource source = host.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = true;
            source.spatialBlend = 0f; // 2D — 화면 밖 어디에서 나는 소리가 아니다
            source.volume = 0f;
            m_sources[i] = source;
        }
    }

    private void WarnOnce(EBgm id, string reason)
    {
        if (m_warned.Add(id))
            Debug.LogWarning($"[BgmPlayer] {reason} — 해당 BGM을 건너뛴다", this);
    }
}
