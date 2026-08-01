/// <summary>
/// 닉네임의 형식 규칙과 표시용 이름 변환. (#249)
/// UGS는 길이·문자셋을 제한하지 않고 공백만 거절하며 그 응답도 불친절해, 규칙은 우리가 정한다.
/// 상태가 없는 순수 규칙이라 AuthBootstrap에서 분리했다 — AccountCredentials와 같은 방식.
/// </summary>
public static class NicknameRules
{
    /// <summary>
    /// 닉네임 최대 글자 수 — UGS는 길이를 제한하지 않으므로 우리가 정한다.
    /// 이름표가 FixedString64Bytes(실사용 61바이트)로 동기화되는데 한글은 UTF-8 3바이트라
    /// 20자를 넘으면 CopyFromTruncated가 조용히 잘라낸다. 머리 위 가독성까지 고려해 여유를 뒀다.
    /// </summary>
    private const int k_maxLength = 12;

    /// <summary>UI 입력 상한의 단일 출처 — AuthPanel이 characterLimit에 쓴다.</summary>
    public static int MaxLength => k_maxLength;

    /// <summary>입력 규칙 검사 — 위반이면 사유 문자열, 통과면 null.</summary>
    public static string Validate(string trimmed)
    {
        if (string.IsNullOrEmpty(trimmed))
            return "닉네임을 입력해 주세요.";

        foreach (char c in trimmed)
        {
            if (char.IsWhiteSpace(c))
                return "닉네임에 공백을 쓸 수 없습니다.";
        }

        if (trimmed.Length > k_maxLength)
            return $"닉네임은 {k_maxLength}자 이하여야 합니다.";

        return null;
    }

    /// <summary>UGS가 PlayerName에 자동으로 붙이는 #1234 판별자를 떼어낸 표시용 이름.</summary>
    public static string StripDiscriminator(string playerName)
    {
        if (string.IsNullOrEmpty(playerName))
            return string.Empty;

        int hash = playerName.LastIndexOf('#');
        return hash >= 0 ? playerName.Substring(0, hash) : playerName;
    }
}
