using System;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Core.Environments;
using UnityEngine;

/// <summary>
/// 계정 조작을 막고 있는 사유. 값 이름이 곧 문구의 키다 — <c>Title.AccountLock.</c> + 이름 (#497).
/// 문구는 "{조작} 수 없습니다" 꼴이라 <see cref="EAccountAction"/>을 인자로 끼워 완성한다.
/// </summary>
[LocalizedEnum("TitleTable", "Title.AccountLock.", nameof(EAccountLock.None))]
public enum EAccountLock
{
    None = 0,
    InSession = 1,  // 세션 참가 중 — PlayerId가 바뀌면 로비·Vivox가 옛 ID를 들고 어긋난다
    Switching = 2,  // 세션 전환 중
}

/// <summary>
/// 잠금 문구에 끼워 넣을 조작 이름 (<c>Title.AccountAction.</c> + 이름).
/// 로그에만 쓰이는 조작(로그아웃·토큰 삭제)은 여기 없다 — 콘솔은 번역 대상이 아니다.
/// </summary>
[LocalizedEnum("TitleTable", "Title.AccountAction.")]
public enum EAccountAction
{
    Link = 0,
    Switch = 1,
}

/// <summary>
/// UGS 초기화·로그인·계정 상태의 단일 창구. 사용처는 App.Net.Auth로 접근한다.
/// 상태 없는 형식 규칙은 NicknameRules(#249)·AccountCredentials(#384)가, 테스트 씬용 수동 조작
/// 패널은 AuthDebugGui가 담당한다 — 여기에는 상태를 가진 흐름만 둔다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class AuthBootstrap : CommonManagerBase
{
    [SerializeField]
    private bool m_signInOnStart = true;

    [SerializeField]
    private string m_environmentName = "production";

    [SerializeField]
    private string m_profile = string.Empty;

    public Func<bool> CanSignOut; // 델리게이트 (bool형 반환)

    public event Action OnSignedIn;
    public event Action OnSignedOut;
    public event Action OnNicknameChanged;

    /// <summary>
    /// <see cref="IsSigningIn"/>이 바뀌었다. 로그인이 <b>실패로</b> 끝나면
    /// <see cref="OnSignedIn"/>이 오지 않으므로 "끝났다"를 알릴 신호가 따로 필요하다. (#585)
    /// </summary>
    public event Action OnSigningInChanged;

    private const string k_nicknamePrefKeyPrefix = "player.nickname.";

    // 로그인 관문 통과 여부를 앱 실행 사이에 기억한다 — 한 번 통과하면 다음 실행부터 세션 화면으로 바로 간다. (#585)
    // 프로필별로 나눈다: 닉네임 캐시와 같은 방식이라 프로필을 바꾸면 관문도 다시 뜬다.
    private const string k_gatePassedPrefKeyPrefix = "auth.gatepassed.";

    // 계정 조작 사유 문구가 든 테이블 — 타이틀 화면의 계정 패널에서만 보인다 (#497)
    private const string k_table = "TitleTable";

    // ── 계정 연동 (#384) — 형식 규칙·오류 문장은 AccountCredentials가 담당 ──
    private string m_accountUsername = string.Empty;

    /// <summary>
    /// 연동 상태를 서버에서 확인했는가. (#384)
    /// 조회에 실패한 상태를 "익명"으로 오인하면 §4 우선순위가 뒤집혀
    /// 로컬 캐시가 계정 닉네임을 덮어쓴다 — 그래서 "모름"을 별도로 구분한다.
    /// </summary>
    private bool m_accountStateKnown;

    #region 상태 조회
    public bool IsSignedIn =>
        UnityServices.State == ServicesInitializationState.Initialized
        && AuthenticationService.Instance.IsSignedIn;

    public string PlayerId => IsSignedIn ? AuthenticationService.Instance.PlayerId : string.Empty;
    public string PlayerName =>
        IsSignedIn ? AuthenticationService.Instance.PlayerName : string.Empty;

    /// <summary>표시용 닉네임 — UGS가 자동으로 붙이는 #1234 판별자를 제거한 이름. (#249)</summary>
    public string Nickname => NicknameRules.StripDiscriminator(PlayerName);

    /// <summary>인스펙터에 설정된 프로필 — AuthDebugGui가 Start와 같은 경로로 로그인하려고 읽는다.</summary>
    public string Profile => m_profile;

    /// <summary>연동된 아이디 — 미연동이면 빈 문자열. (#384)</summary>
    public string AccountUsername => m_accountUsername;

    /// <summary>
    /// 이 실행에서 타이틀의 로그인 관문(<c>AuthGatePanel</c>)을 이미 넘었는가. (#585)
    /// 세션에서 타이틀로 돌아올 때마다 로그인 창을 다시 보여주지 않기 위한 것이다.
    ///
    /// <b>플래그가 여기 있는 이유</b> — Title 씬은 돌아올 때마다 새로 만들어지므로 씬 쪽에
    /// 두면 매번 초기화된다. 이 객체는 상주(DontDestroyOnLoad)라 앱 실행 동안 유지된다.
    ///
    /// <b>로그아웃하면 내린다.</b> 처음에는 "관문은 한 번 고르는 자리"라고 보고 유지했는데,
    /// 그러면 로그아웃한 뒤 세션 화면에 그대로 남아 [세션 생성]·[코드로 참가]가 눌리기만 하고
    /// 실패하는 막다른 길이 됐다(실측). 계정이 없는 상태에서 세션 화면은 할 수 있는 일이 없다.
    /// </summary>
    public bool HasPassedAuthGate { get; private set; }

    /// <summary>
    /// 지난 실행에서 관문을 통과했는가 — PlayerPrefs에 남는다. (#585)
    /// <see cref="HasPassedAuthGate"/>는 이번 실행 안에서만 유효하고 앱을 다시 켜면 초기화되므로,
    /// "한 번 로그인했으면 다음부터 로그인 화면을 건너뛴다"는 실행 간 기억은 이 값이 담당한다.
    /// 로그아웃하면 <see cref="SignOut"/>·<see cref="ClearSessionToken"/>이 함께 지운다.
    /// </summary>
    public bool RememberedAuthGate => PlayerPrefs.GetInt(GatePassedPrefKey, 0) == 1;

    /// <summary>정식 계정으로 승격됐는가. 판별은 PlayerInfo.Username 유무. (#384)</summary>
    public bool IsLinked => m_accountStateKnown && !string.IsNullOrEmpty(m_accountUsername);

    public bool SessionTokenExists =>
        UnityServices.State == ServicesInitializationState.Initialized
        && AuthenticationService.Instance.SessionTokenExists;

    /// <summary>
    /// 익명 로그인이 진행 중인가. (#585)
    ///
    /// <b>"로그인이 안 됐다"와 "로그인이 되는 중이다"는 다르다.</b> 관문은 되는 중일 때만
    /// 버튼을 잠가야 한다 — 안 된 상태(로그아웃 직후)까지 잠그면 [게스트로 시작]·[로그인]이
    /// 스스로 로그인할 수 있는데도 눌리지 않아 빠져나갈 길이 없어진다.
    /// </summary>
    public bool IsSigningIn { get; private set; }

    public bool IsNetworkConnected
    {
        get
        {
            var nm = NetworkManager.Singleton;
            return nm != null && (nm.IsClient || nm.IsServer);
        }
    }

    private string NicknamePrefKey =>
        k_nicknamePrefKeyPrefix + (string.IsNullOrWhiteSpace(m_profile) ? "default" : m_profile);

    private string GatePassedPrefKey =>
        k_gatePassedPrefKeyPrefix + (string.IsNullOrWhiteSpace(m_profile) ? "default" : m_profile);
    #endregion

    /// <summary>
    /// 로그인 관문을 넘었다고 표시 — <c>AuthGatePanel</c>만 호출한다. (#585)
    /// 이번 실행의 플래그와 함께 PlayerPrefs에도 남겨, 다음 실행부터 관문을 건너뛰게 한다.
    /// </summary>
    public void MarkAuthGatePassed()
    {
        HasPassedAuthGate = true;
        PlayerPrefs.SetInt(GatePassedPrefKey, 1);
        PlayerPrefs.Save();
    }

    /// <summary>실행 간 관문 기억을 지운다 — 로그아웃·토큰 삭제와 한 쌍이다. (#585)</summary>
    private void ForgetAuthGate()
    {
        PlayerPrefs.DeleteKey(GatePassedPrefKey);
        PlayerPrefs.Save();
    }

    #region 초기화 · 익명 로그인
    private void Start()
    {
        if (m_signInOnStart)
        {
            SignInOnStartAsync().Forget();
        }
    }

    private async UniTaskVoid SignInOnStartAsync()
    {
        try
        {
            await InitializeAndSignInAsync(m_profile);
        }
        catch (Exception ex)
        {
            // UniTaskVoid라 잡지 않으면 조용히 사라진다. 개별 인증 예외는 아래에서 이미 로그를
            // 남기지만, UnityServices 초기화 실패는 여기서만 드러난다.
            Debug.LogError($"[AuthBootstrap] 시작 시 로그인 실패: {ex.Message}");
        }
    }

    public async UniTask InitializeAndSignInAsync(string profile = null)
    {
        bool wasSignedIn = IsSignedIn;
        SetSigningIn(true);
        try
        {
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
                // 계정 색 복원 (#432 후속) — 캐시 적용은 첫 await 앞이라 동기로 끝나고,
                // 클라우드 왕복만 뒤로 흐른다. await하면 로그인 진행 표시가 그만큼 늘어진다.
                CosmeticsSaveService.RestoreAsync().Forget();
            }
        }
        finally
        {
            SetSigningIn(false);
        }

        // 진행 표시를 끈 뒤에 알린다 — 구독자가 "로그인됐는데 아직 로그인 중"인 어중간한
        // 상태를 보지 않게 한다.
        if (!wasSignedIn && IsSignedIn)
            OnSignedIn?.Invoke();
    }

    private void SetSigningIn(bool value)
    {
        if (IsSigningIn == value)
            return;

        IsSigningIn = value;
        OnSigningInChanged?.Invoke();
    }
    #endregion

    #region 닉네임 (#249)
    /// <summary>닉네임 변경 — 서버 반영에 성공했을 때만 로컬 캐시를 갱신한다.</summary>
    public async UniTask SetPlayerNameAsync(string name)
    {
        if (!IsSignedIn)
            return;

        string trimmed = name?.Trim() ?? string.Empty;
        if (trimmed == Nickname)
            return; // 변경 없음 — 조용히 넘어간다

        ENicknameValidation nicknameResult = NicknameRules.Validate(trimmed);
        if (nicknameResult != ENicknameValidation.Ok)
            throw new LocalizedMessageException(NicknameRules.Describe(nicknameResult));

        await AuthenticationService.Instance.UpdatePlayerNameAsync(trimmed);

        PlayerPrefs.SetString(NicknamePrefKey, trimmed);
        PlayerPrefs.Save();

        OnNicknameChanged?.Invoke();
    }

    /// <summary>
    /// 로그인 직후 로컬 캐시와 서버 닉네임을 맞춘다.
    /// 캐시가 없으면 서버 값을 씨딩하고, 다르면 캐시를 정본으로 삼아 서버에 밀어넣는다 —
    /// 세션 토큰이 지워져 PlayerId가 새로 발급된 경우의 복원 경로.
    /// </summary>
    private async UniTask RestoreCachedNicknameAsync()
    {
        // 연동된 계정은 서버가 정본 — 캐시를 덮어쓰기만 하고 서버로 밀지 않는다 (§4).
        // 이 분기가 없으면 새 기기의 낡은 익명 캐시가 계정 닉네임을 덮어쓴다.
        if (IsLinked)
        {
            CacheNickname();
            return;
        }

        // 연동 여부를 모르는 상태(조회 실패)에서는 push하지 않는다 — 계정 닉네임을 덮어쓸 위험.
        if (!m_accountStateKnown)
            return;

        string cached = PlayerPrefs.GetString(NicknamePrefKey, string.Empty);

        if (string.IsNullOrEmpty(cached))
        {
            CacheNickname();
            return;
        }

        if (cached == Nickname)
            return; // 이미 일치 — 대부분의 재접속 경로, 네트워크 호출 없음

        // 규칙 도입 이전 빌드나 수동 조작으로 남은 캐시가 그대로 서버에 반영되지 않게 한 번 더 검사한다.
        ENicknameValidation cachedResult = NicknameRules.Validate(cached);
        if (cachedResult != ENicknameValidation.Ok)
        {
            Debug.LogWarning($"[AuthBootstrap] 캐시된 닉네임이 규칙 위반이라 폐기: {cachedResult}");
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

    /// <summary>서버 닉네임을 로컬 캐시에 씨딩 — 서버가 정본인 경로에서만 쓴다 (§4).</summary>
    private void CacheNickname()
    {
        if (string.IsNullOrEmpty(Nickname))
            return;

        PlayerPrefs.SetString(NicknamePrefKey, Nickname);
        PlayerPrefs.Save();
    }
    #endregion

    #region 계정 연동 (#384)
    /// <summary>
    /// 익명 계정을 정식 계정으로 승격 — 익명 로그인 상태를 **유지한 채** 자격증명을 붙인다.
    /// 신규 SignUp으로 처리하면 새 PlayerId가 발급돼 닉네임이 유실된다.
    /// </summary>
    public async UniTask LinkAccountAsync(string username, string password)
    {
        if (!IsSignedIn)
            throw new LocalizedMessageException(Message("Title.Account.RequiresSignInToLink"));
        ThrowIfAccountLocked(EAccountAction.Link);
        if (IsLinked)
            throw new LocalizedMessageException(Message("Title.Account.AlreadyLinked"));

        string id = username?.Trim() ?? string.Empty;
        string pw = password ?? string.Empty; // 비밀번호는 Trim하지 않는다 — 공백도 유효 문자일 수 있다

        ThrowIfInvalid(AccountCredentials.Validate(id, pw));

        await AuthenticationService.Instance.AddUsernamePasswordAsync(id, pw);

        m_accountUsername = id;
        m_accountStateKnown = true;
        Debug.Log($"[AuthBootstrap] 계정 연동 완료 / playerId: {PlayerId}");

        // 승격 순간만 캐시 → 서버 1회 (§4). 로그인 때의 push가 조용히 실패했을 수 있어
        // 계정에 이름을 남기는 마지막 기회다.
        string cached = PlayerPrefs.GetString(NicknamePrefKey, string.Empty);
        if (
            !string.IsNullOrEmpty(cached)
            && cached != Nickname
            && NicknameRules.Validate(cached) == ENicknameValidation.Ok
        )
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
    /// 아이디로 로그인 — 새 기기(또는 토큰이 지워진 기기)의 경로.
    /// m_signInOnStart로 이미 익명 로그인된 상태라 SignOut이 **선행**이어야 한다 —
    /// 안 그러면 ClientInvalidUserState가 난다.
    /// </summary>
    public async UniTask SignInWithAccountAsync(string username, string password)
    {
        ThrowIfAccountLocked(EAccountAction.Switch);

        string id = username?.Trim() ?? string.Empty;
        string pw = password ?? string.Empty;

        ThrowIfInvalid(AccountCredentials.Validate(id, pw));

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

        CacheNickname(); // 연동 계정은 서버가 정본 — 캐시를 덮어쓴다 (§4)

        Debug.Log($"[AuthBootstrap] 계정 로그인 완료 / playerId: {PlayerId}");
        OnSignedIn?.Invoke();
        OnNicknameChanged?.Invoke();
    }

    /// <summary>
    /// 서버에서 연동 상태를 확인한다 — 로그인당 1회.
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
    #endregion

    #region 로그아웃 · 계정 전환
    /// <summary>
    /// 계정을 건드려도 되는 상태인가 — 불가하면 사유 문장, 가능하면 null.
    /// 세션에 참가한 채 PlayerId가 바뀌면 로비·Vivox가 옛 ID를 들고 어긋나고,
    /// 세션 전환 중(CanSignOut)에도 같은 창이 열린다. 두 검사가 계정 조작 전부에 붙으므로 여기 모은다.
    /// </summary>
    private EAccountLock GetAccountLock()
    {
        if (IsNetworkConnected)
            return EAccountLock.InSession;

        if (CanSignOut != null && !CanSignOut())
            return EAccountLock.Switching;

        return EAccountLock.None;
    }

    /// <summary>
    /// 사유가 그대로 AuthGatePanel에 표시된다 — 예외가 곧 UI 문구다. (#384)
    /// 문장이 아니라 키로 던진다: 표시하는 쪽이 자기 언어로 읽는다 (#497).
    /// 잠금 사유와 조작 이름을 따로 둔 것은 "세션 참가 중에는 {계정을 연동할} 수 없습니다"처럼
    /// 두 조각의 조합이기 때문이다 — 조합해 두면 조작이 늘 때마다 문구가 배로 는다.
    /// </summary>
    private void ThrowIfAccountLocked(EAccountAction action)
    {
        EAccountLock lockReason = GetAccountLock();
        if (lockReason == EAccountLock.None)
            return;

        throw new LocalizedMessageException(
            Message(
                "Title.AccountLock." + lockReason,
                Message("Title.AccountAction." + action)
            )
        );
    }

    /// <summary>형식 위반이면 사유를 담아 던진다 — 연동·로그인이 같은 검사를 쓴다.</summary>
    private static void ThrowIfInvalid(EAccountValidation result)
    {
        if (result != EAccountValidation.Ok)
            throw new LocalizedMessageException(AccountCredentials.Describe(result));
    }

    private static LocalizedMessage Message(string key, params object[] args) =>
        LocalizedMessage.Of(k_table, key, args);

    /// <summary>
    /// 이 기기에서 계정을 분리하고 새 익명 계정으로 시작한다. (#444)
    /// **"연동 해제"가 아니다** — UGS가 username/password 제거를 지원하지 않아 서버의 계정과
    /// 아이디는 그대로 남고, 이 기기만 새 PlayerId로 떨어져 나온다 (account-link.md 결정 (e)).
    /// ClearSessionToken()은 로그아웃·토큰 삭제까지만 하고 재로그인을 하지 않는다. AuthBootstrap은
    /// 상주 오브젝트라 m_signInOnStart도 다시 돌지 않으므로, 둘을 한 쌍으로 묶어야
    /// 로그아웃 상태로 방치되지 않는다.
    /// </summary>
    public async UniTask StartNewAnonymousAccountAsync()
    {
        ThrowIfAccountLocked(EAccountAction.Switch);
        if (UnityServices.State != ServicesInitializationState.Initialized)
            throw new LocalizedMessageException(Message("Title.Account.RequiresSignInToSwitch"));

        // 재로그인보다 **먼저** 지운다. 순서가 뒤집히면 RestoreCachedNicknameAsync의 익명 경로가
        // 옛 계정 닉네임을 새 익명 계정에 심는다 (§4).
        PlayerPrefs.DeleteKey(NicknamePrefKey);
        PlayerPrefs.Save();

        // 가드를 새로 만들지 않는다 — 위 검사를 통과했고 사이에 await가 없어 상태가 바뀔 수 없으므로
        // 기존 경로를 그대로 재사용한다 (내부 가드는 중복이지만 조용한 return으로 빠지지 않는다).
        ClearSessionToken(); // SignOut + 토큰 삭제 + 연동 상태 초기화 + OnSignedOut

        await InitializeAndSignInAsync(m_profile); // 토큰이 없어 새 PlayerId가 발급된다
        Debug.Log($"[AuthBootstrap] 새 익명 계정으로 전환 / playerId: {PlayerId}");
    }

    public void SignOut(bool clearCredentials = false)
    {
        // 콘솔 전용 경로 — 사유를 표시하지 않으므로 enum을 그대로 찍는다 (§1 범위 밖)
        EAccountLock signOutLock = GetAccountLock();
        if (signOutLock != EAccountLock.None)
        {
            Debug.LogWarning($"[AuthBootstrap] SignOut 거부 — {signOutLock}");
            return;
        }

        if (!IsSignedIn)
            return;

        AuthenticationService.Instance.SignOut(clearCredentials);

        m_accountUsername = string.Empty;
        m_accountStateKnown = false;

        // 로그아웃했으면 관문을 다시 거쳐야 한다 — 이 표시를 남겨두면 타이틀로 돌아왔을 때
        // 로그인 안 된 채로 세션 화면이 떠서 만들기·참가가 눌리기만 하고 실패한다. (#585)
        HasPassedAuthGate = false;
        ForgetAuthGate();

        CosmeticsSaveService.OnSignedOut(); // 색 캐시를 로그인 전 자리로 (#432 후속)
        OnSignedOut?.Invoke();
        Debug.Log("[AuthBootstrap] SignOut 완료");
    }

    public void ClearSessionToken()
    {
        EAccountLock clearLock = GetAccountLock();
        if (clearLock != EAccountLock.None)
        {
            Debug.LogWarning($"[AuthBootstrap] ClearSessionToken 거부 — {clearLock}");
            return;
        }

        if (UnityServices.State != ServicesInitializationState.Initialized)
            return;

        if (IsSignedIn)
        {
            m_accountUsername = string.Empty;
            m_accountStateKnown = false;

            AuthenticationService.Instance.SignOut();

            // 로그아웃했으면 관문을 다시 거쳐야 한다 — 이 표시를 남겨두면 타이틀로 돌아왔을 때
            // 로그인 안 된 채로 세션 화면이 떠서 만들기·참가가 눌리기만 하고 실패한다. (#585)
            HasPassedAuthGate = false;
            ForgetAuthGate();

            OnSignedOut?.Invoke();
        }

        AuthenticationService.Instance.ClearSessionToken();
        Debug.Log("[AuthBootstrap] ClearSessionToken 완료");
    }
    #endregion
}
