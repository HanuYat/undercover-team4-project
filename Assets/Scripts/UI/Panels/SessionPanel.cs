using System;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.UI;

/// <summary>
/// 세션 관문 패널 — Title 씬에서 세션 생성(호스트) / 코드 참가(클라이언트)만 담당한다. (#247)
/// 대기 로비가 아니다: 생성 성공 → TitleManager.StartGame()으로 호스트가 InGame을 열고,
/// 참가 성공 → 서버가 이미 InGame이므로 NGO 씬 동기화가 곧바로 끌고 간다.
/// 대기 공간·게임 시작은 InGame(본부)의 LobbyManager 담당 (#154).
///
/// <b>#585에서 첫 화면이 아니게 됐다</b> — 로그인 관문(AuthGatePanel)을 통과해야 열린다.
/// 그래서 OpenOnAwake를 쓰지 않고 관문이 OpenPanel을 부른다.
///
/// 루트는 화면 전체를 덮고 실제 창은 자식 Window다. 닉네임 편집(NicknameView)은 창 안에,
/// 로그아웃(SignOutView)은 창 밖 화면 하단 [종료]·[설정] 옆에 붙는다 — 후자를 이 패널 아래
/// 두는 이유는 관문을 통과해야 보여야 하기 때문이다. 사유는 SignOutView 주석에 적었다.
/// </summary>
public class SessionPanel : PanelBase
{
    public override bool CanCloseWithESC => false; // 세션 화면의 기본 바탕 — 닫을 수 없다
    public override bool IsStackable => false;

    [Header("UI 참조")]
    [SerializeField]
    private Button m_createButton;

    [Tooltip("이어하기 — 저장된 판이 없으면 눌렀을 때 사유를 띄운다 (#373·#704)")]
    [SerializeField]
    private Button m_continueButton;

    [SerializeField]
    private Button m_joinButton;

    [SerializeField]
    private TMP_InputField m_codeInput;

    [SerializeField]
    private TMP_Text m_statusText;

    // 상태 문구는 코드가 대입하므로 씬 라벨(LocalizeStringEvent)이 아니라 여기서 테이블을 참조한다 —
    // 컴포넌트를 붙이면 SetStatus의 대입과 서로 덮어쓴다. (#497)
    [Header("상태 문구 (TitleTable)")]
    [Tooltip("익명 로그인 진행 중 — Title.Session.Status.SigningIn")]
    [SerializeField]
    private LocalizedString m_statusSigningIn;

    [Tooltip("로그인 완료 — Title.Session.Status.SignedIn ({0}=플레이어 ID)")]
    [SerializeField]
    private LocalizedString m_statusSignedIn;

    [Tooltip("이어할 판이 있음 — Title.Session.Status.SaveFound ({0}=라운드 번호)")]
    [SerializeField]
    private LocalizedString m_statusSaveFound;

    [Tooltip("이어할 판이 없음 — Title.Session.Status.NoSave")]
    [SerializeField]
    private LocalizedString m_statusNoSave;

    [Tooltip("세션 생성 중 — Title.Session.Status.Creating")]
    [SerializeField]
    private LocalizedString m_statusCreating;

    [Tooltip("세션 생성됨 — Title.Session.Status.Created ({0}=참가 코드)")]
    [SerializeField]
    private LocalizedString m_statusCreated;

    [Tooltip("생성 실패 — Title.Session.Status.CreateFailed ({0}=예외 메시지)")]
    [SerializeField]
    private LocalizedString m_statusCreateFailed;

    [Tooltip("세션 참가 중 — Title.Session.Status.Joining")]
    [SerializeField]
    private LocalizedString m_statusJoining;

    [Tooltip("접속 중 — Title.Session.Status.Connecting")]
    [SerializeField]
    private LocalizedString m_statusConnecting;

    [Tooltip("참가 실패 — Title.Session.Status.JoinFailed ({0}=예외 메시지)")]
    [SerializeField]
    private LocalizedString m_statusJoinFailed;

    [Tooltip("게임 버전 불일치 — Title.Session.Status.VersionMismatch ({0}=내 버전, {1}=방 버전)")]
    [SerializeField]
    private LocalizedString m_statusVersionMismatch;

    private bool m_isBusy; // 생성/참가 요청 겹침 방지 래치 (SessionManager m_isBusy와 같은 방침)

    // 버전 불일치 안내를 띄운 상태 — 늦게 도착하는 세이브 조회 결과가 이 문구를 덮지 않게 한다 (#586)
    private bool m_showingMismatch;

    // 진행 중이거나 끝난 세이브 조회. Preserve()로 여러 번 await할 수 있게 해서, 조회가 도는 중에
    // [이어하기]를 눌러도 결과를 기다린 뒤 판단한다 — 아직 모르는 것을 "없음"으로 답하지 않게. (#704)
    // 조회 전 기본값(default)은 곧바로 false로 완료된 상태다 — 그 구간에는 버튼이 잠겨 있어 눌리지 않는다.
    private UniTask<bool> m_saveCheck;

