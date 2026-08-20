using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// 비자발 드롭 복귀 (#429) — 호스트 이탈·세션 삭제로 세션이 죽으면 어느 씬에 있든 타이틀로 되돌린다.
/// AppBootstrap 하위 상주 오브젝트라 씬을 넘어 살아 있어, 씬마다 같은 처리를 두지 않는다.
/// (자발적 이탈은 SessionFlow.LeaveToMainAsync 전담 — SessionManager의 m_isLeaving 가드가
///  그 경로에서 OnConnectionLost를 막아 두 처리가 겹치지 않는다)
/// 매니저가 아니다 — 참조자가 없으므로 App에 올리지 않는다 (R3).
/// </summary>
public class ConnectionLostReturner : MonoBehaviour
{
    private SessionManager Session => App.Net.Session;

    // 한 번의 드롭에 복귀를 두 번 걸지 않기 위한 래치. 상주 컴포넌트라 복귀가 끝나면 반드시 되돌린다
    // — 같은 실행에서 세션을 다시 만들고 또 끊길 수 있다 (씬마다 새로 생기던 RoundEndResetter와 다른 점).
    private bool m_returning;

    // R6: 매니저 구독은 Start에서 — 모든 Awake(App 등록)가 끝난 뒤다.
    private void Start()
    {
        if (Session != null)
            Session.OnConnectionLost += HandleConnectionLost;
        else
            Debug.LogWarning(
                "ConnectionLostReturner: SessionManager가 없어 드롭 복귀를 걸 수 없다",
                this
            );
    }

    private void OnDestroy()
    {
        if (Session != null)
            Session.OnConnectionLost -= HandleConnectionLost;
    }

    private void HandleConnectionLost(EConnectionLostReason reason)
    {
        if (m_returning)
            return;
        m_returning = true;
        ReturnToTitleAsync().Forget();
    }

    // NGO Shutdown이 끝난 뒤에 로드해야 한다 — 아직 IsListening이면 App.LoadScene이 NGO 씬 동기화 분기를
    // 타고, 클라는 씬 로드 권한이 없어 아무 일도 일어나지 않는다 (#326에서 재현된 고착).
    private async UniTaskVoid ReturnToTitleAsync()
    {
        try
        {
            await SessionFlow.WaitForNetworkShutdownAsync();
            if (App.CurrentScene != EScene.Title)
                App.LoadScene(EScene.Title);
        }
        finally
        {
            m_returning = false;
        }
    }
}
