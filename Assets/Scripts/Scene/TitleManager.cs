using UnityEngine;

/// <summary>
/// Title 씬 진입점 — 세션 관문. 세션 생성/참가 UI(SessionPanel)만 담당한다.
/// 대기·준비는 별도 씬 — Lobby(최초 대기)·Shop(라운드 사이 허브)가 맡는다. (#214)
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class TitleManager : SceneManagerBase
{
    /// <summary>
    /// 호스트 전용 — 세션 생성 직후 SessionPanel이 호출. 서버가 Lobby를 로드하면
    /// 이후 접속하는 클라이언트는 NGO 씬 동기화로 자동으로 따라온다.
    /// </summary>
    public void StartGame() => MoveToNextScene(EScene.Lobby);
}
