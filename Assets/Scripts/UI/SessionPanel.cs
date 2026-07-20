using System;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 세션 관문 패널 — Title 씬에서 세션 생성(호스트) / 코드 참가(클라이언트)만 담당한다. (#247)
/// 대기 로비가 아니다: 생성 성공 → TitleManager.StartGame()으로 호스트가 InGame을 열고,
/// 참가 성공 → 서버가 이미 InGame이므로 NGO 씬 동기화가 곧바로 끌고 간다.
/// 대기 공간·게임 시작은 InGame(본부)의 LobbyManager 담당 (#154).
/// </summary>
public class SessionPanel : PanelBase
{
    public override bool CanCloseWithESC => false; // 로비의 기본 화면 — 닫을 수 없다
    public override bool IsStackable => false;
    protected override bool OpenOnAwake => true;

    [Header("UI 참조")]
    [SerializeField]
    private Button m_createButton;

    [SerializeField]
    private Button m_joinButton;

    [SerializeField]
    private TMP_InputField m_codeInput;

    [SerializeField]
    private TMP_Text m_statusText;

    private bool m_isBusy; // 생성/참가 요청 겹침 방지 래치 (SessionManager m_isBusy와 같은 방침)

    private void OnEnable()
    {
        m_createButton.onClick.AddListener(HandleCreateClicked);
        m_joinButton.onClick.AddListener(HandleJoinClicked);
    }

    private void OnDisable()
    {
        m_createButton.onClick.RemoveListener(HandleCreateClicked);
        m_joinButton.onClick.RemoveListener(HandleJoinClicked);
    }

    private void HandleCreateClicked() => CreateAsync().Forget();

    private void HandleJoinClicked() => JoinAsync().Forget();

    private async UniTaskVoid CreateAsync()
    {
        if (m_isBusy)
            return;
        m_isBusy = true;
        SetStatus("세션 생성 중...");
        try
        {
            string code = await App.Net.Session.CreateSessionAsync();
            SetStatus($"세션 생성됨 — 코드: {code}");
            App.SceneFlow.Title.StartGame(); // 호스트: 인게임(본부 대기) 진입
        }
        catch (Exception e)
        {
            SetStatus($"생성 실패: {e.Message}");
            m_isBusy = false; // 실패 시에만 해제 — 성공하면 씬이 넘어간다
        }
    }

    private async UniTaskVoid JoinAsync()
    {
        if (m_isBusy)
            return;
        m_isBusy = true;
        SetStatus("세션 참가 중...");
        try
        {
            await App.Net.Session.JoinByCodeAsync(m_codeInput.text.Trim());
            SetStatus("접속 중...");
            // 씬 전환은 하지 않는다 — 서버 권위. NGO 씬 동기화가 InGame으로 끌고 간다.
        }
        catch (Exception e)
        {
            SetStatus($"참가 실패: {e.Message}");
            m_isBusy = false;
        }
    }

    private void SetStatus(string message)
    {
        if (m_statusText != null)
            m_statusText.text = message;
    }
}
