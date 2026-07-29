using Unity.Services.Core;
using UnityEngine;

/// <summary>
/// 계정 자격증명의 형식 규칙과 오류 메시지. (#384)
/// UGS 규칙을 클라이언트에서 먼저 검사하는 이유는 서버가 형식 위반을
/// InvalidParameters(10002) 하나로 뭉쳐 보내며 **어느 쪽이 틀렸는지 알려주지 않기** 때문이다.
/// 상태가 없는 순수 규칙이라 AuthBootstrap에서 분리했다.
/// </summary>
public static class AccountCredentials
{
    private const int k_minUsernameLength = 3;
    private const int k_maxUsernameLength = 20;
    private const int k_minPasswordLength = 8;
    private const int k_maxPasswordLength = 30;

    /// <summary>UI 입력 상한의 단일 출처 — AuthPanel이 characterLimit에 쓴다. (#249의 MaxNicknameLength와 같은 방식)</summary>
    public static int MaxUsernameLength => k_maxUsernameLength;
    public static int MaxPasswordLength => k_maxPasswordLength;

    /// <summary>형식 검사 — 위반이면 사유 문자열, 통과면 null.</summary>
    public static string Validate(string username, string password)
    {
        if (string.IsNullOrEmpty(username))
            return "아이디를 입력해 주세요.";

        if (username.Length < k_minUsernameLength || username.Length > k_maxUsernameLength)
            return $"아이디는 {k_minUsernameLength}~{k_maxUsernameLength}자여야 합니다.";

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
                return "아이디에는 영문·숫자와 . - @ _ 만 쓸 수 있습니다.";
        }

        if (string.IsNullOrEmpty(password))
            return "비밀번호를 입력해 주세요.";

        if (password.Length < k_minPasswordLength || password.Length > k_maxPasswordLength)
            return $"비밀번호는 {k_minPasswordLength}~{k_maxPasswordLength}자여야 합니다.";

        bool hasLower = false;
        bool hasUpper = false;
        bool hasDigit = false;
        bool hasSymbol = false;
        foreach (char c in password)
        {
            // 한글 입력(IME가 한글 모드로 남은 경우)과 공백을 여기서 걸러낸다 — 실제로 겪은 함정.
            if (c < '!' || c > '~')
                return "비밀번호에는 영문·숫자·기호만 쓸 수 있습니다.";

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
            return "비밀번호에 영문 대·소문자, 숫자, 기호를 각각 1자 이상 포함해야 합니다.";

        return null;
    }

    // UGS 에러 코드 — #384 스파이크 실측값. (2026-07-28)
    // SDK의 AuthenticationErrorCodes에 대응 상수가 없거나 이름이 버전마다 달라
    // 공식 문서에도 숫자 매핑이 없다. 관측한 값을 여기 고정하고 근거를 남긴다.
    private const int k_errorInvalidFormat = 10002; // 아이디/비번 형식 위반
    private const int k_errorUsernameTaken = 10003; // 그 아이디를 다른 플레이어가 이미 씀
    private const int k_errorAlreadyLinked = 10004; // 내 계정에 이미 아이디가 붙어 있음

    /// <summary>UGS 에러를 사용자에게 보일 문장으로. (#384)</summary>
    public static string DescribeError(RequestFailedException ex)
    {
        switch (ex.ErrorCode)
        {
            case k_errorUsernameTaken:
                return "이미 사용 중인 아이디입니다. 다른 아이디를 써 주세요.";
            case k_errorAlreadyLinked:
                return "이 계정에는 이미 아이디가 연동되어 있습니다.";
            case k_errorInvalidFormat:
                return "아이디 또는 비밀번호 형식이 올바르지 않습니다.";
        }

        // 아이디·비번 불일치(서버 title: WRONG_USERNAME_PASSWORD)는 SDK가 코드로 매핑하지 않아
        // ErrorCode가 0으로 온다(실측). 네트워크·서비스 장애도 같은 0이라 둘을 가릴 수 없으므로
        // 어느 하나로 단정하지 않는다 — "비밀번호가 틀렸다"고 잘라 말하면 서버 장애 때 거짓이 된다.
        // 진단에 필요한 값은 화면이 아니라 로그로 보낸다.
        Debug.LogWarning($"[AccountCredentials] 미분류 인증 오류 code={ex.ErrorCode}: {ex.Message}");
        return "아이디 또는 비밀번호를 확인해 주세요. 계속 실패하면 잠시 후 다시 시도해 주세요.";
    }
}
