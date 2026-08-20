#if UNITY_EDITOR
using Cysharp.Threading.Tasks;
using Unity.Multiplayer.PlayMode;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 개발용 자동 호스트 — 게임 맵 씬(Assets/Scenes/Maps/*)을 직접 Play할 때 쓴다.
/// 튜토리얼 씬에는 두지 않는다 — 그쪽은 빌드에서도 호스트가 필요해 TutorialDirector가 직접 띄운다 (#663).
/// 메인 에디터에서 Play하면 로컬 호스트를 띄우고 라운드를
/// 자동 시작해 바로 플레이한다(솔로). MPPM 가상 플레이어(클론)는 자동 호스트하지 않고
/// '클라이언트 참가' 버튼만 띄운다 — 호스트가 뜬 뒤 눌러 접속하면 멀티 테스트가 된다.
/// 솔로/멀티는 미리 고르는 모드가 아니라 클라이언트 참가 여부로 갈린다.
/// 에디터 전용(#if UNITY_EDITOR) — 빌드에는 아예 컴파일되지 않는다.
/// </summary>
public class DevAutoHost : MonoBehaviour
{
    private void Start()
    {
        // MPPM 가상 플레이어는 자동 호스트하지 않는다 — 전 인스턴스가 StartHost하면 같은 포트에 바인딩해 충돌.
        // 클론은 아래 OnGUI '참가' 버튼으로, 호스트가 뜬 뒤 수동 접속한다(접속 순서를 사람이 통제 → 타이밍 문제 없음).
        if (!CurrentPlayer.IsMainEditor)
            return;

        AutoHostAsync().Forget();
    }

    private async UniTaskVoid AutoHostAsync()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null)
        {
            Debug.LogWarning("[DevAutoHost] NetworkManager가 없어 자동 호스트를 건너뛴다", this);
            return;
        }

        if (nm.IsListening)
            return; // 이미 세션 중이면 관여하지 않는다

        // 모든 Start()가 끝난 다음 프레임에 호스트를 띄운다 — PlayerSpawnManager가 Start에서 Approval
        // 콜백을 등록하는데, 그 전에 StartHost하면 호스트 플레이어가 스폰 포인트를 못 받고 원점에 생성된다(#247).
        await UniTask.NextFrame(this.GetCancellationTokenOnDestroy());

        // #628 이후로는 ConnectionApprovalGate.Install()이 걸려야 SpawnPolicy가 실제로 불린다 —
        // 정식 흐름(SessionManager.CreateSessionAsync)을 안 타는 이 경로는 직접 걸어야 한다.
        if (App.Net.Session != null)
        {
            ConnectionApprovalGate.StampLocalPayload(nm);
            App.Net.Session.Approval.Install(nm);
        }

        nm.StartHost(); // 로컬 호스트 — Relay/세션 코드 불필요

        await UniTask.NextFrame(this.GetCancellationTokenOnDestroy()); // NGO 서버 준비 보장

        // 준비 완료를 대신 보고한다 (#215) — 씬 직접 Play는 App.LoadScene 파이프라인을 타지 않아
        // InGameManager.WaitUntilReadyAsync(안에 ReportSelfReady가 있다)가 아예 불리지 않는다. 그러면
        // SceneReadyGate가 호스트 보고를 못 받아 30초 타임아웃까지 라운드가 시작되지 않는다.
        // 클론은 보고하지 않아도 된다 — 게이트가 기다리는 대상은 스폰 시점의 접속자, 즉 호스트뿐이다.
        App.Game.ReadyGate?.ReportSelfReady();

        // 서버 권위로 라운드 준비 시작 — LobbyManager '게임 시작' 버튼을 대신한다.
        // StartRound가 아니라 준비 진입점을 부른다 — NPC 스폰이 준비 단계로 옮겨졌다(#403).
        // 여기선 호스트 혼자라 RoundManager의 전원 입장 대기는 자동으로 건너뛴다(기다릴 상대가 없음).
        App.Game.Round?.BeginRoundPreparation();
        Debug.Log("[DevAutoHost] 로컬 호스트 + 라운드 자동 준비 시작 (게임 씬 직접 Play)");
    }

    // MPPM 클론 전용 — 호스트(메인 에디터)가 뜬 뒤 눌러 로컬 접속한다. 접속 주소는 UnityTransport 기본값(127.0.0.1).
    private void OnGUI()
    {
        if (CurrentPlayer.IsMainEditor)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.IsListening)
            return;

        GUILayout.BeginArea(new Rect(10, 10, 260, 60));
        if (GUILayout.Button("클라이언트로 참가 (127.0.0.1)"))
        {
            // 안 찍으면 페이로드가 비어 호스트의 버전 게이트가 "?"로 보고 거부한다 (#628).
            ConnectionApprovalGate.StampLocalPayload(nm);
            nm.StartClient();
        }
        GUILayout.EndArea();
    }
}
#endif
