using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 씬 로드 실행 담당 — App.LoadScene의 실제 구현부. 직접 호출하지 말고 App.LoadScene을 쓸 것.
/// 세션 중이면 NGO 씬 동기화(서버만), 아니면 로컬 로드.
/// 전제: NetworkManager의 Enable Scene Management가 켜져 있어야 한다.
///
/// 로드는 비동기다 (#403) — 동기 로드는 호출 프레임에 씬을 갈아끼워 로딩 화면을 띄울 틈 자체가 없었다.
/// 완료 판정은 경로마다 다르다: 오프라인은 활성화 시점을 직접 열고, NGO는 열어주지 않으므로 씬 이벤트로 확인한다.
/// </summary>
public static class AppHelper
{
    // LoadSceneAsync는 활성화 대기(allowSceneActivation=false) 상태에서 progress가 1.0이 아니라 0.9에서 멈춘다.
    private const float k_activationReadyProgress = 0.9f;

    // 활성화 직후 첫 프레임은 셰이더 컴파일·텍스처 업로드로 튄다 — 로딩 화면으로 덮은 채 흘려보낸다.
    private const int k_firstRenderFrames = 2;

    // NGO 씬 동기화의 로컬 완료를 기다리는 상한. 무한 대기(로딩 화면 고착) 방지.
    private const float k_networkLoadTimeoutSeconds = 30f;

    /// <summary>EScene → 실제 씬 이름. 빌드 인덱스에 결합하지 않는다 (NGO도 이름 기반 로드).</summary>
    public static string ToSceneName(EScene scene) =>
        scene switch
        {
            EScene.Title => "Title Scene", // main이 Title.unity → "Title Scene.unity"로 개명 (#214 리베이스 반영)
            EScene.Lobby => "Lobby",
            EScene.Shop => "Shop",
            // 라운드를 진행할 게임 맵. 맵은 여러 개(Assets/Scenes/Maps/*)지만 고르는 수단이 아직 없어
            // 여기서 한 장을 지정한다 — 맵을 바꿔 보려면 이 줄만 고치면 된다.
            // 로비에서 맵을 고르게 되면 이 자리를 그 선택값으로 바꾼다. (#215)
            EScene.Game => "Map_Apocalypse",
            _ => null,
        };

    // 이름에 없으면 씬 매니저에게 물어본다 — <b>InGameManager가 있는 씬이 곧 게임 맵</b>이다 (#215).
    // 게임 맵은 여러 개(Assets/Scenes/Maps/*)라서 맵마다 이 스위치에 줄을 늘리지 않기 위한 것이다.
    // 이 시점에 씬 매니저는 이미 등록돼 있다 — 호출부(sceneLoaded·InitCurrentScene)가 모두 Awake 뒤다.
    private static EScene FromSceneName(string sceneName) =>
        sceneName switch
        {
            "Title Scene" => EScene.Title,
            "Lobby" => EScene.Lobby,
            "Shop" => EScene.Shop,
            "Main Scene" => EScene.Game,
            _ => App.SceneFlow.Game != null ? EScene.Game : EScene.None,
        };

    internal static async UniTask LoadSceneAsync(EScene scene, CancellationToken token)
    {
        string sceneName = ToSceneName(scene);
        if (sceneName == null)
        {
            Debug.LogError($"[AppHelper] 로드할 수 없는 씬: {scene}");
            return;
        }

        NetworkManager net = NetworkManager.Singleton;

        // 네트워크 세션 중 — 서버가 NGO 씬 동기화로 로드해야 전 클라이언트가 따라온다
        if (net != null && net.IsListening)
        {
            if (!net.IsServer)
            {
                // 클라이언트가 임의로 씬을 바꾸면 세션에서 이탈한다 — 조용히 무시하지 않고 알린다
                Debug.LogError($"[AppHelper] 세션 중 씬 전환은 서버만 할 수 있습니다: {scene}");
                return;
            }

            await LoadViaNetworkAsync(net, sceneName, token);
            return;
        }

        // 오프라인 (에디터 단독 테스트 포함) — 로컬 로드
        await LoadLocalAsync(sceneName, token);
    }

