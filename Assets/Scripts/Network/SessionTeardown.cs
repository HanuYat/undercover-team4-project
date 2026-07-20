using System;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// 온라인 계층(세션 · NGO · 음성 · 인증)을 정해진 순서로 내려주는 임시 teardown 오케스트레이터. (#169)
/// 설계: docs/design/network-lifecycle.md — 원칙 2(단일 진입점) + 종료 순서.
/// 정식 로비/메뉴(#154)가 생기면 그 컨트롤러가 LeaveToMainAsync()를 호출하도록 바꾸면 된다(이 클래스 제거 가능).
/// </summary>
public class SessionTeardown : MonoBehaviour
{
    [SerializeField]
    private SessionManager m_session;

    [SerializeField]
    private VivoxManager m_vivox;

    [SerializeField]
    private AuthBootstrap m_auth;

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
            if (m_vivox != null)
                await m_vivox.LogoutAsync();

            // 2. 세션 이탈 -> NGO 내림(자동). 채널은 1번에서 이미 정리됨 -> OnSessionLeft발 채널 이탈은 no-op
            if (m_session != null)
                await m_session.LeaveAsync();

            // 3. 세션이 비었으면 로그아웃 성공
            if (m_auth != null)
                m_auth.SignOut();
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
