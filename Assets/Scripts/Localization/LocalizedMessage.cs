using System;

/// <summary>
/// 표시할 문구를 <b>완성 문장이 아니라 테이블 키 + 인자</b>로 나르는 값. 설계 정본: docs/design/localization.md (#497)
///
/// 문장을 만들어 넘기면 그 문장은 만든 시점의 언어로 굳는다. UI를 모르는 코드가 사유를 돌려주는 자리
/// (<see cref="AccountCredentials"/> · <see cref="NicknameRules"/> · <see cref="AuthBootstrap"/>의 예외)가
/// 그래서 문제였다 — 화면에 띄운 뒤 언어를 바꿔도 그 줄만 옛 언어로 남는다.
/// 키로 나르면 <b>표시하는 쪽이 원할 때마다</b> 지금 언어로 읽는다.
///
/// 인자에 <see cref="LocalizedMessage"/>를 넣으면 읽을 때 그것도 함께 풀린다 —
/// "세션 참가 중에는 {계정을 연동할} 수 없습니다"처럼 조각을 끼우는 문구용이다.
/// </summary>
public readonly struct LocalizedMessage
{
    private readonly string m_table;
    private readonly string m_key;
    private readonly object[] m_args;
    private readonly string m_literal; // 번역 대상이 아닌 원문 — 외부 SDK가 준 메시지 등

    private LocalizedMessage(string table, string key, object[] args, string literal)
    {
        m_table = table;
        m_key = key;
        m_args = args;
        m_literal = literal;
    }

    /// <summary>띄울 것이 없음 — 상태 줄을 비울 때.</summary>
    public static LocalizedMessage None => default;

    public static LocalizedMessage Of(string table, string key, params object[] args) =>
        new LocalizedMessage(table, key, args, null);

    /// <summary>번역하지 않고 그대로 띄울 원문 — 우리 테이블에 없는 외부 문구(UGS SDK 메시지 등).</summary>
    public static LocalizedMessage Literal(string text) =>
        new LocalizedMessage(null, null, null, text);

    public bool IsEmpty => string.IsNullOrEmpty(m_key) && string.IsNullOrEmpty(m_literal);

    /// <summary>진단용 표기 — 로그·예외 메시지에 쓴다. 번역하지 않는다.</summary>
    public string KeyPath =>
        m_literal ?? (string.IsNullOrEmpty(m_key) ? "(없음)" : m_table + "/" + m_key);

    /// <summary>지금 언어로 읽는다. 비어 있으면 빈 문자열.</summary>
    public string Resolve()
    {
        if (!string.IsNullOrEmpty(m_literal))
            return m_literal;

        if (string.IsNullOrEmpty(m_key))
            return string.Empty;

        return LocalizedStrings.Get(m_table, m_key, ResolveArgs());
    }

    // 인자로 들어온 LocalizedMessage를 먼저 푼다 — 조각도 지금 언어여야 한다.
    private object[] ResolveArgs()
    {
        if (m_args == null || m_args.Length == 0)
            return Array.Empty<object>();

        var resolved = new object[m_args.Length];
        for (int i = 0; i < m_args.Length; i++)
            resolved[i] = m_args[i] is LocalizedMessage nested ? nested.Resolve() : m_args[i];
        return resolved;
    }
}

/// <summary>
/// 사용자에게 보일 사유를 문장이 아니라 <see cref="LocalizedMessage"/>로 나르는 예외 (#497).
/// 예외 메시지를 그대로 화면에 띄우던 자리(<see cref="AuthBootstrap"/> → <see cref="AuthGatePanel"/>) 전용이다.
/// <see cref="Exception.Message"/>에는 키를 담는다 — 로그에서 어느 문구인지 보이게 하되 번역은 하지 않는다.
/// </summary>
public class LocalizedMessageException : Exception
{
    public LocalizedMessageException(LocalizedMessage reason)
        : base(reason.KeyPath) => Reason = reason;

    /// <summary>표시할 사유 — 받는 쪽이 자기 언어로 읽는다.</summary>
    public LocalizedMessage Reason { get; }
}
