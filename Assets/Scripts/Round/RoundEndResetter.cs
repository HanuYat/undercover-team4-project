using System;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 라운드가 끝나면 정산을 잠깐 보여주고 로비로 복귀시키는 라운드 마감 처리. (#214 세션 유지 씬 흐름)
/// 세션·NGO·Vivox를 유지한 채 로비로만 전환한다 — 루프(로비↔게임)를 세션 유지로 반복하기 위함.
/// 정산 UI 내용은 #107/#183; 여기서는 표시 시간(placeholder)만 둔다.
///
/// 서버 권위 흐름 (RoundManager와 동일 방침, #56):
///  · 서버·오프라인 — <see cref="RoundManager.OnRoundEnded"/> 발행(성공·실패 무관) 시 로비 복귀를 시작한다.
///    서버가 로비를 로드하면 클라는 NGO 씬 동기화로 함께 이동한다 — 클라는 여기서 아무 것도 하지 않는다.
///  · EScene 매핑이 없는 테스트 씬은 App 흐름 밖 — 세션 없이 자기 씬을 재로드한다(기존 폴백).
///
/// 비자발 드롭(호스트 이탈·세션 삭제)은 세션이 죽은 것이므로 로비가 아니라 타이틀로 복귀한다.
/// (자발적 로그아웃은 SessionTeardown 전담 — m_isLeaving 가드로 OnConnectionLost가 발화하지 않는다.)
/// </summary>
public class RoundEndResetter : MonoBehaviour
{
    private RoundManager Round => App.Game.Round;
    private SessionManager Session => App.Net.Session;

    [Header("정산 표시")]
    // 정산 화면(#107) 연출과 맞춘다: SettlementPanel의 텍스트 지연(1.5s) + 카운트다운(10s) = 11.5s.
    // 카운트다운이 0에 닿는 순간 상점(허브)으로 복귀하도록 이 값을 그 합과 같게 유지할 것.
    [Tooltip(
        "라운드 종료 후 상점(허브) 복귀까지의 대기(초) — 정산 텍스트 지연+카운트다운과 맞춘다. 0이면 즉시"
    )]
    [SerializeField]
    private float m_resetDelaySeconds = 11.5f;

    // 한 번만 수행하기 위한 래치
    private bool m_ending;

    private void OnEnable()
    {
        // 라운드 종료는 서버·오프라인에서만 발행된다 — 권위 피어가 로비 복귀 시작
        if (Round != null)
            Round.OnRoundEnded += HandleRoundEnded;
    }

    private void Start()
    {
        // 비자발 드롭(호스트가 세션을 내림) — 세션이 죽었으니 타이틀로. 자발적 로그아웃은 SessionTeardown 전담.
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

    // 서버·오프라인: 라운드 종료(성공/실패 공통) → 정산 표시 후 로비 복귀. (세션 유지)
    private void HandleRoundEnded(RoundResult result, RoundEndReason reason)
    {
        if (m_ending)
            return;
        m_ending = true;
        EndRoundToLobbyAsync().Forget();
    }

    // 비자발 드롭: 세션이 죽었으므로 타이틀로. (붙어 있을 세션이 없어 로비로 가면 안 된다)
    private void HandleConnectionLost()
    {
        if (m_ending)
            return;
        m_ending = true;
        ReturnToTitleAsync().Forget();
    }

    // NGO Shutdown이 끝난 뒤에 타이틀로 로드해야 한다 — 아직 IsListening이면 App.LoadScene이 NGO 씬 동기화
    // 분기를 타고, 클라는 씬 로드 권한이 없어 아무 일도 안 일어나 Game 씬에 고착된다(#326 재현). SessionTeardown/
    // SessionFlow의 자발적 이탈이 같은 이유로 shutdown을 기다리는 것과 동일하다.
    private async UniTaskVoid ReturnToTitleAsync()
    {
        await SessionFlow.WaitForNetworkShutdownAsync();
        if (App.CurrentScene != EScene.Title)
            App.LoadScene(EScene.Title); // NGO 내려간 뒤 → 오프라인 로컬 로드
    }

    private async UniTaskVoid EndRoundToLobbyAsync()
    {
        // 정산을 잠깐 보여줄 여유. freeze로 timeScale이 건드려져도 흐르도록 실시간 기준.
        // 대기 중 씬 언로드로 파괴되면 취소한다. (#247)
        if (m_resetDelaySeconds > 0f)
            await UniTask.Delay(
                TimeSpan.FromSeconds(m_resetDelaySeconds),
                ignoreTimeScale: true,
                cancellationToken: this.GetCancellationTokenOnDestroy()
            );

        // 테스트 씬(App 흐름 밖, 오프라인): 세션이 없으니 NGO만 내리고 자기 씬 재로드. (기존 폴백)
        if (App.CurrentScene == EScene.None)
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm != null && (nm.IsListening || nm.IsClient || nm.IsServer))
                nm.Shutdown();
            Scene active = SceneManager.GetActiveScene();
            Debug.Log($"[RoundEndResetter] 라운드 종료 - '{active.name}' 재로드(테스트 씬 폴백)");
            SceneManager.LoadScene(active.name);
            return;
        }

        // 정식 루프: 세션 유지하며 상점 씬으로 복귀. 서버만 로드하면 클라는 NGO 씬 동기화로 따라옴.
        Debug.Log("[RoundEndResetter] 라운드 종료 — 세션 유지한 채 상점(허브) 복귀");
        App.LoadScene(EScene.Shop);
    }
}
