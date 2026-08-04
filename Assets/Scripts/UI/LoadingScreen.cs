using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.SceneManagement;

/// <summary>
/// 씬 전환을 덮는 상주 로딩 화면 (#403). AppBootstrap 프리팹 하위(DontDestroyOnLoad)에 배치한다.
///
/// 구동 경로가 둘이다 — 세션 중 씬 전환은 서버만 App.LoadScene을 호출하고(AppHelper 참고),
/// 클라이언트는 NGO 씬 동기화로 끌려올 뿐이라 그 파이프라인에 들어오지 않기 때문이다.
///  · 서버·오프라인 — App.LoadScene이 ShowAsync/HideAsync를 직접 호출한다.
///  · 클라이언트   — NGO 씬 이벤트(OnLoad/OnLoadComplete)를 구독해 스스로 덮는다.
/// 서버는 두 경로 모두에 걸리므로 <see cref="IsBusy"/>로 뒤쪽(클라이언트용 자동 경로)을 막는다.
///
/// 페이드 인은 두지 않는다 — 대신 로드 시작 전 k_settleFrames만큼 프레임을 흘려 "덮은 화면이 최소
/// 한 번 렌더됐다"를 보장한다. 캔버스를 켜는 것만으로는 아직 그려진 게 아니라서, 이 대기가 없으면
/// 같은 프레임에 씬이 갈아끼워져 유저에겐 여전히 옛 씬이 멈춘 화면으로 보인다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class LoadingScreen : CommonManagerBase
{
    // 덮은 화면이 실제로 렌더되는 것을 보장하는 최소 프레임 수.
    private const int k_settleFrames = 60;

    private const float k_spinnerDegreesPerSecond = 180f;

    // 클라이언트 자동 경로의 무한 대기 방지 상한.
    private const float k_loadTimeoutSeconds = 30f;

    [Header("참조")]
    [SerializeField]
    private Canvas m_canvas;

    [SerializeField]
    private CanvasGroup m_canvasGroup;

    [Tooltip("비워도 됨 — 지정하면 로딩 중 회전한다")]
    [SerializeField]
    private RectTransform m_spinner;

    [Tooltip("비워도 됨 — 상태 문구")]
    [SerializeField]
    private TMP_Text m_statusText;

    // 라벨에 LocalizeStringEvent를 붙이지 않고 여기서 테이블을 참조한다 — SetStatus가 대입하는 자리라
    // 컴포넌트를 붙이면 둘이 서로 덮어쓴다. 지금은 대입하는 곳이 없지만 그때 조용히 깨진다. (#497)
    [Tooltip("기본 상태 문구 — Common.Loading.Status")]
    [SerializeField]
    private LocalizedString m_defaultStatus;

    [Header("연출")]
    [Tooltip("페이드 아웃 시간(초). 0이면 즉시 사라진다. (페이드 인은 두지 않는다 — #403)")]
    [SerializeField]
    private float m_fadeOutSeconds = 0.35f;

    // 세션마다 새로 만들어지는 NGO SceneManager — 지금 구독 중인 대상
    private NetworkSceneManager m_hookedSceneManager;

    // 클라이언트 자동 경로에서 로컬 로드 완료를 확인하는 씬 이름
    private string m_clientLoadedScene;

    // 지금 표시 중인 상태 문구 — 구독 해제 기준
    private LocalizedString m_boundStatus;

    /// <summary>이 화면이 지금 씬을 덮고 있는가 — 두 구동 경로의 중복 실행을 막는 데 쓴다.</summary>
    public bool IsBusy { get; private set; }

    protected override void Awake()
    {
        base.Awake(); // ★ 매니저 등록 유지 (R5)
        SetVisible(false);
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
        if (IsBusy && m_spinner != null)
            m_spinner.Rotate(0f, 0f, -k_spinnerDegreesPerSecond * Time.unscaledDeltaTime);

        RefreshNetworkHook();
    }

    #region 표시 제어 — App.LoadScene 파이프라인이 호출
    /// <summary>화면을 덮고, 그것이 실제로 렌더될 때까지 대기한다. 씬 로드를 시작하기 전에 await 할 것.</summary>
    public async UniTask ShowAsync(CancellationToken token = default)
    {
        ShowInstant();
        await UniTask.DelayFrame(k_settleFrames, PlayerLoopTiming.Update, token);
    }

    /// <summary>대기 없이 즉시 덮는다 — 이미 로드가 시작돼 기다릴 여유가 없는 클라이언트 경로용.</summary>
    public void ShowInstant()
    {
        IsBusy = true;
        SetVisible(true);
    }

    /// <summary>페이드 아웃 후 화면을 내린다.</summary>
    public async UniTask HideAsync(CancellationToken token = default)
    {
        if (!IsBusy)
            return;

        await FadeOutAsync(token);
        SetVisible(false);
        IsBusy = false;
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

    // 페이드도 실시간 기준 — RoundEndResetter의 정산 대기와 같은 이유(timeScale 조작에 영향받지 않게)
    private async UniTask FadeOutAsync(CancellationToken token)
    {
        if (m_canvasGroup == null || m_fadeOutSeconds <= 0f)
            return;

        float elapsed = 0f;
        while (elapsed < m_fadeOutSeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            m_canvasGroup.alpha = Mathf.Lerp(1f, 0f, elapsed / m_fadeOutSeconds);
            await UniTask.Yield(PlayerLoopTiming.Update, token);
        }
    }

    private void SetVisible(bool visible)
    {
        if (m_canvas != null)
            m_canvas.enabled = visible;

        if (m_canvasGroup == null)
            return;

        m_canvasGroup.alpha = visible ? 1f : 0f;
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

        if (clientId != net.LocalClientId || IsBusy)
            return;

        CoverUntilLoadedAsync(sceneName).Forget();
    }

    private void HandleNetworkLoadComplete(ulong clientId, string sceneName, LoadSceneMode mode)
    {
        NetworkManager net = NetworkManager.Singleton;
        if (net != null && clientId == net.LocalClientId)
            m_clientLoadedScene = sceneName;
    }

    private async UniTaskVoid CoverUntilLoadedAsync(string sceneName)
    {
        CancellationToken token = this.GetCancellationTokenOnDestroy();
        m_clientLoadedScene = null;
        ShowInstant();

        // 데드라인 폴링 — SessionFlow.WaitForNetworkShutdownAsync와 같은 방침(강제하지 않고 경고 후 진행).
        // 세션이 도중에 끊기면 완료 신호가 영영 안 오므로 IsListening도 종료 조건에 넣는다.
        float deadline = Time.realtimeSinceStartup + k_loadTimeoutSeconds;
        await UniTask.WaitUntil(
            () =>
                m_clientLoadedScene == sceneName
                || NetworkManager.Singleton == null
                || !NetworkManager.Singleton.IsListening
                || Time.realtimeSinceStartup >= deadline,
            cancellationToken: token
        );

        if (m_clientLoadedScene == sceneName)
        {
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
    #endregion
}
