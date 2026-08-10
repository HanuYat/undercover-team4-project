using System;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 라운드가 끝나면 정산을 잠깐 보여주고 다음 씬으로 넘기는 라운드 마감 처리. (#214 세션 유지 씬 흐름)
/// 세션·NGO·Vivox를 유지한 채 씬만 전환한다 — 루프를 세션 유지로 반복하기 위함.
///
/// 결과에 따라 도착지가 갈린다 (#395):
///  · <b>성공</b> — 상점(허브)으로. 번 돈으로 다음 라운드를 준비하는 기존 루프.
///  · <b>실패</b> — 판이 끝났으므로 로비로 되돌리고 팀 자금을 초기값으로 리셋한다. 거기서 새 판을 시작한다.
/// 정산 UI 내용은 #107/#183; 여기서는 표시 시간(placeholder)만 둔다.
///
/// 서버 권위 흐름 (RoundManager와 동일 방침, #56):
///  · 서버·오프라인 — <see cref="RoundManager.OnRoundEnded"/> 발행(성공·실패 무관) 시 로비 복귀를 시작한다.
///    서버가 로비를 로드하면 클라는 NGO 씬 동기화로 함께 이동한다 — 클라는 여기서 아무 것도 하지 않는다.
///  · EScene 매핑이 없는 테스트 씬은 App 흐름 밖 — 세션 없이 자기 씬을 재로드한다(기존 폴백).
///
/// 비자발 드롭(호스트 이탈·세션 삭제)은 이 컴포넌트가 다루지 않는다 — 상주 ConnectionLostReturner가 전 씬 공통으로 처리한다. (#429)
/// </summary>
public class RoundEndResetter : MonoBehaviour
{
    private RoundManager Round => App.Game.Round;
    private TeamFund TeamFund => App.Game.TeamFund;
    private ShopPurchases ShopPurchases => App.Game.ShopPurchases;
    private RoundProgress RoundProgress => App.Game.RoundProgress;

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

    private void OnDisable()
    {
        if (Round != null)
            Round.OnRoundEnded -= HandleRoundEnded;
    }

    // 서버·오프라인: 라운드 종료(성공/실패 공통) → 정산 표시 후 로비 복귀. (세션 유지)
    private void HandleRoundEnded(RoundResult result, RoundEndReason reason)
    {
        if (m_ending)
            return;
        m_ending = true;
        EndRoundAsync(result).Forget();
    }

    private async UniTaskVoid EndRoundAsync(RoundResult result)
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

        // 실패 = 판이 끝났다 (#395). 로비로 되돌려 새 판을 시작하게 하고, 그동안 모은 팀 자금도 되돌린다.
        // 자금 리셋을 여기서 하는 이유: 정산 화면이 이미 이번 라운드 결과를 다 보여준 뒤라(위 대기) 표시가
        // 흔들리지 않고, TeamFund는 씬을 넘어 유지되는 상주 홀더라 씬 전환만으로는 초기화되지 않는다.
        if (result != RoundResult.Success)
        {
            if (TeamFund != null)
                TeamFund.ResetToStarting();
            else
                Debug.LogWarning("[RoundEndResetter] TeamFund를 찾지 못해 자금을 초기화하지 못했다", this);

            // 상점 구매 목록도 같은 이유로 여기서 비운다 — 자금만 되돌리면 지난 판에 산 장비를
            // 공짜로 들고 새 판을 시작한다. (#182, TeamFund와 같은 상주 홀더라 씬 전환으로는 안 지워진다)
            ShopPurchases?.Clear();

            // 라운드 진행도도 같은 이유로 되돌린다 (#377) — 실패한 판의 난이도를 새 판이 물려받지 않는다.
            // 상주 홀더라 씬 전환만으로는 초기화되지 않는 것도 팀 자금과 같다.
            RoundProgress?.ResetToFirst();

            Debug.Log("[RoundEndResetter] 라운드 실패 — 세션 유지한 채 로비 복귀 (새 판 시작)");
            App.LoadScene(EScene.Lobby);
            return;
        }

        // 성공했으니 다음 라운드로 진행도를 올린다 (#377) — 할당량이 이 값을 타고 오른다.
        // 라운드 시작이 아니라 여기서 올리는 이유: 다음 게임 씬이 로드되기 전에 값이 확정돼 복제까지 끝나야
        // 클라이언트가 첫 프레임부터 맞는 할당량을 본다 (RoundProgress 주석 참고).
        RoundProgress?.Advance();

        // 성공: 세션 유지하며 상점 씬으로 복귀. 서버만 로드하면 클라는 NGO 씬 동기화로 따라옴.
        Debug.Log("[RoundEndResetter] 라운드 성공 — 세션 유지한 채 상점(허브) 복귀");
        App.LoadScene(EScene.Shop);
    }
}
