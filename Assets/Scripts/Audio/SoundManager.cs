using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 효과음 재생 — 3D/2D 원샷과 2D 루프. <see cref="App"/>.Sound로 접근한다. (#478, #483)
///
/// AppBootstrap에 상주한다(DontDestroyOnLoad). <b>BGM은 여기 없다</b> — 같은 오브젝트의
/// <see cref="BgmPlayer"/>가 맡고, 이 매니저는 카탈로그만 넘겨준다(<c>App.Sound.Bgm</c>으로 닿는다).
/// 효과음은 불릴 때만 도는 원샷 풀이지만 BGM은 페이드 때문에 매 프레임 돌고 씬 전환을 구독하므로,
/// 한 클래스에 두면 소리 하나를 추가할 때마다 성격이 다른 두 덩어리를 함께 읽어야 한다.
///
/// <b>마스터 음량은 건드리지 않는다</b> — <see cref="GameSettings"/>가 <c>AudioListener.volume</c>으로
/// 이미 걸고 있다(#225). 대신 <b>효과음 음량</b>은 여기서 곱한다 — 믹서가 없어 재생 지점이 유일한 창구다.
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
[RequireComponent(typeof(BgmPlayer))]
public class SoundManager : CommonManagerBase
{
    [Tooltip("오디오 카탈로그 — 비우면 모든 재생 요청이 무동작한다")]
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

    // 2D 루프 전용 소스 — 풀에서 빌리지 않는다. 풀은 오래된 것을 뺏는 정책이라 루프를 맡길 수 없다.
    private AudioSource m_loopSource;
    private EAudioClip m_loopId = EAudioClip.None;

    // 환경음 전용 슬롯 — 채널링 루프와 따로 둔다 (#647). 비가 오는 중에 스캔을 하면
    // 한 슬롯으로는 서로를 끄고, 스캔이 끝날 때 비까지 그친다.
    private AudioSource m_ambientSource;
    private EAudioClip m_ambientId = EAudioClip.None;

    // 배선 사고를 알리되 매 프레임 도배하지 않는다.
    private readonly HashSet<EAudioClip> m_warned = new();

    /// <summary>BGM 재생 — 같은 오브젝트의 협력자. <c>App.Sound.Bgm</c>으로 닿는다.</summary>
    public BgmPlayer Bgm { get; private set; }

    protected override void Awake()
    {
        base.Awake(); // App.Sound 등록 (R5)

        BuildIndex();
        BuildSources();

        // 카탈로그 배선 지점을 하나로 두려고 이쪽이 넘겨준다 (BgmPlayer.Initialize 주석 참고).
        Bgm = GetComponent<BgmPlayer>();
        Bgm.Initialize(m_library);

        GameSettings.OnSfxVolumeChanged += HandleSfxVolumeChanged;
    }

    protected override void OnDestroy()
    {
        GameSettings.OnSfxVolumeChanged -= HandleSfxVolumeChanged;
        base.OnDestroy(); // App.Sound 해제 (R5)
    }

    /// <summary>이 항목을 지금 설정으로 낼 때의 음량 — 자기 <see cref="AudioSource"/>로 직접 트는 쪽도
    /// 이걸 거쳐야 효과음 슬라이더가 걸린다 (<see cref="GetSfxEntry"/>와 짝).</summary>
    public static float SfxVolumeOf(AudioLibrary.Entry entry) =>
        entry == null ? 0f : entry.Volume * GameSettings.SfxVolume;

    // 이미 울리고 있는 루프에 새 음량을 건다. 원샷은 곧 끝나므로 굳이 다시 쓰지 않는다.
    private void HandleSfxVolumeChanged(float _)
    {
        if (m_loopSource != null && m_loopId != EAudioClip.None
            && m_entries.TryGetValue(m_loopId, out AudioLibrary.Entry loop))
        {
            m_loopSource.volume = SfxVolumeOf(loop);
        }

        if (m_ambientSource != null && m_ambientId != EAudioClip.None
            && m_entries.TryGetValue(m_ambientId, out AudioLibrary.Entry ambient))
        {
            m_ambientSource.volume = SfxVolumeOf(ambient);
        }
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
        source.volume = SfxVolumeOf(entry);
        ApplyStartOffset(source, entry);
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
        source.volume = SfxVolumeOf(entry);
        ApplyStartOffset(source, entry);
        source.Play();
    }

    /// <summary>
    /// '하는 동안 계속' 나는 2D 소리를 건다 — 채널링음처럼 시작과 끝이 분명한 것.
    /// 이미 같은 소리가 돌고 있으면 처음부터 다시 시작하지 않는다.
    ///
    /// <b>슬롯은 하나뿐이다</b> — 한 번에 채널링할 수 있는 행동이 하나라서다(스캔 중에 소생을 할 수 없다).
    /// 둘 이상이 겹칠 일이 생기면 그때 슬롯을 늘릴 것.
    /// </summary>
    public void PlayLoop2D(EAudioClip id)
    {
        if (id == EAudioClip.None)
        {
            StopLoop2D();
            return;
        }

        if (m_loopId == id)
            return;

        if (!m_entries.TryGetValue(id, out AudioLibrary.Entry entry))
        {
            WarnOnce(id, $"카탈로그에 {id} 항목이 없다");
            return;
        }

        // 클립 미배정은 '아직 안 채움'이다 — 이전 루프는 끊어 두고 조용히 넘어간다.
        if (entry.Clip == null)
        {
            StopLoop2D();
            return;
        }

        if (m_loopSource == null)
            return;

        m_loopSource.clip = entry.Clip;
        m_loopSource.volume = SfxVolumeOf(entry);
        m_loopSource.Play();
        m_loopId = id;
    }

    /// <summary>2D 루프를 끊는다 — 채널링이 완료·취소·중단 중 무엇으로 끝나도 불러야 한다.</summary>
    public void StopLoop2D()
    {
        if (m_loopId == EAudioClip.None)
            return;

        if (m_loopSource != null)
        {
            m_loopSource.Stop();
            m_loopSource.clip = null;
        }

        m_loopId = EAudioClip.None;
    }

    /// <summary>
    /// 환경음 2D 루프를 건다 — 비·눈처럼 <b>세상이 내는</b> 소리용. (#647)
    /// 채널링 루프(<see cref="PlayLoop2D"/>)와 슬롯이 달라 서로를 끄지 않는다.
    /// </summary>
    public void PlayAmbient2D(EAudioClip id)
    {
        if (id == EAudioClip.None)
        {
            StopAmbient2D();
            return;
        }

        if (m_ambientId == id)
            return; // 이미 같은 소리가 돌고 있다 — 처음부터 다시 시작하지 않는다

        if (!m_entries.TryGetValue(id, out AudioLibrary.Entry entry))
        {
            WarnOnce(id, $"카탈로그에 {id} 항목이 없다");
            return;
        }

        if (entry.Clip == null)
        {
            WarnOnce(id, $"{id} 항목에 클립이 배정되지 않았다");
            StopAmbient2D();
            return;
        }

        if (m_ambientSource == null)
            return;

        m_ambientSource.clip = entry.Clip;
        m_ambientSource.volume = SfxVolumeOf(entry);
        m_ambientSource.Play();
        m_ambientId = id;
    }

    /// <summary>환경음 루프를 끊는다 — 날씨가 그치거나 뷰가 사라질 때 부른다.</summary>
    public void StopAmbient2D(EAudioClip id = EAudioClip.None)
    {
        // 남이 건 소리는 끄지 않는다 — 비가 그칠 때 눈 소리를 끊으면 안 된다
        if (m_ambientId == EAudioClip.None || (id != EAudioClip.None && m_ambientId != id))
            return;

        if (m_ambientSource != null)
        {
            m_ambientSource.Stop();
            m_ambientSource.clip = null;
        }

        m_ambientId = EAudioClip.None;
    }

    /// <summary>
    /// 카탈로그 항목을 그대로 돌려준다 — 없으면 null. <b>풀로 낼 수 없는 소리</b>가 자기
    /// <see cref="AudioSource"/>로 직접 틀 때 쓴다(발소리 루프처럼 움직이는 대상을 계속 따라다녀야
    /// 하고 길이가 긴 것). 풀은 원샷 전용이라 오래된 소리를 뺏어 가므로 루프를 맡길 수 없다.
    /// </summary>
    public AudioLibrary.Entry GetSfxEntry(EAudioClip id) =>
        m_entries.TryGetValue(id, out AudioLibrary.Entry entry) ? entry : null;

    // 클립 앞의 여린 도입부를 건너뛴다 (#549). 풀에서 빌린 소스는 직전 재생의 위치가 남아 있으므로
    // 배선값이 0이어도 매번 되돌려 준다 — 안 그러면 앞 소리의 시작 위치가 다음 소리에 새어 나간다.
    // 클립 길이를 넘는 값이 배선되면 Unity가 예외를 던지므로 안쪽으로 묶는다.
    private static void ApplyStartOffset(AudioSource source, AudioLibrary.Entry entry)
    {
        source.time = entry.StartOffset <= 0f
            ? 0f
            : Mathf.Min(entry.StartOffset, Mathf.Max(0f, entry.Clip.length - 0.05f));
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

        BuildLoopSource();
    }

    private void BuildLoopSource()
    {
        m_loopSource = BuildLoopSource("Loop2DSource");
        m_ambientSource = BuildLoopSource("Ambient2DSource");
    }

    private AudioSource BuildLoopSource(string label)
    {
        var host = new GameObject(label);
        host.transform.SetParent(transform, false);

        AudioSource source = host.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = true;
        source.spatialBlend = 0f; // 2D — 채널링음은 하는 본인에게만, 환경음은 어디 서 있든 같게 난다
        return source;
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
