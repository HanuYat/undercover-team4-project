using Unity.Services.Core;
using UnityEngine;

/// <summary>
/// 자격증명 형식 검사 결과. 값 이름이 곧 사유 문구의 키다 — <c>Title.AccountValidation.</c> + 이름 (#497).
/// </summary>
[LocalizedEnum("TitleTable", "Title.AccountValidation.", nameof(EAccountValidation.Ok))]
public enum EAccountValidation
{
    Ok = 0,
    UsernameEmpty = 1,
    UsernameLength = 2,
    UsernameCharset = 3,
    PasswordEmpty = 4,
    PasswordLength = 5,
    PasswordCharset = 6,

    /// <summary>소문자·숫자·기호 중 빠진 것이 있다. <b>대문자는 묻지 않는다</b> — 그것만 보낼 때 채운다
    /// (<see cref="AccountCredentials.ToProviderPassword"/>).</summary>
    PasswordComposition = 7,
}

/// <summary>
/// UGS 인증 오류 분류 — 실측 코드로 가른 결과. 값 이름이 곧 문구의 키다 (<c>Title.AccountError.</c> + 이름).
/// </summary>
[LocalizedEnum("TitleTable", "Title.AccountError.")]
public enum EAccountError
{
    UsernameTaken = 0,
    AlreadyLinked = 1,
    InvalidFormat = 2,
    Unclassified = 3, // 코드로 가릴 수 없는 나머지 — 아이디·비번 불일치와 서비스 장애가 여기 섞인다
}

/// <summary>
/// 계정 자격증명의 형식 규칙과 오류 분류. (#384)
/// UGS 규칙을 클라이언트에서 먼저 검사하는 이유는 서버가 형식 위반을
/// InvalidParameters(10002) 하나로 뭉쳐 보내며 **어느 쪽이 틀렸는지 알려주지 않기** 때문이다.
/// 상태가 없는 순수 규칙이라 AuthBootstrap에서 분리했다.
///
/// 사유는 <b>문장이 아니라 enum</b>으로 돌려준다 (#497 결정 (h)·(g)). 여기서 문장을 만들면
/// 그 문장은 만든 시점의 언어로 굳고, 규칙 검사기가 표시 언어를 알아야 하는 것도 이상하다.
/// 문장이 필요한 쪽은 <see cref="Describe(EAccountValidation)"/>로 <see cref="LocalizedMessage"/>를 받는다.
/// </summary>
public static class AccountCredentials
{
    private const int k_minUsernameLength = 3;
    private const int k_maxUsernameLength = 20;
    private const int k_minPasswordLength = 8;

    // UGS 상한은 30이지만 사용자에게는 그보다 짧게 연다 — 보낼 때 접미사를 붙이기 때문이다
    // (ToProviderPassword). 사용자 상한 + 접미사 길이가 30을 넘으면 서버가 거절한다.
    private const int k_maxPasswordLength = 30 - 1;

    /// <summary>
    /// UGS로 보낼 때 <b>대문자 요구만</b> 채우는 접미사. 나머지(소문자·숫자·기호)는 사용자에게 그대로 묻는다.
    ///
    /// <b>이 값은 바꾸면 안 된다.</b> 이걸 붙여 만든 계정의 비밀번호가 통째로 달라져 로그인이 막힌다.
    /// 길이를 바꾸는 것도 같다 — 위 상한이 이 길이에 물려 있다.
    /// </summary>
    private const string k_providerPasswordSuffix = "A";

    private const string k_table = "TitleTable";
    private const string k_validationPrefix = "Title.AccountValidation.";
    private const string k_errorPrefix = "Title.AccountError.";

    /// <summary>UI 입력 상한의 단일 출처 — AuthGatePanel이 characterLimit에 쓴다. (#249의 NicknameRules.MaxLength와 같은 방식)</summary>
    public static int MaxUsernameLength => k_maxUsernameLength;
    public static int MaxPasswordLength => k_maxPasswordLength;

