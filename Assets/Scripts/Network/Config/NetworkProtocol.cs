using System;
using System.Text;
using UnityEngine;

/// <summary>
/// 네트워크 호환성 식별자의 단일 출처 (#586) — 버전이 다른 빌드가 한 세션에 섞이는 것을 막는다.
/// A층(세션 프로퍼티, 클라가 스스로 물러남)과 B층(연결 승인, <see cref="ConnectionApprovalGate"/>가
/// 호스트에서 거부)이 반대 방향에서 이 값을 검사한다 (#628) — 하나가 다른 하나의 대체가 아니다.
/// </summary>
public static class NetworkProtocol
{
    /// <summary>
    /// 네트워크 호환성이 깨지는 변경마다 손으로 +1. 마케팅 버전(bundleVersion)은 이런 변경에 따라
    /// 오르지 않으므로 별도로 둔다.
    /// </summary>
    public const int k_protocolVersion = 2;

    public static string VersionString => $"{Application.version}#{k_protocolVersion}";

    /// <summary>버전 정보가 아예 없을 때의 표시 (구버전 빌드/페이로드 없음). 값이 다른 것과 똑같이 취급.</summary>
    public const string k_unknownVersion = "?";

    private const string k_mismatchReasonPrefix = "VER#";
    private const char k_payloadSeparator = '\n';

    /// <summary>NetworkConfig.ConnectionData / 승인 Payload에 실을 바이트 (#628, sha는 #622).</summary>
    public static byte[] EncodePayload() =>
        Encoding.UTF8.GetBytes(VersionString + k_payloadSeparator + BuildStamp.Sha);

    /// <summary>
    /// 반환 타입이 string이 아니라 PeerStamp인 이유 — 호출자가 통짜 문자열을 비교하다 sha가
    /// 참가 차단에 끼어드는 사고를 타입으로 막는다 (#622). 차단 판정은 Version 필드만 본다.
    /// </summary>
    public static PeerStamp DecodePayload(byte[] payload)
    {
        if (payload == null || payload.Length == 0)
            return new PeerStamp(k_unknownVersion, BuildStamp.k_unknownSha);

        try
        {
            string decoded = Encoding.UTF8.GetString(payload);
            int separatorIndex = decoded.IndexOf(k_payloadSeparator);
            return separatorIndex < 0
                ? new PeerStamp(decoded, BuildStamp.k_unknownSha)
                : new PeerStamp(decoded[..separatorIndex], decoded[(separatorIndex + 1)..]);
        }
        catch (Exception)
        {
            return new PeerStamp(k_unknownVersion, BuildStamp.k_unknownSha);
        }
    }

    /// <summary>사람이 읽는 문장이 아니라 <see cref="TryParseMismatchReason"/>이 되돌려 파싱할 값이다.</summary>
    public static string BuildMismatchReason(string hostVersion) =>
        k_mismatchReasonPrefix + hostVersion;

    public static bool TryParseMismatchReason(string reason, out string hostVersion)
    {
        if (!string.IsNullOrEmpty(reason) && reason.StartsWith(k_mismatchReasonPrefix))
        {
            hostVersion = reason.Substring(k_mismatchReasonPrefix.Length);
            return true;
        }

        hostVersion = null;
        return false;
    }
}

// 승인 페이로드 한 쪽의 정보 (#622) — Version은 차단 판정에, Sha는 로그 진단에만 쓴다.
public readonly struct PeerStamp
{
    public string Version { get; }
    public string Sha { get; }

    public PeerStamp(string version, string sha)
    {
        Version = version;
        Sha = sha;
    }
}

/// <summary>
/// 참가한 세션의 게임 버전이 로컬과 달라 참가를 물렸을 때 (#586).
/// 전용 타입인 이유는 UI가 이 실패만 다르게 말해야 하기 때문이다 — 사용자가 할 일이
/// "새 빌드를 받아라"로 정해져 있어 예외 메시지를 그대로 보여 줄 필요가 없다.
/// </summary>
public class SessionVersionMismatchException : Exception
{
    public string LocalVersion { get; }
    public string SessionVersion { get; }

    public SessionVersionMismatchException(string localVersion, string sessionVersion)
        : base($"게임 버전 불일치 — 내 버전 {localVersion} / 방 버전 {sessionVersion}")
    {
        LocalVersion = localVersion;
        SessionVersion = sessionVersion;
    }
}
