using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 씬 로드 실행 담당 — App.LoadScene의 실제 구현부. 직접 호출하지 말고 App.LoadScene을 쓸 것.
/// 세션 중이면 NGO 씬 동기화(서버만), 아니면 로컬 로드.
/// 전제: NetworkManager의 Enable Scene Management가 켜져 있어야 한다.
/// </summary>
public static class AppHelper
{
    /// <summary>EScene → 실제 씬 이름. 빌드 인덱스에 결합하지 않는다 (NGO도 이름 기반 로드).</summary>
    public static string ToSceneName(EScene scene) =>
        scene switch
        {
            EScene.Title => "Title Scene", // main이 Title.unity → "Title Scene.unity"로 개명 (#214 리베이스 반영)
            EScene.Lobby => "Lobby",
            EScene.Shop => "Shop",
            EScene.Game => "Main Scene",
            _ => null,
        };

    private static EScene FromSceneName(string sceneName) =>
        sceneName switch
        {
            "Title Scene" => EScene.Title,
            "Lobby" => EScene.Lobby,
            "Shop" => EScene.Shop,
            "Main Scene" => EScene.Game,
            _ => EScene.None,
        };

    internal static void LoadScene(EScene scene)
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

            SceneEventProgressStatus status = net.SceneManager.LoadScene(
                sceneName,
                LoadSceneMode.Single
            );
            if (status != SceneEventProgressStatus.Started)
                Debug.LogError($"[AppHelper] NGO 씬 로드 실패: {scene} ({status})");
            return;
        }

        // 오프라인 (에디터 단독 테스트 포함) — 로컬 로드
        SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
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
