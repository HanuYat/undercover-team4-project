using System;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Core.Environments;
using UnityEngine;

[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class AuthBootstrap : CommonManagerBase
{
    [SerializeField]
    private bool m_signInOnStart = true;

    [SerializeField]
    private string m_environmentName = "production";

    [SerializeField]
    private string m_profile = string.Empty;

    private bool m_isBusy;
    private string m_status = "대기 중...";
    private bool m_eventsRegistered;

    public Func<bool> CanSignOut; // 델리게이트 (bool형 반환)

    public event Action OnSignedIn;
    public event Action OnSignedOut;

    public bool IsSignedIn =>
        UnityServices.State == ServicesInitializationState.Initialized
        && AuthenticationService.Instance.IsSignedIn;

    public string PlayerId => IsSignedIn ? AuthenticationService.Instance.PlayerId : string.Empty;
    public string PlayerName =>
        IsSignedIn ? AuthenticationService.Instance.PlayerName : string.Empty;

    private const string k_nicknamePrefKeyPrefix = "player.nickname.";

    /// <summary>
    /// 닉네임 최대 글자 수 — UGS는 길이를 제한하지 않으므로 우리가 정한다. (#249)
    /// 이름표가 FixedString64Bytes(실사용 61바이트)로 동기화되는데 한글은 UTF-8 3바이트라
    /// 20자를 넘으면 CopyFromTruncated가 조용히 잘라낸다. 머리 위 가독성까지 고려해 여유를 뒀다.
    /// </summary>
    private const int k_maxNicknameLength = 12;

    public static int MaxNicknameLength => k_maxNicknameLength;

    // ── 계정 연동 (#384) — 형식 규칙·오류 문장은 AccountCredentials가 담당 ──
    private string m_accountUsername = string.Empty;

    /// <summary>
    /// 연동 상태를 서버에서 확인했는가. (#384)
    /// 조회에 실패한 상태를 "익명"으로 오인하면 §4 우선순위가 뒤집혀
    /// 로컬 캐시가 계정 닉네임을 덮어쓴다 — 그래서 "모름"을 별도로 구분한다.
    /// </summary>
    private bool m_accountStateKnown;

    /// <summary>연동된 아이디 — 미연동이면 빈 문자열. (#384)</summary>
    public string AccountUsername => m_accountUsername;

    /// <summary>정식 계정으로 승격됐는가. 판별은 PlayerInfo.Username 유무. (#384)</summary>
    public bool IsLinked => m_accountStateKnown && !string.IsNullOrEmpty(m_accountUsername);

    public event Action OnNicknameChanged;

    /// <summary>표시용 닉네임 — UGS가 자동으로 붙이는 #1234 판별자를 제거한 이름. (#249)</summary>
    public string Nickname
    {
        get
        {
            string full = PlayerName;
            if (string.IsNullOrEmpty(full))
                return string.Empty;

            int hash = full.LastIndexOf('#');
            return hash >= 0 ? full.Substring(0, hash) : full;
        }
    }

    private string NicknamePrefKey =>
        k_nicknamePrefKeyPrefix + (string.IsNullOrWhiteSpace(m_profile) ? "default" : m_profile);

    public bool SessionTokenExists =>
        UnityServices.State == ServicesInitializationState.Initialized
        && AuthenticationService.Instance.SessionTokenExists;

    public bool IsNetworkConnected
    {
        get
        {
            var nm = NetworkManager.Singleton;
            return nm != null && (nm.IsClient || nm.IsServer);
        }
    }

    private void RegisterEvents()
    {
        if (m_eventsRegistered)
            return;

        m_eventsRegistered = true;
    }

    protected override void OnDestroy()
    {
        base.OnDestroy(); // App 등록 해제

        if (!m_eventsRegistered)
            return;

        m_eventsRegistered = false;
    }

    private void Start()
    {
        if (m_signInOnStart)
        {
            HandleSignInAsync(m_profile).Forget();
        }
    }

    private async UniTaskVoid HandleSignInAsync(string profile)
    {
        if (m_isBusy)
            return;

        m_isBusy = true;
        m_status = "초기화 + 익명 로그인 중...";

        try
        {
            await InitializeAndSignInAsync(profile);
            m_status = $"로그인 성공 - PlayerId: {PlayerId}";
        }
        catch (Exception ex)
        {
            m_status = $"로그인 실패 - {ex.Message}";
        }
        finally
        {
            m_isBusy = false;
        }
    }

    public async UniTask InitializeAndSignInAsync(string profile = null)
    {
        bool wasSignedIn = IsSignedIn;

        if (UnityServices.State != ServicesInitializationState.Initialized)
        {
            var options = new InitializationOptions();
            if (!string.IsNullOrWhiteSpace(m_environmentName))
            {
                options.SetEnvironmentName(m_environmentName);
            }
            if (!string.IsNullOrWhiteSpace(profile))
            {
                options.SetProfile(profile);
            }

            await UnityServices.InitializeAsync(options);
            Debug.Log(
                $"[AuthBootstrap] UnityServices 초기화 완료 / env: {m_environmentName}, profile: {m_profile}"
            );
        }

        RegisterEvents();

        if (!AuthenticationService.Instance.IsSignedIn)
        {
            try
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
                await AuthenticationService.Instance.GetPlayerNameAsync();
                Debug.Log($"[AuthBootstrap] 익명 로그인 완료 / playerId: {PlayerId}");
            }
            catch (AuthenticationException ex)
            {
                Debug.LogError($"[AuthBootstrap] 인증 실패: {ex.Message}");
                throw;
            }
            catch (RequestFailedException ex)
            {
                Debug.LogError($"[AuthBootstrap] 오류: {ex.Message}");
                throw;
            }
        }

        if (!wasSignedIn && IsSignedIn)
        {
            await RefreshAccountStateAsync(); // 순서 중요 — 아래 복원이 IsLinked에 의존한다 (§4)
            await RestoreCachedNicknameAsync();
            OnSignedIn?.Invoke();
        }
    }

    /// <summary>
    /// 닉네임 입력 규칙 검사 — 위반이면 사유 문자열, 통과면 null. (#249)
    /// UGS는 길이·문자셋을 제한하지 않고 공백만 거절하며 그 응답도 불친절해, 규칙은 우리가 정한다.
    /// </summary>
    private static string ValidateNickname(string trimmed)
    {
        if (string.IsNullOrEmpty(trimmed))
            return "닉네임을 입력해 주세요.";

        foreach (char c in trimmed)
        {
            if (char.IsWhiteSpace(c))
                return "닉네임에 공백을 쓸 수 없습니다.";
        }

        if (trimmed.Length > k_maxNicknameLength)
            return $"닉네임은 {k_maxNicknameLength}자 이하여야 합니다.";

        return null;
    }

    /// <summary>닉네임 변경 — 서버 반영에 성공했을 때만 로컬 캐시를 갱신한다. (#249)</summary>
    public async UniTask SetPlayerNameAsync(string name)
    {
        if (!IsSignedIn)
            return;

        string trimmed = name?.Trim() ?? string.Empty;
        if (trimmed == Nickname)
            return; // 변경 없음 — 조용히 넘어간다

        string error = ValidateNickname(trimmed);
        if (error != null)
            throw new ArgumentException(error);

        await AuthenticationService.Instance.UpdatePlayerNameAsync(trimmed);

        PlayerPrefs.SetString(NicknamePrefKey, trimmed);
        PlayerPrefs.Save();

        OnNicknameChanged?.Invoke();
    }

    /// <summary>
    /// 익명 계정을 정식 계정으로 승격 — 익명 로그인 상태를 **유지한 채** 자격증명을 붙인다. (#384)
    /// 신규 SignUp으로 처리하면 새 PlayerId가 발급돼 닉네임이 유실된다.
    /// </summary>
    public async UniTask LinkAccountAsync(string username, string password)
    {
        if (!IsSignedIn)
            throw new InvalidOperationException("로그인 후에 연동할 수 있습니다.");
        if (IsNetworkConnected)
            throw new InvalidOperationException("세션 참가 중에는 계정을 연동할 수 없습니다.");
        if (CanSignOut != null && !CanSignOut())
            throw new InvalidOperationException("세션 전환 중에는 계정을 연동할 수 없습니다.");
        if (IsLinked)
            throw new InvalidOperationException("이미 계정이 연동되어 있습니다.");

        string id = username?.Trim() ?? string.Empty;
        string pw = password ?? string.Empty; // 비밀번호는 Trim하지 않는다 — 공백도 유효 문자일 수 있다

        string error = AccountCredentials.Validate(id, pw);
        if (error != null)
            throw new ArgumentException(error);

        await AuthenticationService.Instance.AddUsernamePasswordAsync(id, pw);

        m_accountUsername = id;
        m_accountStateKnown = true;
        Debug.Log($"[AuthBootstrap] 계정 연동 완료 / playerId: {PlayerId}");

        // 승격 순간만 캐시 → 서버 1회 (§4). 로그인 때의 push가 조용히 실패했을 수 있어
        // 계정에 이름을 남기는 마지막 기회다.
        string cached = PlayerPrefs.GetString(NicknamePrefKey, string.Empty);
        if (!string.IsNullOrEmpty(cached) && cached != Nickname && ValidateNickname(cached) == null)
        {
            try
            {
                await AuthenticationService.Instance.UpdatePlayerNameAsync(cached);
                OnNicknameChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AuthBootstrap] 승격 후 닉네임 반영 실패: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 아이디로 로그인 — 새 기기(또는 토큰이 지워진 기기)의 경로. (#384)
    /// m_signInOnStart로 이미 익명 로그인된 상태라 SignOut이 **선행**이어야 한다 —
    /// 안 그러면 ClientInvalidUserState가 난다.
    /// </summary>
    public async UniTask SignInWithAccountAsync(string username, string password)
    {
        if (IsNetworkConnected)
            throw new InvalidOperationException("세션 참가 중에는 계정을 바꿀 수 없습니다.");
        if (CanSignOut != null && !CanSignOut())
            throw new InvalidOperationException("세션 전환 중에는 계정을 바꿀 수 없습니다.");

        string id = username?.Trim() ?? string.Empty;
        string pw = password ?? string.Empty;

        string error = AccountCredentials.Validate(id, pw);
        if (error != null)
            throw new ArgumentException(error);

        if (UnityServices.State != ServicesInitializationState.Initialized)
            await InitializeAndSignInAsync(m_profile); // 초기화 경로 확보 — 바로 아래에서 로그아웃한다

        bool wasSignedIn = IsSignedIn;
        if (wasSignedIn)
            AuthenticationService.Instance.SignOut(); // 토큰은 유지 — 실패 시 익명으로 되돌아갈 수 있다

        try
        {
            await AuthenticationService.Instance.SignInWithUsernamePasswordAsync(id, pw);
            await AuthenticationService.Instance.GetPlayerNameAsync(); // 이걸 빼면 Nickname이 빈 문자열
        }
        catch
        {
            // 비밀번호를 틀렸을 뿐인데 로그아웃 상태로 방치하지 않는다.
            // SignOut이 토큰을 남겨두므로 익명 로그인은 원래 PlayerId로 복귀한다.
            if (wasSignedIn)
            {
                try
                {
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();
                    await AuthenticationService.Instance.GetPlayerNameAsync();
                }
                catch (Exception restoreEx)
                {
                    Debug.LogWarning($"[AuthBootstrap] 익명 복귀 실패: {restoreEx.Message}");
                }
            }
            throw;
        }

        m_accountUsername = id;
        m_accountStateKnown = true;

        // 연동 계정은 서버가 정본 — 캐시를 덮어쓴다 (§4)
        if (!string.IsNullOrEmpty(Nickname))
        {
            PlayerPrefs.SetString(NicknamePrefKey, Nickname);
            PlayerPrefs.Save();
        }

        Debug.Log($"[AuthBootstrap] 계정 로그인 완료 / playerId: {PlayerId}");
        OnSignedIn?.Invoke();
        OnNicknameChanged?.Invoke();
    }

    public void SignOut(bool clearCredentials = false)
    {
        if (IsNetworkConnected)
        {
            m_status = "세션 참가 중에는 로그아웃 불가";
            Debug.LogWarning("[AuthBootstrap] 연결 중 SignOut 거부 - 세션 이탈 후 재시도.");
            return;
        }

        if (CanSignOut != null && !CanSignOut())
        {
            m_status = "세션 전환 중 로그아웃 불가";
            Debug.LogWarning("[AuthBootstrap] 세션 전환 중 SignOut 거부");
            return;
        }

        if (!IsSignedIn)
            return;

        AuthenticationService.Instance.SignOut(clearCredentials);

        m_accountUsername = string.Empty;
        m_accountStateKnown = false;

        OnSignedOut?.Invoke();
        Debug.Log($"[AuthBootstrap] SignOut 완료");
    }

    public void ClearSessionToken()
    {
        if (IsNetworkConnected)
        {
            m_status = "세션 참가 중 토큰 삭제 불가";
            Debug.LogWarning(
                "[AuthBootstrap] 연결 중 ClearSessionToken 거부 - 세션 이탈 후 재시도."
            );
            return;
        }

        if (CanSignOut != null && !CanSignOut())
        {
            m_status = "세션 전환 중 토큰 삭제 불가";
            Debug.LogWarning("[AuthBootstrap] 연결 중 ClearSessionToken 거부");
            return;
        }

        if (UnityServices.State != ServicesInitializationState.Initialized)
            return;

        if (IsSignedIn)
        {
            m_accountUsername = string.Empty;
            m_accountStateKnown = false;

            AuthenticationService.Instance.SignOut();
            OnSignedOut?.Invoke();
        }

        AuthenticationService.Instance.ClearSessionToken();
        Debug.Log($"[AuthBootstrap] ClearSessionToken 완료");
    }

    /// <summary>
    /// 서버에서 연동 상태를 확인한다 — 로그인당 1회. (#384)
    /// §4 우선순위가 이 값에 의존하므로 RestoreCachedNicknameAsync보다 **먼저** 불러야 한다.
    /// </summary>
    private async UniTask RefreshAccountStateAsync()
    {
        try
        {
            // PlayerInfo.Username을 그대로 믿고 이 호출을 생략하면 안 된다. Editor에서는
            // UnityServices 초기화가 도메인 리로드를 넘어 살아남아 앞선 실행의 조회 결과가
            // 남아 있을 수 있고(실측), 새 프로세스에서는 비어 있다. 비어 있는 값을 "미연동"으로
            // 읽으면 §4가 익명 경로로 가서 로컬 캐시가 계정 닉네임을 덮어쓴다.
            // 아끼는 것은 로그인당 1회 왕복, 잃는 것은 닉네임이므로 항상 서버에 확인한다.
            var info = await AuthenticationService.Instance.GetPlayerInfoAsync();
            m_accountUsername = info?.Username ?? string.Empty;
            m_accountStateKnown = true;
            Debug.Log($"[AuthBootstrap] 연동 상태: {(IsLinked ? m_accountUsername : "미연동")}");
        }
        catch (Exception ex)
        {
            // 실패해도 로그인은 유효하다 — "모름"으로 남겨 캐시 push를 막는다 (§4).
            m_accountUsername = string.Empty;
            m_accountStateKnown = false;
            Debug.LogWarning($"[AuthBootstrap] 연동 상태 조회 실패: {ex.Message}");
        }
    }

    /// <summary>
    /// 로그인 직후 로컬 캐시와 서버 닉네임을 맞춘다. (#249)
    /// 캐시가 없으면 서버 값을 씨딩하고, 다르면 캐시를 정본으로 삼아 서버에 밀어넣는다 —
    /// 세션 토큰이 지워져 PlayerId가 새로 발급된 경우의 복원 경로.
    /// </summary>
    private async UniTask RestoreCachedNicknameAsync()
    {
        // 연동된 계정은 서버가 정본 — 캐시를 덮어쓰기만 하고 서버로 밀지 않는다 (§4).
        // 이 분기가 없으면 새 기기의 낡은 익명 캐시가 계정 닉네임을 덮어쓴다.
        if (IsLinked)
        {
            if (!string.IsNullOrEmpty(Nickname))
            {
                PlayerPrefs.SetString(NicknamePrefKey, Nickname);
                PlayerPrefs.Save();
            }
            return;
        }

        // 연동 여부를 모르는 상태(조회 실패)에서는 push하지 않는다 — 계정 닉네임을 덮어쓸 위험.
        if (!m_accountStateKnown)
            return;

        string cached = PlayerPrefs.GetString(NicknamePrefKey, string.Empty);

        if (string.IsNullOrEmpty(cached))
        {
            if (!string.IsNullOrEmpty(Nickname))
            {
                PlayerPrefs.SetString(NicknamePrefKey, Nickname);
                PlayerPrefs.Save();
            }
            return;
        }

        if (cached == Nickname)
            return; // 이미 일치 — 대부분의 재접속 경로, 네트워크 호출 없음

        // 규칙 도입 이전 빌드나 수동 조작으로 남은 캐시가 그대로 서버에 반영되지 않게 한 번 더 검사한다.
        string error = ValidateNickname(cached);
        if (error != null)
        {
            Debug.LogWarning($"[AuthBootstrap] 캐시된 닉네임이 규칙 위반이라 폐기: {error}");
            PlayerPrefs.DeleteKey(NicknamePrefKey);
            PlayerPrefs.Save();
            return;
        }

        try
        {
            await AuthenticationService.Instance.UpdatePlayerNameAsync(cached);
            Debug.Log($"[AuthBootstrap] 캐시된 닉네임 복원: {cached}");
            OnNicknameChanged?.Invoke();
        }
        catch (Exception ex)
        {
            // 복원 실패는 치명적이지 않다 — 서버 이름을 그대로 쓰고 다음 로그인에 재시도한다.
            // 여기서 예외가 새어 나가면 호출부의 OnSignedIn이 실행되지 않아 로그인은 됐는데
            // UI가 갱신되지 않는 상태로 남으므로, 주석의 의도대로 모든 예외를 삼킨다.
            Debug.LogWarning($"[AuthBootstrap] 닉네임 복원 실패: {ex.Message}");
        }
    }

    [Tooltip("OnGUI 디버그 패널 표시 — 테스트 씬 수동 조작용. 정식 UI는 AuthPanel (#247)")]
    [SerializeField]
    private bool m_showDebugGui;

    [SerializeField]
    private float m_guiTopOffset = 10f;

    private string m_nicknameInput = string.Empty;

    // ── #384 스파이크 (임시 — 확인 끝나면 삭제) ──
    private string m_idInput = string.Empty;
    private string m_pwInput = string.Empty;
    private string m_spikeResult = "-";

    private void OnGUI()
    {
        if (!m_showDebugGui)
            return;
        if (IsNetworkConnected)
            return;

        GUILayout.BeginArea(new Rect(700, m_guiTopOffset, 380, 560));

        GUILayout.Label("Authentication (익명) — 상태");

        bool initialized = UnityServices.State == ServicesInitializationState.Initialized;
        GUILayout.Label($"초기화됨: {initialized}");
        GUILayout.Label($"IsSignedIn: {IsSignedIn}");
        GUILayout.Label($"PlayerId: {(string.IsNullOrEmpty(PlayerId) ? "(없음)" : PlayerId)}");
        GUILayout.Label($"Nickname: {(string.IsNullOrEmpty(Nickname) ? "(없음)" : Nickname)}");
        GUILayout.Label($"PlayerName(전체): {PlayerName}");

        GUILayout.BeginHorizontal();
        m_nicknameInput = GUILayout.TextField(m_nicknameInput, 128);
        GUI.enabled = !m_isBusy && IsSignedIn;
        if (GUILayout.Button("적용", GUILayout.Width(60)))
            ApplyNicknameAsync(m_nicknameInput).Forget();
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        GUILayout.Label(
            $"SessionTokenExists: {(initialized ? SessionTokenExists.ToString() : "(미초기화)")}"
        );

        GUILayout.Label($"연결됨(세션/NGO): {IsNetworkConnected}");
        GUILayout.Space(8);

        bool canSignOut = !m_isBusy && !IsNetworkConnected && (CanSignOut == null || CanSignOut());

        GUI.enabled = !m_isBusy;
        if (GUILayout.Button("Sign In (init + 익명 로그인)"))
        {
            HandleSignInAsync(string.IsNullOrWhiteSpace(m_profile) ? null : m_profile).Forget();
        }

        GUI.enabled = canSignOut;
        if (GUILayout.Button("Sign Out"))
        {
            SignOut();
        }

        if (GUILayout.Button("New Player (로그아웃 + 토큰 삭제 → 새 PlayerId)"))
        {
            ClearSessionToken();
        }

        GUI.enabled = true;

        if (IsNetworkConnected)
        {
            GUILayout.Label("*세션 참가 중*");
        }

        GUILayout.Space(8);
        GUILayout.Label("── 계정 연동 스파이크 (#384) ──");

        GUILayout.BeginHorizontal();
        GUILayout.Label("ID", GUILayout.Width(26));
        m_idInput = GUILayout.TextField(m_idInput, 20);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("PW", GUILayout.Width(26));
        m_pwInput = GUILayout.TextField(m_pwInput, 30);
        GUILayout.EndHorizontal();

        GUI.enabled = !m_isBusy && IsSignedIn;
        if (GUILayout.Button("① 승격(Add) — PlayerId 유지 확인"))
            SpikeLinkAsync().Forget();
        if (GUILayout.Button("② 연동 상태 조회(PlayerInfo)"))
            SpikeInspectAsync().Forget();

        GUI.enabled = !m_isBusy;
        if (GUILayout.Button("③ 로그아웃 → 아이디 로그인"))
            SpikeSignInAsync().Forget();
        GUI.enabled = true;

        GUILayout.Label(m_spikeResult);

        GUILayout.Space(8);
        GUILayout.Label(m_status);

        GUILayout.EndArea();
    }

    private async UniTaskVoid ApplyNicknameAsync(string name)
    {
        if (m_isBusy)
            return;

        m_isBusy = true;
        try
        {
            await SetPlayerNameAsync(name);
            m_status = $"닉네임 적용: {PlayerName}";
        }
        catch (Exception ex)
        {
            m_status = $"닉네임 실패 - {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            m_isBusy = false;
        }
    }

    /// <summary>① 익명 계정에 자격증명 추가 = 승격. PlayerId가 유지되는지가 핵심. (#384 스파이크)</summary>
    private async UniTaskVoid SpikeLinkAsync()
    {
        if (m_isBusy)
            return;

        m_isBusy = true;
        string idBefore = PlayerId;
        string nickBefore = Nickname;
        try
        {
            Debug.Log($"[스파이크] 전송값 id='{m_idInput}' (길이 {m_idInput.Length}) / pw 길이 {m_pwInput.Length}");

            await AuthenticationService.Instance.AddUsernamePasswordAsync(m_idInput, m_pwInput);

            bool kept = PlayerId == idBefore;
            m_spikeResult =
                $"승격 {(kept ? "OK — PlayerId 유지" : "!! PlayerId 변경됨 (설계 재검토)")}\n"
                + $"before: {idBefore}\nafter : {PlayerId}\n닉네임: {nickBefore} → {Nickname}";
            Debug.Log($"[스파이크] {m_spikeResult}");
        }
        catch (RequestFailedException ex) // AuthenticationException도 여기로 잡힌다 (파생 클래스)
        {
            m_spikeResult = $"{ex.GetType().Name} ErrorCode={ex.ErrorCode}\n{ex.Message}";
            Debug.LogError($"[스파이크] {m_spikeResult}");
        }
        finally
        {
            m_isBusy = false;
        }
    }

    /// <summary>② 연동 여부를 무엇으로 판별할 수 있는지 확인. (#384 스파이크)</summary>
    private async UniTaskVoid SpikeInspectAsync()
    {
        if (m_isBusy)
            return;

        m_isBusy = true;
        try
        {
            var info = await AuthenticationService.Instance.GetPlayerInfoAsync();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Username: '{info.Username}'");
            sb.AppendLine($"Identities: {info.Identities?.Count ?? 0}");
            if (info.Identities != null)
            {
                foreach (var identity in info.Identities)
                    sb.AppendLine($"  typeId='{identity.TypeId}' userId='{identity.UserId}'");
            }

            m_spikeResult = sb.ToString();
            Debug.Log($"[스파이크] PlayerInfo\n{m_spikeResult}");
        }
        catch (RequestFailedException ex)
        {
            m_spikeResult = $"{ex.GetType().Name} ErrorCode={ex.ErrorCode}\n{ex.Message}";
            Debug.LogError($"[스파이크] {m_spikeResult}");
        }
        finally
        {
            m_isBusy = false;
        }
    }

    /// <summary>③ 새 기기 로그인 경로 — SignOut 선행이 필수. (#384 스파이크)</summary>
    private async UniTaskVoid SpikeSignInAsync()
    {
        if (m_isBusy)
            return;

        m_isBusy = true;
        try
        {
            if (IsSignedIn)
                AuthenticationService.Instance.SignOut(); // 이걸 빼면 ClientInvalidUserState

            await AuthenticationService.Instance.SignInWithUsernamePasswordAsync(
                m_idInput,
                m_pwInput
            );
            await AuthenticationService.Instance.GetPlayerNameAsync(); // 이걸 빼면 Nickname이 빈 문자열

            m_spikeResult = $"로그인 OK\nPlayerId: {PlayerId}\n닉네임: {Nickname}";
            Debug.Log($"[스파이크] {m_spikeResult}");
        }
        catch (RequestFailedException ex)
        {
            m_spikeResult = $"{ex.GetType().Name} ErrorCode={ex.ErrorCode}\n{ex.Message}";
            Debug.LogError($"[스파이크] {m_spikeResult}");
        }
        finally
        {
            m_isBusy = false;
        }
    }
}
