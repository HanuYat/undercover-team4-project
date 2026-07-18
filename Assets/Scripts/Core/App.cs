using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// 전역 파사드 — 모든 매니저 접근의 단일 경로. (아키텍처 규칙: 매니저 접근은 App 경유만 허용)
/// 매니저 필드는 ManagerHandler가 리플렉션으로 주입/해제한다. 직접 대입 금지.
/// 새 매니저 추가 = ① 베이스 상속 ② 아래에 private 필드 추가 ③ 그룹에 프로퍼티 추가.
/// 등록 기준: 씬에 개념적으로 하나뿐인 서비스가, 두 개 이상의 도메인에서 참조될 때.
/// </summary>
public class App : Singleton<App>
{
    #region 매니저 필드 — ManagerHandler가 주입 (타입당 하나만)
#pragma warning disable CS0649 // 리플렉션으로만 할당되는 필드 — "할당되지 않음" 경고는 오탐
    // 씬 매니저 (씬 전환 시 교체됨)
    private SceneManagerBase m_sceneManager;

    // 상주 매니저 (AppBootstrap 프리팹 · DontDestroyOnLoad — 3단계에서 배치)
    private SessionManager m_sessionManager;
    private AuthBootstrap m_authBootstrap;
    private VivoxManager m_vivoxManager;

    // 인게임 매니저 (Main Scene)
    private RoundManager m_roundManager;
    private SuddenEventManager m_suddenEventManager;
    private WantedListManager m_wantedListManager;
    private ArrestJudge m_arrestJudge;
    private CriminalAssigner m_criminalAssigner;
    private NpcSpawner m_npcSpawner;
    private AppearanceAssigner m_appearanceAssigner;

    // UI 매니저 (씬 전환 시 교체됨)
    private UIManagerBase m_uiManager;
#pragma warning restore CS0649
    #endregion

    #region 씬 상태 · 이벤트
    public static UnityAction<EScene> OnSceneLoad;    // 언로드 직전 (정리 작업 훅)
    public static UnityAction<EScene> OnSceneLoaded;  // 새 씬 로드 완료

    public static EScene PrevScene { get; private set; }
    public static EScene CurrentScene { get; private set; }

    /// <summary>씬 전환 단일 경로 — 세션 중이면 NGO 씬 동기화, 아니면 로컬 로드 (AppHelper가 분기).</summary>
    public static void LoadScene(EScene scene)
    {
        OnSceneLoad?.Invoke(scene);
        AppHelper.LoadScene(scene);
    }

    /// <summary>AppHelper의 sceneLoaded 콜백에서만 호출 — 씬 상태 갱신 + 완료 이벤트.</summary>
    internal static void NotifySceneLoaded(EScene scene)
    {
        PrevScene = CurrentScene;
        CurrentScene = scene;
        OnSceneLoaded?.Invoke(scene);
    }
    #endregion

    #region 접근 그룹 — 사용처는 반드시 이 경로로
    public static class Net
    {
        public static SessionManager Session => Instance.m_sessionManager;
        public static AuthBootstrap Auth => Instance.m_authBootstrap;
        public static VivoxManager Vivox => Instance.m_vivoxManager;
    }

    public static class Game
    {
        public static RoundManager Round => Instance.m_roundManager;
        public static SuddenEventManager SuddenEvent => Instance.m_suddenEventManager;
        public static WantedListManager WantedList => Instance.m_wantedListManager;
        public static ArrestJudge ArrestJudge => Instance.m_arrestJudge;
        public static CriminalAssigner CriminalAssigner => Instance.m_criminalAssigner;
        public static NpcSpawner NpcSpawner => Instance.m_npcSpawner;
        public static AppearanceAssigner Appearance => Instance.m_appearanceAssigner;
    }

    public static class SceneFlow
    {
        public static SceneManagerBase Current => Instance.m_sceneManager;
    }

    public static class UI
    {
        public static UIManagerBase Current => Instance.m_uiManager;
        // TitleUI / InGameUI 캐스트 프로퍼티는 3단계에서 해당 클래스와 함께 추가
    }
    #endregion

    // Enter Play Mode Options에서 도메인 리로드를 꺼도 이전 플레이의 매니저 참조가 남지 않도록 리셋
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Reset();
        OnSceneLoad = null;
        OnSceneLoaded = null;
        PrevScene = EScene.None;
        CurrentScene = EScene.None;
    }
}