    // 오프라인 경로만 활성화 시점을 제어할 수 있다 — 에셋 로드가 끝난 뒤 우리가 활성화를 연다.
    private static async UniTask LoadLocalAsync(string sceneName, CancellationToken token)
    {
        AsyncOperation op = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
        if (op == null)
        {
            Debug.LogError($"[AppHelper] 씬 로드를 시작하지 못했습니다: {sceneName}");
            return;
        }

        op.allowSceneActivation = false;
        await UniTask.WaitUntil(
            () => op.progress >= k_activationReadyProgress,
            cancellationToken: token
        );

        // 활성화 프레임 — 새 씬 전체의 Awake/OnEnable/Start가 여기서 한 번에 돈다(쪼갤 수 없다).
        // 이 프레임의 스파이크는 없앨 수 없고, 로딩 화면으로 가리는 것이 최선이다.
        op.allowSceneActivation = true;
        await op.ToUniTask(cancellationToken: token);

        await UniTask.DelayFrame(k_firstRenderFrames, PlayerLoopTiming.Update, token);
    }

    // NGO 씬 동기화는 활성화 시점을 열어주지 않는다 — 로컬 로드 완료를 씬 이벤트로 확인한다.
    private static async UniTask LoadViaNetworkAsync(
        NetworkManager net,
        string sceneName,
        CancellationToken token
    )
    {
        bool localLoaded = false;

        void HandleLoadComplete(ulong clientId, string loadedScene, LoadSceneMode mode)
        {
            if (clientId == net.LocalClientId && loadedScene == sceneName)
                localLoaded = true;
        }

        // LoadScene 호출 전에 걸어야 한다 — 완료가 먼저 울려 신호를 놓치는 경우를 없앤다
        net.SceneManager.OnLoadComplete += HandleLoadComplete;
        try
        {
            SceneEventProgressStatus status = net.SceneManager.LoadScene(
                sceneName,
                LoadSceneMode.Single
            );
            if (status != SceneEventProgressStatus.Started)
            {
                Debug.LogError($"[AppHelper] NGO 씬 로드 실패: {sceneName} ({status})");
                return;
            }

            // 데드라인 폴링 — SessionFlow.WaitForNetworkShutdownAsync와 같은 방침(강제하지 않고 경고 후 진행).
            // 세션이 도중에 끊기면 완료 신호가 영영 안 오므로 IsListening도 종료 조건에 넣는다.
            float deadline = Time.realtimeSinceStartup + k_networkLoadTimeoutSeconds;
            await UniTask.WaitUntil(
                () => localLoaded || !net.IsListening || Time.realtimeSinceStartup >= deadline,
                cancellationToken: token
            );

            if (!localLoaded)
                Debug.LogWarning(
                    $"[AppHelper] NGO 씬 로드 완료를 확인하지 못했습니다: {sceneName} — 그대로 진행"
                );
        }
        finally
        {
            // 세션이 내려가면 SceneManager 자체가 사라진다
            if (net.SceneManager != null)
                net.SceneManager.OnLoadComplete -= HandleLoadComplete;
        }

        await UniTask.DelayFrame(k_firstRenderFrames, PlayerLoopTiming.Update, token);
    }

    // 어떤 경로로 씬이 로드되든(App.LoadScene, NGO 동기화, 에디터 직접 Play) App의 씬 상태를 갱신한다
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void HookSceneLoaded()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    // 특정 씬에서 바로 Play하면 그 초기(활성) 씬은 sceneLoaded가 울리지 않아 CurrentScene이 None으로 남는다.
    // 최초 활성 씬을 한 번 반영한다 — 이후 전환은 위 sceneLoaded 훅이 담당.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void InitCurrentScene()
    {
        if (App.CurrentScene == EScene.None)
            App.NotifySceneLoaded(FromSceneName(SceneManager.GetActiveScene().name));
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (mode != LoadSceneMode.Single)
            return;

        App.NotifySceneLoaded(FromSceneName(scene.name));
    }
}