    /// <summary>형식 검사 — 통과면 <see cref="EAccountValidation.Ok"/>.</summary>
    public static EAccountValidation Validate(string username, string password)
    {
        if (string.IsNullOrEmpty(username))
            return EAccountValidation.UsernameEmpty;

        if (username.Length < k_minUsernameLength || username.Length > k_maxUsernameLength)
            return EAccountValidation.UsernameLength;

        foreach (char c in username)
        {
            // UGS 허용 문자셋 — 영숫자 + . - @ _ (한글·공백 불가)
            bool allowed =
                (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9')
                || c == '.'
                || c == '-'
                || c == '@'
                || c == '_';
            if (!allowed)
                return EAccountValidation.UsernameCharset;
        }

        if (string.IsNullOrEmpty(password))
            return EAccountValidation.PasswordEmpty;

        if (password.Length < k_minPasswordLength || password.Length > k_maxPasswordLength)
            return EAccountValidation.PasswordLength;

        bool hasLower = false;
        bool hasDigit = false;
        bool hasSymbol = false;
        foreach (char c in password)
        {
            // 한글 입력(IME가 한글 모드로 남은 경우)과 공백을 여기서 걸러낸다 — 실제로 겪은 함정.
            if (c < '!' || c > '~')
                return EAccountValidation.PasswordCharset;

            if (c >= 'a' && c <= 'z')
                hasLower = true;
            else if (c >= '0' && c <= '9')
                hasDigit = true;
            else if (c < 'A' || c > 'Z')
                hasSymbol = true;
        }

        // 대문자는 <b>묻지 않는다</b> — UGS가 요구하지만 보낼 때 채운다 (ToProviderPassword).
        // 나머지 셋은 종전대로 사용자에게 요구한다.
        if (!hasLower || !hasDigit || !hasSymbol)
            return EAccountValidation.PasswordComposition;

        return EAccountValidation.Ok;
    }

    /// <summary>
    /// UGS로 보낼 비밀번호 — 대문자가 없으면 <see cref="k_providerPasswordSuffix"/>를 붙여 채운다.
    /// 가입·로그인 <b>양쪽이 모두</b> 이걸 거쳐야 같은 값이 오간다.
    ///
    /// <b>조건부인 것이 핵심이다</b> — 이 규칙이 생기기 전에 만든 계정의 비밀번호는 대문자를 이미
    /// 갖고 있으므로 원문 그대로 나가고, 그래서 기존 계정이 그대로 로그인된다. 무조건 붙이면 전부 막힌다.
    /// </summary>
    public static string ToProviderPassword(string password)
    {
        string pw = password ?? string.Empty;
        return HasUpper(pw) ? pw : pw + k_providerPasswordSuffix;
    }

    // 대문자가 하나라도 있는가 — UGS 조합 규칙 중 우리가 사용자에게 묻지 않는 유일한 항목이다.
    private static bool HasUpper(string password)
    {
        foreach (char c in password)
        {
            if (c >= 'A' && c <= 'Z')
                return true;
        }

        return false;
    }

    /// <summary>
    /// 검사 결과를 표시용 사유로. 통과면 빈 값.
    /// 길이 문구의 인자는 여기서 채운다 — 상·하한 상수의 주인이 이 클래스다.
    /// </summary>
    public static LocalizedMessage Describe(EAccountValidation result) =>
        result switch
        {
            EAccountValidation.Ok => LocalizedMessage.None,
            EAccountValidation.UsernameLength => Message(
                result,
                k_minUsernameLength,
                k_maxUsernameLength
            ),
            EAccountValidation.PasswordLength => Message(
                result,
                k_minPasswordLength,
                k_maxPasswordLength
            ),
            _ => Message(result),
        };

    private static LocalizedMessage Message(EAccountValidation result, params object[] args) =>
        LocalizedMessage.Of(k_table, k_validationPrefix + result, args);

    // UGS 에러 코드 — #384 스파이크 실측값. (2026-07-28)
    // SDK의 AuthenticationErrorCodes에 대응 상수가 없거나 이름이 버전마다 달라
    // 공식 문서에도 숫자 매핑이 없다. 관측한 값을 여기 고정하고 근거를 남긴다.
    private const int k_errorInvalidFormat = 10002; // 아이디/비번 형식 위반
    private const int k_errorUsernameTaken = 10003; // 그 아이디를 다른 플레이어가 이미 씀
    private const int k_errorAlreadyLinked = 10004; // 내 계정에 이미 아이디가 붙어 있음

    /// <summary>UGS 에러를 사용자에게 보일 분류로. (#384)</summary>
    public static EAccountError ClassifyError(RequestFailedException ex)
    {
        switch (ex.ErrorCode)
        {
            case k_errorUsernameTaken:
                return EAccountError.UsernameTaken;
            case k_errorAlreadyLinked:
                return EAccountError.AlreadyLinked;
            case k_errorInvalidFormat:
                return EAccountError.InvalidFormat;
        }

        // 아이디·비번 불일치(서버 title: WRONG_USERNAME_PASSWORD)는 SDK가 코드로 매핑하지 않아
        // ErrorCode가 0으로 온다(실측). 네트워크·서비스 장애도 같은 0이라 둘을 가릴 수 없으므로
        // 어느 하나로 단정하지 않는다 — "비밀번호가 틀렸다"고 잘라 말하면 서버 장애 때 거짓이 된다.
        // 진단에 필요한 값은 화면이 아니라 로그로 보낸다.
        Debug.LogWarning(
            $"[AccountCredentials] 미분류 인증 오류 code={ex.ErrorCode}: {ex.Message}"
        );
        return EAccountError.Unclassified;
    }

    /// <summary>오류 분류를 표시용 사유로.</summary>
    public static LocalizedMessage Describe(EAccountError error) =>
        LocalizedMessage.Of(k_table, k_errorPrefix + error);

    /// <summary>UGS 에러를 곧바로 표시용 사유로 — 분류를 따로 쓸 일이 없는 호출부용.</summary>
    public static LocalizedMessage DescribeError(RequestFailedException ex) =>
        Describe(ClassifyError(ex));
}