    // 지금 표시 중인 문구 — 구독 해제 기준. 언어를 바꿔도 떠 있는 상태 문구가 따라오게 한다 (#251 관례).
    private LocalizedString m_boundStatus;

    private void OnEnable()
    {
        m_createButton.onClick.AddListener(HandleCreateClicked);
        m_continueButton.onClick.AddListener(HandleContinueClicked);
        m_joinButton.onClick.AddListener(HandleJoinClicked);

        // 이 화면이 열릴 때 로그인이 끝나 있는지는 어느 길로 왔느냐에 갈린다 (#585):
        //  · 관문(AuthGatePanel)을 넘어 왔으면 — 세 갈래 전부 로그인 뒤에 Pass()하므로 이미 끝나 있다.
        //  · 관문을 기억으로 건너뛰었으면 — 자동 익명 로그인이 아직 진행 중일 수 있다(TitleUIManager.Start).
        // 그래서 두 경우를 다 받는다. 세션에서 타이틀로 돌아온 경우도 앞쪽에 해당한다 — 로그인은 유지되므로(#442)
        // OnSignedIn이 다시 오지 않는다.
        AuthBootstrap auth = App.Net.Auth;
        if (auth == null)
            return;

        if (auth.IsSignedIn)
        {
            HandleSignedIn();
            return;
        }

        // 로그인이 끝날 때까지 버튼을 잠근다. 끝내 실패하면 TitleUIManager가 관문으로 되돌린다.
        SetButtonsInteractable(false);
        SetStatus(m_statusSigningIn);
        auth.OnSignedIn += HandleSignedIn;
    }

    private void OnDisable()
    {
        m_createButton.onClick.RemoveListener(HandleCreateClicked);
        m_continueButton.onClick.RemoveListener(HandleContinueClicked);
        m_joinButton.onClick.RemoveListener(HandleJoinClicked);

        if (App.Net.Auth != null)
            App.Net.Auth.OnSignedIn -= HandleSignedIn;

        UnbindStatus(); // 남은 구독이 나중에 발화해 파괴된 라벨을 건드리지 않게
    }

    private void HandleSignedIn()
    {
        SetButtonsInteractable(true);
        SetStatus(m_statusSignedIn, App.Net.Auth.PlayerId);
        RefreshSaveAsync().Forget();
        ShowPendingVersionMismatch(); // 있으면 로그인 완료 문구를 덮는다 — 왜 튕겨 나왔는지가 먼저다 (#586)
    }

    /// <summary>
    /// 버전 불일치로 참가가 물러난 사유를 띄운다 (#586). 이 화면에서 곧바로 실패했을 수도 있고,
    /// 씬 동기화에 로비까지 끌려갔다가 타이틀로 되돌아온 뒤일 수도 있어 사유는 SessionManager가 들고 있다.
    /// </summary>
    private void ShowPendingVersionMismatch()
    {
        // 이 화면이 이미 씬과 함께 갈렸으면 소비하지 않는다 — 타이틀에서 새로 열릴 때 띄워야 한다
        if (m_statusText == null || App.Net.Session == null)
            return;

        SessionVersionMismatchException pending = App.Net.Session.TakePendingVersionMismatch();
        if (pending == null)
            return;

        Debug.Log(
            $"[SessionPanel] 버전 불일치 안내 / 내 버전 {pending.LocalVersion}, 방 버전 {pending.SessionVersion}"
        );
        SetStatus(m_statusVersionMismatch, pending.LocalVersion, pending.SessionVersion);
        m_showingMismatch = true;
    }

    // 세이브 유무를 미리 물어 둔다 (#373). 조회가 실패하면 없는 것으로 친다 — 새 판은 언제나 가능하다.
    private async UniTaskVoid RefreshSaveAsync()
    {
        m_saveCheck = SaveService.RefreshAsync().Preserve();
        bool hasSave = await m_saveCheck;

        // 조회가 도는 동안 타이틀을 떠났거나(파괴) 이미 세션을 만들기 시작했을 수 있다 —
        // 그때는 손대지 않는다. 늦게 도착한 결과가 "세션 생성 중..." 문구를 덮으면 안 된다.
        if (m_statusText == null || m_isBusy || m_showingMismatch)
            return;

        if (hasSave)
            SetStatus(m_statusSaveFound, SaveService.SavedRound);
    }

    private void SetButtonsInteractable(bool interactable)
    {
        m_createButton.interactable = interactable;
        m_continueButton.interactable = interactable;
        m_joinButton.interactable = interactable;
    }

    private void HandleCreateClicked() => CreateAsync(continueSave: false).Forget();

