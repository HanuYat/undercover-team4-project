using System;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 온라인 계층(세션 · NGO · 음성 · 인증) 종료를 정해진 순서로 내려주는 재사용 진입점. (#326)
/// 설계: docs/design/network-lifecycle.md — 원칙 2(단일 진입점) + 종료 순서.
/// 매니저는 App 파사드로만 접근하므로 씬 오브젝트가 아니어도 되어, 어느 씬의 UI에서든 호출할 수 있다.
/// (구 SessionTeardown MonoBehaviour의 로직을 옮겨 온 것 — 일시정지 패널이 정식 진입점이 되면서 static화)
/// </summary>
public static class SessionFlow
{
    private const float k_shutdownTimeoutSeconds = 5f;

    // 종료 요청 중복(버튼 연타·여러 패널)을 막는 래치.
    private static bool s_busy;

    public static bool IsBusy => s_busy;

    /// <summary>
    /// "메인으로 나가기" — Vivox 로그아웃 → 세션 이탈(NGO 자동 종료) → 타이틀 복귀.
    /// **인증은 유지한다** (#442) — 타이틀에서 곧바로 방을 만들거나 참가할 수 있어야 하고,
    /// 명시적 로그아웃은 SignOutView의 로그아웃 버튼이 전담한다. 세션을 먼저 비우므로
    /// 원칙 4의 CanSignOut 게이트는 그대로 유효하다 — 순서 위반은 여전히 구조적으로 불가능하다.
    /// 이 순서를 지키면 #164류(계층 간 미전파)가 구조적으로 발생하지 않는다.
    /// 클라가 호출하면 본인만 이탈하고, 호스트가 호출하면 세션이 닫혀 전원이 나간다.
    /// </summary>
    public static async UniTask LeaveToMainAsync()
    {
        if (s_busy)
            return;
        s_busy = true;

        try
        {
            // 1. Vivox 완전 로그아웃
            if (App.Net.Vivox != null)
                await App.Net.Vivox.LogoutAsync();

            // 2. 세션 이탈 -> NGO 내림(자동). 채널은 1번에서 이미 정리됨 -> OnSessionLeft발 채널 이탈은 no-op
            if (App.Net.Session != null)
                await App.Net.Session.LeaveAsync();

            // 2-1. NGO가 완전히 내려갈 때까지 대기 — Shutdown은 즉시가 아니라 다음 프레임(들)에 걸쳐 끝난다.
            //      완료를 기다리지 않으면 App.LoadScene이 NGO가 살아있는 걸로 보고
            //      로컬 로드 대신 NGO 씬 동기화로 잘못 분기한다.
            await WaitForNetworkShutdownAsync();

            // 3. 타이틀(로비)로 복귀 — 씬 전환 단일 경로. NGO가 완전히 내려간 뒤라 오프라인 로컬 로드로 처리된다.
            //    세션 없이 단독 Play한 경우에도 여기서 확실히 타이틀로 돌아간다.
            if (App.CurrentScene != EScene.Title)
                App.LoadScene(EScene.Title);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SessionFlow] 종료 중 오류: {ex}");
        }
        finally
        {
            s_busy = false;
        }
    }

    // 세션 이탈(Session.LeaveAsync)이 촉발한 NGO Shutdown이 끝날 때까지(IsListening == false) 대기한다.
    // NGO는 반드시 SDK(ISession.LeaveAsync)로만 내려야 한다 — 여기서 NetworkManager.Shutdown()을 직접
    // 부르면 SDK의 NetworkManagerSession이 자기 종료 핸드셰이크를 건너뛰어(ShutdownInProgress 가드) 내부
    // 상태가 깨지고, 스케줄된 완료 콜백 OnStopCompleted에서 NRE가 난다(SDK 경고: "Use ISession.LeaveAsync
    // instead"). 그래서 강제하지 않고 SDK가 내릴 때까지 폴링만 한다. 무한 대기 방지 타임아웃.
    public static async UniTask WaitForNetworkShutdownAsync()
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
                "[SessionFlow] NGO가 제한시간 내에 완전히 내려가지 않음 — 그대로 진행"
            );
    }
}
