using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 씬 전환을 덮는 상주 로딩 화면 (#403). AppBootstrap 프리팹 하위(DontDestroyOnLoad)에 배치한다.
///
/// 구동 경로가 셋이다 — 세션 중 씬 전환은 서버만 App.LoadScene을 호출하고(AppHelper 참고),
/// 클라이언트는 NGO 씬 동기화로 끌려올 뿐이라 그 파이프라인에 들어오지 않기 때문이다.
///  · 서버·오프라인 — App.LoadScene이 ShowAsync/HideAsync를 직접 호출한다.
///  · 클라이언트   — NGO 씬 이벤트(OnLoad/OnLoadComplete)를 구독해 스스로 덮는다.
///  · 클라이언트   — 그보다 앞서 전환 예고(SceneTransitionAnnouncer)를 받아 미리 덮는다 (#748).
/// 서버는 IsServer 검사로 뒤쪽 두 경로에서 빠진다.
///
/// 자동 경로의 재진입 가드는 <see cref="IsBusy"/>가 아니라 m_isTrackingNetworkLoad다 — 예고로 먼저
/// 덮으면 IsBusy가 이미 true라, 그걸로 막으면 완료 대기·내리기가 안 돌아 화면이 영영 남는다. (#748)
///
/// 덮을 때는 페이드 인, 내릴 때는 페이드 아웃한다. 페이드 인은 <see cref="ShowAsync"/>가 await 하므로
/// 씬 로드는 화면이 완전히 불투명해진 뒤에야 시작된다 — 반투명한 채로 씬이 갈아끼워지면 전환이 그대로
/// 비친다. 이미 로드가 시작돼 기다릴 여유가 없는 <see cref="ShowInstant"/> 경로만 페이드 없이 덮는다.
///
/// 페이드 인 뒤에도 k_settleFrames만큼 프레임을 더 흘린다 — 마지막 알파(=1)를 쓴 프레임이 아직 렌더되지
/// 않았기 때문이다. 캔버스를 켜는 것도 알파를 쓰는 것도 아직 그려진 게 아니라서, 이 대기가 없으면
/// 같은 프레임에 씬이 갈아끼워져 유저에겐 여전히 옛 씬이 멈춘 화면으로 보인다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class LoadingScreen : CommonManagerBase
{
    // 덮은 화면이 실제로 렌더되는 것을 보장하는 최소 프레임 수. 한두 프레임이면 되는데 60(약 1초)이라
    // 서버의 NGO 로드 시작이 그만큼 늦었고, 클라가 덮는 시점도 함께 밀렸다. (#748)
    private const int k_settleFrames = 3;

    // 표시값이 목표를 따라가는 속도(초당 비율) — 로드 진행률은 계단식으로 튄다.
    private const float k_progressPerSecond = 2.5f;

    // 클라이언트 자동 경로의 무한 대기 방지 상한.
    private const float k_loadTimeoutSeconds = 30f;

    // 전환 예고만 오고 씬 로드가 시작되지 않을 때 스스로 내리는 상한 (#748).
    private const float k_announceTimeoutSeconds = 10f;

    [Header("참조")]
    [SerializeField]
    private Canvas m_canvas;

    [SerializeField]
    private CanvasGroup m_canvasGroup;

    [Tooltip("비워도 됨 — 상태 문구")]
    [SerializeField]
    private TMP_Text m_statusText;

    [Header("진행률 (#582)")]
    [Tooltip("게이지바 — Image Type을 Filled로 둘 것")]
    [SerializeField]
    private Image m_progressFill;

    [Tooltip("게이지바 우측 퍼센트 숫자")]
    [SerializeField]
    private TMP_Text m_percentText;

    [Header("달리는 캐릭터 (#582)")]
    [Tooltip("전용 카메라·캐릭터가 있는 무대 — 로딩 중에만 켜서 렌더 비용을 없앤다")]
    [SerializeField]
    private GameObject m_runnerStage;

    // 라벨에 LocalizeStringEvent를 붙이지 않고 여기서 테이블을 참조한다 — SetStatus가 대입하는 자리라
    // 컴포넌트를 붙이면 둘이 서로 덮어쓴다. 지금은 대입하는 곳이 없지만 그때 조용히 깨진다. (#497)
    [Tooltip("기본 상태 문구 — Common.Loading.Status")]
    [SerializeField]
    private LocalizedString m_defaultStatus;

    [Tooltip("씬 로드 후 런타임 스폰을 기다리는 동안의 문구 — Common.Loading.Preparing")]
    [SerializeField]
    private LocalizedString m_readyWaitStatus;

    [Header("연출")]
    [Tooltip("페이드 인 시간(초). 0이면 즉시 덮는다. 이 시간만큼 씬 로드 시작이 늦어진다.")]
    [SerializeField]
    private float m_fadeInSeconds = 0.25f;

    [Tooltip("페이드 아웃 시간(초). 0이면 즉시 사라진다.")]
    [SerializeField]
    private float m_fadeOutSeconds = 0.35f;

    // 세션마다 새로 만들어지는 NGO SceneManager — 지금 구독 중인 대상
    private NetworkSceneManager m_hookedSceneManager;

    // 클라이언트 자동 경로에서 로컬 로드 완료를 확인하는 씬 이름
    private string m_clientLoadedScene;

    // 클라이언트 자동 경로가 돌고 있는가 — "화면이 떠 있는가"(IsBusy)와 구분한다 (클래스 주석 참고, #748)
    private bool m_isTrackingNetworkLoad;

    // 지금 표시 중인 상태 문구 — 구독 해제 기준
    private LocalizedString m_boundStatus;

    // 게이지의 목표값과 실제 표시값. 둘을 나눈 이유는 위 k_progressPerSecond 주석 참고.
    private float m_targetProgress;
    private float m_shownProgress;

    // 마지막으로 라벨에 쓴 정수 퍼센트 — 같은 값이면 문자열을 다시 만들지 않는다
    private int m_shownPercent = -1;

    // 진행 중인 페이드의 세대 번호 — 새 페이드나 BeginShow가 끼어들면 이전 루프가 알파 쓰기를 멈춘다
    private int m_fadeGeneration;

    /// <summary>이 화면이 지금 씬을 덮고 있는가 — 두 구동 경로의 중복 실행을 막는 데 쓴다.</summary>
    public bool IsBusy { get; private set; }

    protected override void Awake()
    {
        base.Awake(); // ★ 매니저 등록 유지 (R5)
        SetVisible(false, 0f);
        SetStatus(null); // 기본 문구를 걸어 둔다
    }

    protected override void OnDestroy()
    {
        base.OnDestroy(); // ★ 매니저 등록 해제 유지 (R5)
        UnbindStatus();
        HookSceneManager(null);
    }

    private void Update()
    {
        // timeScale이 0으로 잠겨도(돌발 이벤트 freeze) 돌아야 하므로 실시간 기준
        if (IsBusy)
            AdvanceProgress();

        RefreshNetworkHook();
    }

    #region 표시 제어 — App.LoadScene 파이프라인이 호출
    /// <summary>
    /// 페이드 인으로 화면을 덮고, 완전히 불투명해진 화면이 실제로 렌더될 때까지 대기한다.
    /// 씬 로드를 시작하기 전에 await 할 것.
    /// </summary>
    public async UniTask ShowAsync(CancellationToken token = default)
    {
        BeginShow(0f);
        await FadeToAsync(1f, m_fadeInSeconds, token);
        await UniTask.DelayFrame(k_settleFrames, PlayerLoopTiming.Update, token);
    }

    /// <summary>대기 없이 즉시 덮는다 — 이미 로드가 시작돼 기다릴 여유가 없는 클라이언트 경로용.</summary>
    public void ShowInstant() => BeginShow(1f);

    // 덮기 공통 — 지난 전환의 잔여 상태를 되돌리고 주어진 알파로 화면을 켠다.
    private void BeginShow(float alpha)
    {
        IsBusy = true;
        m_targetProgress = 0f;
        m_shownProgress = 0f;
        m_shownPercent = -1;
        RenderProgress();
        SetStatus(null); // 지난 전환의 준비 대기 문구가 남아 있지 않게 되돌린다
        m_fadeGeneration++; // 진행 중인 페이드가 아래 알파를 덮어쓰지 않게 무효화한다
        SetVisible(true, alpha);
    }

    /// <summary>페이드 아웃 후 화면을 내린다.</summary>
    public async UniTask HideAsync(CancellationToken token = default)
    {
        if (!IsBusy)
            return;

        // 로드가 게이지보다 빨리 끝나면 중간값에서 사라진다 — 페이드 동안 100%가 보이게 맞춰 둔다.
        m_targetProgress = 1f;
        m_shownProgress = 1f;
        RenderProgress();

        await FadeToAsync(0f, m_fadeOutSeconds, token);
        SetVisible(false, 0f);
        IsBusy = false;
    }

    /// <summary>씬 로드 진행률(0~1) 보고 — App.LoadScene 파이프라인이 매 프레임 부른다. (#582)</summary>
    public void ReportSceneLoadProgress(float ratio01) => SetTargetProgress(ratio01);

    /// <summary>
    /// 씬 로드가 끝나 이제 런타임 스폰을 기다린다 — 게이지를 채우고 문구를 바꾼다. (#582)
    ///
    /// 이 구간을 게이지에 태우지 않는 이유는 <b>실측할 값이 없어서</b>다. 처음에는 씬 로드에 0.9를
    /// 주고 남은 0.1을 이 구간에 배정했지만, 대기가 끝나는 즉시 HideAsync가 이어져 0.9→1.0이
    /// 한 프레임도 못 돌았다 — 클라이언트는 k_settleFrames(약 1초)만큼 90%에 멈춰 있다가 사라졌다.
    /// 얻는 것 없이 "90%에서 멈추는 로딩바" 인상만 남아, 진척은 게이지에서 빼고 문구로 알린다.
    /// </summary>
    public void BeginSceneReadyWait()
    {
        SetTargetProgress(1f);
        SetStatus(m_readyWaitStatus);
    }

    // 되감기 금지. 구동 경로가 둘이라(서버는 App.LoadScene, 클라는 NGO 이벤트) 늦게 도착한
    // 낮은 값이 섞일 수 있고, 퍼센트가 줄어드는 화면은 그 자체로 고장으로 읽힌다.
    private void SetTargetProgress(float value) =>
        m_targetProgress = Mathf.Max(m_targetProgress, Mathf.Clamp01(value));

    private void AdvanceProgress()
    {
        if (Mathf.Approximately(m_shownProgress, m_targetProgress))
            return;

        m_shownProgress = Mathf.MoveTowards(
            m_shownProgress,
            m_targetProgress,
            k_progressPerSecond * Time.unscaledDeltaTime
        );
        RenderProgress();
    }

    private void RenderProgress()
    {
        if (m_progressFill != null)
            m_progressFill.fillAmount = m_shownProgress;

        if (m_percentText == null)
            return;

        // 표시값은 매 프레임 조금씩 움직이지만 정수 퍼센트는 그대로인 프레임이 대부분이다
        int percent = Mathf.RoundToInt(m_shownProgress * 100f);
        if (percent == m_shownPercent)
            return;

        m_shownPercent = percent;
        // 숫자와 기호뿐이라 테이블을 타지 않는다 (#497 예외 — 방침은 #525에서 함께 정한다)
        m_percentText.text = percent + "%";
    }

    /// <summary>
    /// 상태 문구 교체 (전원 대기 표시 등 후속 확장용). <c>null</c>을 넣으면 기본 문구로 돌아간다.
    ///
    /// <b>문자열이 아니라 <see cref="LocalizedString"/>을 받는다</b> — 완성된 한국어를 넘기면 그 문구만
    /// 번역에서 빠지고, 로딩 중에 언어를 바꿀 방법도 없어 눈에 띄지도 않는다. 호출부가 테이블 키를 넘기게
    /// 강제하는 편이 낫다. 이 화면은 상주(DontDestroyOnLoad)라 켜고 끄는 훅이 없으므로 구독 해제 기준은
    /// 표시 중인 참조 하나다 (SessionPanel과 같은 방침). (#497)
    /// </summary>
    public void SetStatus(LocalizedString status)
    {
        LocalizedString next = status ?? m_defaultStatus;

        if (m_statusText == null)
            return;

        if (next == null || next.IsEmpty)
        {
            Debug.LogWarning("[LoadingScreen] 상태 문구가 연결되지 않았습니다.", this);
            return;
        }

        UnbindStatus();

        m_boundStatus = next;
        m_boundStatus.StringChanged += HandleStatusChanged; // 구독 즉시 현재 언어로 1회 발화
    }

    private void HandleStatusChanged(string localized)
    {
        if (m_statusText != null)
            m_statusText.text = localized;
    }

    private void UnbindStatus()
    {
        if (m_boundStatus == null)
            return;

        m_boundStatus.StringChanged -= HandleStatusChanged;
        m_boundStatus = null;
    }

    /// <summary>
    /// 알파를 목표값까지 옮긴다. 페이드는 실시간 기준 — RoundEndResetter의 정산 대기와 같은 이유
    /// (timeScale 조작에 영향받지 않게). 도중에 다른 페이드나 <see cref="BeginShow"/>가 끼어들면
    /// 세대 번호가 어긋나 조용히 손을 뗀다 (예고로 페이드 인하던 중 즉시 덮기가 들어오는 경우 — #748).
    /// </summary>
    private async UniTask FadeToAsync(float target, float seconds, CancellationToken token)
    {
        if (m_canvasGroup == null)
            return;

        int generation = ++m_fadeGeneration;

        if (seconds <= 0f)
        {
            m_canvasGroup.alpha = target;
            return;
        }

        float from = m_canvasGroup.alpha;
        float elapsed = 0f;
        while (elapsed < seconds)
        {
            elapsed += Time.unscaledDeltaTime;
            m_canvasGroup.alpha = Mathf.Lerp(from, target, elapsed / seconds);
            await UniTask.Yield(PlayerLoopTiming.Update, token);

            if (generation != m_fadeGeneration)
                return;
        }

        m_canvasGroup.alpha = target;
    }

    private void SetVisible(bool visible, float alpha)
    {
        if (m_canvas != null)
            m_canvas.enabled = visible;

        // 무대에는 전용 카메라가 있다 — 켠 채로 두면 로딩이 아닐 때도 매 프레임 RenderTexture를 그린다.
        if (m_runnerStage != null)
            m_runnerStage.SetActive(visible);

        if (m_canvasGroup == null)
            return;

        m_canvasGroup.alpha = alpha;
        m_canvasGroup.blocksRaycasts = visible; // 아래 씬 UI로 클릭이 새지 않게
        m_canvasGroup.interactable = visible;
    }
    #endregion

    #region 클라이언트 자동 경로 — NGO 씬 동기화로 끌려오는 쪽
    // NGO SceneManager는 세션이 살아있는 동안만 존재하고 재접속마다 새로 만들어진다. 이 객체는 상주라
    // 생성 시점에 한 번 걸어둘 수 없어, 대상이 바뀐 프레임에만 다시 건다 (참조 비교라 비용은 무시 가능).
    private void RefreshNetworkHook()
    {
        NetworkManager net = NetworkManager.Singleton;
        NetworkSceneManager current = net != null && net.IsListening ? net.SceneManager : null;

        if (!ReferenceEquals(current, m_hookedSceneManager))
            HookSceneManager(current);
    }

    private void HookSceneManager(NetworkSceneManager target)
    {
        if (m_hookedSceneManager != null)
        {
            m_hookedSceneManager.OnLoad -= HandleNetworkLoad;
            m_hookedSceneManager.OnLoadComplete -= HandleNetworkLoadComplete;
        }

        m_hookedSceneManager = target;

        if (m_hookedSceneManager != null)
        {
            m_hookedSceneManager.OnLoad += HandleNetworkLoad;
            m_hookedSceneManager.OnLoadComplete += HandleNetworkLoadComplete;
        }
    }

    private void HandleNetworkLoad(
        ulong clientId,
        string sceneName,
        LoadSceneMode mode,
        AsyncOperation operation
    )
    {
        NetworkManager net = NetworkManager.Singleton;
        if (net == null || net.IsServer)
            return; // 서버는 App.LoadScene이 이미 덮고 있다

        if (clientId != net.LocalClientId || m_isTrackingNetworkLoad)
            return;

        CoverUntilLoadedAsync(sceneName, operation).Forget();
    }

    private void HandleNetworkLoadComplete(ulong clientId, string sceneName, LoadSceneMode mode)
    {
        NetworkManager net = NetworkManager.Singleton;
        if (net != null && clientId == net.LocalClientId)
            m_clientLoadedScene = sceneName;
    }

    /// <summary>서버의 전환 예고를 받아 미리 덮는다 — 완료 대기·내리기는 뒤이어 올 씬 이벤트가 맡는다. (#748)</summary>
    public void CoverForIncomingSceneChange()
    {
        if (IsBusy)
            return;

        BeginShow(0f);
        FadeToAsync(1f, m_fadeInSeconds, this.GetCancellationTokenOnDestroy()).Forget();
        WaitForAnnouncedLoadAsync().Forget();
    }

    // 예고만 오고 로드가 시작되지 않으면(로드 실패·세션 끊김) 덮은 화면이 굳는다 —
    // 자동 경로가 이어받지 않은 채 상한을 넘기면 스스로 내린다.
    private async UniTaskVoid WaitForAnnouncedLoadAsync()
    {
        CancellationToken token = this.GetCancellationTokenOnDestroy();

        float deadline = Time.realtimeSinceStartup + k_announceTimeoutSeconds;
        while (!m_isTrackingNetworkLoad && IsBusy && Time.realtimeSinceStartup < deadline)
            await UniTask.Yield(PlayerLoopTiming.Update, token);

        if (m_isTrackingNetworkLoad || !IsBusy)
            return;

        Debug.LogWarning(
            "[LoadingScreen] 전환 예고 뒤 씬 로드가 시작되지 않았습니다 — 로딩 화면을 내립니다."
        );
        await HideAsync(token);
    }

    private async UniTaskVoid CoverUntilLoadedAsync(string sceneName, AsyncOperation operation)
    {
        CancellationToken token = this.GetCancellationTokenOnDestroy();
        m_clientLoadedScene = null;
        m_isTrackingNetworkLoad = true; // 예고가 먼저 덮었더라도 완료 대기는 여기가 맡는다 (#748)

        // 예고가 이미 덮는 중이면 손대지 않는다 — 페이드 인하던 알파를 1로 튕겨 올리는 꼴이 된다.
        if (!IsBusy)
            ShowInstant();

        try
        {
            // 데드라인 폴링 — SessionFlow.WaitForNetworkShutdownAsync와 같은 방침(강제하지 않고 경고 후 진행).
            // 세션이 도중에 끊기면 완료 신호가 영영 안 오므로 IsListening도 종료 조건에 넣는다.
            // 폴링하는 김에 게이지도 여기서 채운다 — 클라이언트는 이 경로가 유일하다 (#582).
            float deadline = Time.realtimeSinceStartup + k_loadTimeoutSeconds;
            while (
                m_clientLoadedScene != sceneName
                && NetworkManager.Singleton != null
                && NetworkManager.Singleton.IsListening
                && Time.realtimeSinceStartup < deadline
            )
            {
                if (operation != null)
                    ReportSceneLoadProgress(operation.progress);

                await UniTask.Yield(PlayerLoopTiming.Update, token);
            }

            if (m_clientLoadedScene == sceneName)
            {
                BeginSceneReadyWait();

                await UniTask.DelayFrame(k_settleFrames, PlayerLoopTiming.Update, token); // 첫 렌더 가리기

                // 런타임 스폰까지 기다린다 — 서버는 App.LoadScene이 같은 대기를 걸지만 클라는 여기가 유일한 경로
                await App.WaitUntilSceneReadyAsync(token);
            }
            else
            {
                Debug.LogWarning(
                    $"[LoadingScreen] '{sceneName}' 로드 완료를 확인하지 못했습니다 — 로딩 화면을 내립니다."
                );
            }

            await HideAsync(token);
        }
        finally
        {
            m_isTrackingNetworkLoad = false;
        }
    }
    #endregion
}