    private void HandleContinueClicked() => ContinueAsync().Forget();

    /// <summary>
    /// 이어하기 (#704). 세이브가 없으면 세션을 만들지 않고 사유를 띄운다.
    ///
    /// 예전에는 세이브가 있을 때만 버튼을 켜는 것이 유일한 안내였는데, 이 화면의 버튼은 Transition이 없어
    /// 잠긴 티가 나지 않는다 — 평소와 같은 모양으로 눌리지 않아 "고장난 버튼"으로 보였다.
    /// 그래서 버튼은 나머지 둘과 같이 열어 두고, 없다는 사실을 눌렀을 때 문구로 답한다.
    /// </summary>
    private async UniTaskVoid ContinueAsync()
    {
        if (m_isBusy)
            return;

        // 조회가 아직 돌고 있으면 기다린다 — 결과를 모르는 채로 "없다"고 답하지 않는다
        bool hasSave = await m_saveCheck;

        // 기다리는 사이 화면이 갈렸을 수 있다(로그아웃·씬 전환) — 그때는 손대지 않는다
        if (m_statusText == null || !isActiveAndEnabled)
            return;

        if (!hasSave)
        {
            SetStatus(m_statusNoSave);
            return;
        }

        CreateAsync(continueSave: true).Forget();
    }

    private void HandleJoinClicked() => JoinAsync().Forget();

    /// <param name="continueSave">저장된 판을 이어서 시작할지. 세션 생성 전에 정해져야 한다 (#373).</param>
    private async UniTaskVoid CreateAsync(bool continueSave)
    {
        if (m_isBusy)
            return;
        m_isBusy = true;

        // 반드시 세션 생성 전에 — 상주 홀더는 세션이 켜지는 순간 스폰되면서 세이브를 읽는다.
        if (continueSave)
            SaveService.UseSave();
        else
            SaveService.StartFresh();

        SetStatus(m_statusCreating);
        try
        {
            string code = await App.Net.Session.CreateSessionAsync();
            SetStatus(m_statusCreated, code);
            App.SceneFlow.Title.StartGame(); // 호스트: 인게임(본부 대기) 진입
        }
        catch (Exception e)
        {
            SetStatus(m_statusCreateFailed, e.Message);
            SaveService.StartFresh(); // 세션이 안 섰으니 대기 중인 세이브도 물린다
            m_isBusy = false; // 실패 시에만 해제 — 성공하면 씬이 넘어간다
        }
    }

    private async UniTaskVoid JoinAsync()
    {
        if (m_isBusy)
            return;
        m_isBusy = true;
        m_showingMismatch = false;
        SetStatus(m_statusJoining);
        try
        {
            await App.Net.Session.JoinByCodeAsync(m_codeInput.text.Trim());
            SetStatus(m_statusConnecting);
            // 씬 전환은 하지 않는다 — 서버 권위. NGO 씬 동기화가 InGame으로 끌고 간다.
        }
        catch (SessionVersionMismatchException)
        {
            // 아직 타이틀에 남아 있는 경우의 빠른 길 — 이미 로비로 끌려갔다면 여기서는 못 띄우고,
            // 타이틀로 되돌아가 새로 열린 화면이 같은 사유를 집어 띄운다. (#586)
            ShowPendingVersionMismatch();
            m_isBusy = false;
        }
        catch (Exception e)
        {
            SetStatus(m_statusJoinFailed, e.Message);
            m_isBusy = false;
        }
    }

    /// <summary>
    /// 상태 문구를 바꾼다 — 이전 문구의 구독을 끊고 새 문구를 구독한다.
    /// 구독하는 이유는 이 화면에서 설정 창을 열어 언어를 바꿀 수 있기 때문이다 (#374).
    /// 한 번만 읽어 대입하면 그때 떠 있던 문구가 옛 언어로 굳는다.
    /// </summary>
    /// <param name="args">Smart String 인자. 인자를 먼저 넣어야 구독 시점의 첫 발화부터 올바른 문장이 나온다.</param>
    private void SetStatus(LocalizedString message, params object[] args)
    {
        if (m_statusText == null || message == null || message.IsEmpty)
            return;

        UnbindStatus();

        // 인자가 없으면 비운다 — 남겨 두면 이전 문구의 인자가 다음 문구에 딸려 간다
        message.Arguments = (args != null && args.Length > 0) ? args : null;

        m_boundStatus = message;
        m_boundStatus.StringChanged += HandleStatusChanged; // 구독 즉시 현재 언어로 1회 발화
    }

    private void HandleStatusChanged(string localized)
    {
        if (m_statusText != null)
            m_statusText.text = localized;
    }

    private void UnbindStatus()
    {
        if (m_boundStatus == null)
            return;

        m_boundStatus.StringChanged -= HandleStatusChanged;
        m_boundStatus = null;
    }
}
