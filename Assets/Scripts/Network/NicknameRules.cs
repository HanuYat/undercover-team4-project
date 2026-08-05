/// <summary>
/// 닉네임 형식 검사 결과. 값 이름이 곧 사유 문구의 키다 — <c>Title.NicknameValidation.</c> + 이름 (#497).
/// </summary>
[LocalizedEnum("TitleTable", "Title.NicknameValidation.", nameof(ENicknameValidation.Ok))]
public enum ENicknameValidation
{
    Ok = 0,
    Empty = 1,
    Whitespace = 2,
    TooLong = 3,
}

/// <summary>
/// 닉네임의 형식 규칙과 표시용 이름 변환. (#249)
/// UGS는 길이·문자셋을 제한하지 않고 공백만 거절하며 그 응답도 불친절해, 규칙은 우리가 정한다.
/// 상태가 없는 순수 규칙이라 AuthBootstrap에서 분리했다 — AccountCredentials와 같은 방식.
/// 사유를 문장이 아니라 enum으로 돌려주는 것도 같다 (#497).
/// </summary>
public static class NicknameRules
{
    /// <summary>
    /// 닉네임 최대 글자 수 — UGS는 길이를 제한하지 않으므로 우리가 정한다.
    /// 이름표가 FixedString64Bytes(실사용 61바이트)로 동기화되는데 한글은 UTF-8 3바이트라
    /// 20자를 넘으면 CopyFromTruncated가 조용히 잘라낸다. 머리 위 가독성까지 고려해 여유를 뒀다.
    /// </summary>
    private const int k_maxLength = 12;

    private const string k_table = "TitleTable";
    private const string k_validationPrefix = "Title.NicknameValidation.";

    /// <summary>UI 입력 상한의 단일 출처 — AuthPanel이 characterLimit에 쓴다.</summary>
    public static int MaxLength => k_maxLength;

    /// <summary>입력 규칙 검사 — 통과면 <see cref="ENicknameValidation.Ok"/>.</summary>
    public static ENicknameValidation Validate(string trimmed)
    {
        if (string.IsNullOrEmpty(trimmed))
            return ENicknameValidation.Empty;

        foreach (char c in trimmed)
        {
            if (char.IsWhiteSpace(c))
                return ENicknameValidation.Whitespace;
        }

        if (trimmed.Length > k_maxLength)
            return ENicknameValidation.TooLong;

        return ENicknameValidation.Ok;
    }

    /// <summary>검사 결과를 표시용 사유로. 통과면 빈 값. 길이 인자는 상수의 주인인 여기서 채운다.</summary>
    public static LocalizedMessage Describe(ENicknameValidation result) =>
        result switch
        {
            ENicknameValidation.Ok => LocalizedMessage.None,
            ENicknameValidation.TooLong => LocalizedMessage.Of(
                k_table,
                k_validationPrefix + result,
                k_maxLength
            ),
            _ => LocalizedMessage.Of(k_table, k_validationPrefix + result),
        };

    /// <summary>UGS가 PlayerName에 자동으로 붙이는 #1234 판별자를 떼어낸 표시용 이름.</summary>
    public static string StripDiscriminator(string playerName)
    {
        if (string.IsNullOrEmpty(playerName))
            return string.Empty;

        int hash = playerName.LastIndexOf('#');
        return hash >= 0 ? playerName.Substring(0, hash) : playerName;
    }
}
