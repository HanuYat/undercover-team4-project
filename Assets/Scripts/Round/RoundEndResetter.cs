using System;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 라운드가 끝나면 세션을 종료하고 현재 씬을 다시 로드해 "처음부터" 상태로 되돌리는 임시 마감 처리. (#188)
/// 정산 → 로비 복귀 → 다음 라운드 같은 정식 라운드 순환 흐름(#107/#183)이 붙기 전까지의 자리표시 구현이다.
///
/// 서버 권위 흐름 (RoundManager와 동일 방침, #56):
///  · 서버·오프라인 — <see cref="RoundManager.OnRoundEnded"/>가 발행되면(성공·실패 무관) 리셋을 시작한다.
///  · 멀티플레이 클라이언트 — 라운드 종료를 아직 동기화받지 않으므로(#43 전), 호스트가 세션을 내려
///    "비자발"로 끊기는 것(<see cref="SessionManager.OnConnectionLost"/>)을 신호로 각자 동일하게 리셋한다.
///    자발적 로그아웃은 SessionTeardown이 전담한다 — 이 리셋은 발화하지 않는다(#287 충돌 방지).
///
/// 리셋 = UGS 세션 나가기 → NGO Shutdown → 로비(Title) 복귀. (#247 씬 흐름)
/// 단, EScene 매핑이 없는 테스트 씬에서는 기존처럼 자기 씬을 재로드한다.
/// </summary>
public class RoundEndResetter : MonoBehaviour
{
    private RoundManager Round => App.Game.Round;
    private SessionManager Session => App.Net.Session;

    [Header("리셋 타이밍")]
    [Tooltip("라운드 종료 후 리셋까지의 대기(초) — 결과를 잠깐 보여줄 여유. 0이면 즉시")]
    [SerializeField] private float m_resetDelaySeconds = 2f;

    // 종료·끊김이 겹쳐 들어와도(호스트는 둘 다 발생) 리셋을 한 번만 수행하기 위한 래치
    private bool m_resetting;

    private void OnEnable()
    {
        // 라운드 종료는 서버·오프라인에서만 발행된다 — 이 훅으로 권위 피어가 리셋을 시작한다.
        if (Round != null)
            Round.OnRoundEnded += HandleRoundEnded;
    }

    private void Start()
    {
        // 클라이언트 리셋 신호는 "비자발 드롭"(호스트가 세션을 내림)만 삼는다 — SessionManager.OnConnectionLost.
        // NetworkManager.OnClientStopped(모든 NGO 정지에 반응)를 쓰면 자발적 로그아웃(SessionTeardown)이
        // 촉발한 정지에도 깨어나, 세션 이탈~타이틀 복귀를 SessionTeardown과 중복 수행하며 충돌한다(#287).
        // OnConnectionLost는 SessionManager의 m_isLeaving 가드로 자발적 이탈 시엔 발화하지 않는다.
        if (Session != null)
            Session.OnConnectionLost += HandleConnectionLost;
    }

    private void OnDisable()
    {
        if (Round != null)
            Round.OnRoundEnded -= HandleRoundEnded;
    }

    private void OnDestroy()
    {
        if (Session != null)
            Session.OnConnectionLost -= HandleConnectionLost;
    }

    // 서버·오프라인: 라운드가 끝나면(성공/실패 공통) 리셋을 시작한다. (사유는 종료 피드백 UI(#210)가 따로 표시)
    private void HandleRoundEnded(RoundResult result, RoundEndReason reason)
    {
        BeginReset();
    }

    // 클라이언트: 호스트가 세션을 내려 "비자발"로 연결이 끊기면 리셋한다.
    // (자발적 로그아웃은 SessionTeardown이 전담하므로 여기로 오지 않는다 — m_isLeaving 가드)
    private void HandleConnectionLost()
    {
        BeginReset();
    }

    private void BeginReset()
    {
        if (m_resetting)
            return;
        m_resetting = true;
        ResetToStartAsync().Forget();
    }

    private async UniTaskVoid ResetToStartAsync()
    {
        // 결과를 잠깐 보여줄 여유. freeze로 timeScale이 건드려져도 흐르도록 실시간 기준.
        // [버그 수정] 대기 중 씬 언로드로 파괴되면 이후 로직이 죽은 오브젝트에서 돌지 않게 취소한다. (#247)
        if (m_resetDelaySeconds > 0f)
            await UniTask.Delay(TimeSpan.FromSeconds(m_resetDelaySeconds), ignoreTimeScale: true,
                cancellationToken: this.GetCancellationTokenOnDestroy());

        // 1) UGS 세션 나가기 (있을 때만) — OnSessionLeft로 Vivox 채널 정리까지 연쇄된다.
        if (Session != null && Session.CurrentSession != null)
        {
            try
            {
                await Session.LeaveAsync();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RoundEndResetter] 세션 나가기 실패(무시하고 진행): {ex.Message}");
            }
        }

        // 2) NGO 종료. 세션이 있으면 위 LeaveAsync가 SDK(ISession.LeaveAsync) 경로로 이미 내렸다 —
        //    여기서 NetworkManager.Shutdown()을 직접 부르면 SDK 상태가 깨져 NRE가 난다(#287).
        //    그래서 SDK 세션이 아예 없는 로컬/오프라인 NGO만 여기서 직접 내린다.
        NetworkManager nm = NetworkManager.Singleton;
        if ((Session == null || Session.CurrentSession == null)
            && nm != null && (nm.IsListening || nm.IsClient || nm.IsServer))
            nm.Shutdown();

        // 3) 로비(Title) 복귀 — App 씬 흐름 단일 경로. (#247)
        //    EScene 매핑이 없는 테스트 씬(CurrentScene == None)은 App 흐름 밖이므로 기존처럼 자기 씬을 재로드한다.
        if (App.CurrentScene == EScene.None)
        {
            Scene active = SceneManager.GetActiveScene();
            Debug.Log($"[RoundEndResetter] 라운드 종료 — 세션 종료 후 '{active.name}' 씬 재로드(테스트 씬 폴백)");
            SceneManager.LoadScene(active.name);
            return;
        }

        Debug.Log("[RoundEndResetter] 라운드 종료 — 세션 종료 후 로비(Title) 복귀");
        App.LoadScene(EScene.Title);
    }
}
