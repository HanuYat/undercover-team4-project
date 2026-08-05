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
    private const int k_maxPasswordLength = 30;

    private const string k_table = "TitleTable";
    private const string k_validationPrefix = "Title.AccountValidation.";
    private const string k_errorPrefix = "Title.AccountError.";

    /// <summary>UI 입력 상한의 단일 출처 — AuthPanel이 characterLimit에 쓴다. (#249의 NicknameRules.MaxLength와 같은 방식)</summary>
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
        bool hasUpper = false;
        bool hasDigit = false;
        bool hasSymbol = false;
        foreach (char c in password)
        {
            // 한글 입력(IME가 한글 모드로 남은 경우)과 공백을 여기서 걸러낸다 — 실제로 겪은 함정.
            if (c < '!' || c > '~')
                return EAccountValidation.PasswordCharset;

            if (c >= 'a' && c <= 'z')
                hasLower = true;
            else if (c >= 'A' && c <= 'Z')
                hasUpper = true;
            else if (c >= '0' && c <= '9')
                hasDigit = true;
            else
                hasSymbol = true;
        }

        if (!hasLower || !hasUpper || !hasDigit || !hasSymbol)
            return EAccountValidation.PasswordComposition;

        return EAccountValidation.Ok;
    }

    /// <summary>
    /// 검사 결과를 표시용 사유로. 통과면 빈 값.
    /// 길이 문구의 인자는 여기서 채운다 — 상·하한 상수의 주인이 이 클래스다.
    /// </summary>
    public static LocalizedMessage Describe(EAccountValidation result) =>
        result switch
        {
            EAccountValidation.Ok => LocalizedMessage.None,
            EAccountValidation.UsernameLength => Message(result, k_minUsernameLength, k_maxUsernameLength),
            EAccountValidation.PasswordLength => Message(result, k_minPasswordLength, k_maxPasswordLength),
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
        Debug.LogWarning($"[AccountCredentials] 미분류 인증 오류 code={ex.ErrorCode}: {ex.Message}");
        return EAccountError.Unclassified;
    }

    /// <summary>오류 분류를 표시용 사유로.</summary>
    public static LocalizedMessage Describe(EAccountError error) =>
        LocalizedMessage.Of(k_table, k_errorPrefix + error);

    /// <summary>UGS 에러를 곧바로 표시용 사유로 — 분류를 따로 쓸 일이 없는 호출부용.</summary>
    public static LocalizedMessage DescribeError(RequestFailedException ex) => Describe(ClassifyError(ex));
}
