using System;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 온라인 계층(세션 · NGO · 음성 · 인증)을 정해진 순서로 내려주는 임시 teardown 오케스트레이터. (#169)
/// 설계: docs/design/network-lifecycle.md — 원칙 2(단일 진입점) + 종료 순서.
/// 정식 로비/메뉴(#154)가 생기면 그 컨트롤러가 LeaveToMainAsync()를 호출하도록 바꾸면 된다(이 클래스 제거 가능).
/// </summary>
public class SessionTeardown : MonoBehaviour
{
    // 매니저는 App 파사드로만 접근한다 (R1). SessionManager·VivoxManager·AuthBootstrap은 DontDestroyOnLoad
    // 상주 매니저(AppBootstrap 프리팹)라, 씬 오브젝트인 이 클래스에서 [SerializeField]로 잡으면 런타임에
    // null이 된다 — 그러면 아래 teardown이 통째로 스킵돼 NGO/Vivox가 안 내려간다(#287의 실제 원인).
    private static SessionManager Session => App.Net.Session;
    private static VivoxManager Vivox => App.Net.Vivox;
    private static AuthBootstrap Auth => App.Net.Auth;

    private bool m_busy;

    /// <summary>
    /// "메인으로 나가기(로그아웃)" — Vivox 로그아웃 → 세션 이탈(NGO 자동 종료) → Auth 로그아웃.
    /// 이 순서를 지키면 #164류(계층 간 미전파)가 구조적으로 발생하지 않는다.
    /// </summary>
    ///
    public async UniTask LeaveToMainAsync()
    {
        if (m_busy)
            return;
        m_busy = true;

        try
        {
            // 1. Vivox 완전 로그아웃
            if (Vivox != null)
                await Vivox.LogoutAsync();

            // 2. 세션 이탈 -> NGO 내림(자동). 채널은 1번에서 이미 정리됨 -> OnSessionLeft발 채널 이탈은 no-op
            if (Session != null)
                await Session.LeaveAsync();

            // 2-1. NGO가 완전히 내려갈 때까지 대기 — Shutdown은 즉시가 아니라 다음 프레임(들)에 걸쳐 끝난다.
            //      완료를 기다리지 않으면 (a) 아래 SignOut이 AuthBootstrap의 IsNetworkConnected 가드에 막히고,
            //      (b) App.LoadScene이 NGO가 살아있는 걸로 보고 로컬 로드 대신 NGO 씬 동기화로 잘못 분기한다.
            await WaitForNetworkShutdownAsync();

            // 3. 세션이 비었으면 로그아웃 성공
            if (Auth != null)
                Auth.SignOut();

            // 4. 타이틀(로비)로 복귀 — 씬 전환 단일 경로. NGO가 완전히 내려간 뒤라 오프라인 로컬 로드로 처리된다.
            //    세션 없이 단독 Play한 경우에도 여기서 확실히 타이틀로 돌아간다.
            if (App.CurrentScene != EScene.Title)
                App.LoadScene(EScene.Title);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SessionTeardown] 종료 중 오류: {ex}");
        }
        finally
        {
            m_busy = false;
        }
    }

    // 세션 이탈(Session.LeaveAsync)이 촉발한 NGO Shutdown이 끝날 때까지(IsListening == false) 대기한다.
    // NGO는 반드시 SDK(ISession.LeaveAsync)로만 내려야 한다 — 여기서 NetworkManager.Shutdown()을 직접
    // 부르면 SDK의 NetworkManagerSession이 자기 종료 핸드셰이크를 건너뛰어(ShutdownInProgress 가드) 내부
    // 상태가 깨지고, 스케줄된 완료 콜백 OnStopCompleted에서 NRE가 난다(SDK 경고: "Use ISession.LeaveAsync
    // instead"). 그래서 강제하지 않고 SDK가 내릴 때까지 폴링만 한다. 무한 대기 방지 타임아웃.
    private static async UniTask WaitForNetworkShutdownAsync()
    {
        float deadline = Time.realtimeSinceStartup + k_shutdownTimeoutSeconds;
        while (
            NetworkManager.Singleton != null
            && NetworkManager.Singleton.IsListening
            && Time.realtimeSinceStartup < deadline
        )
        {
            await UniTask.Yield();
        }

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            Debug.LogWarning(
                "[SessionTeardown] NGO가 제한시간 내에 완전히 내려가지 않음 — 그대로 진행"
            );
    }

    private const float k_shutdownTimeoutSeconds = 5f;

    [SerializeField]
    private float m_guiTopOffset = 60f; // 좌측 상단 — 세션 코드 HUD 아래

    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, m_guiTopOffset, 380, 60));
        GUI.enabled = !m_busy;
        if (GUILayout.Button("메인으로 나가기 (로그아웃)"))
            LeaveToMainAsync().Forget();
        GUI.enabled = true;
        GUILayout.EndArea();
    }
}
