#if UNITY_EDITOR
using System.IO;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// ⚠ <b>임시 계측의 공용 기록기</b> — `feature/ragdoll-hotfix` 전용. 검증이 끝나면
/// <c>Assets/Scripts/Diagnostics/</c> 폴더째 지운다.
///
/// <b>왜 따로 두나.</b> 시체 진단은 두 곳에서 나온다 — 프레임 단위 표본
/// (<see cref="CorpseTeleportDiag"/>)과 배치 단계 추적(<c>NpcRagdoll.LogPlacement</c>). 그런데
/// 배치 추적은 서버에서만 도는 <c>Debug.Log</c>라 <b>호스트 콘솔에만</b> 남았고, 표본은 파일에
/// 남았다 — 같은 순간을 보려면 두 곳을 오가야 했다. 여기로 모으면 <b>한 파일에서 시간순으로</b> 읽힌다.
///
/// <b>역할별로 파일이 갈린다.</b> MPPM 가상 플레이어는 자기 프로젝트 루트(<c>Library/VP/...</c>)를
/// 가지므로 프로세스마다 다른 파일에 쓴다. 그것을 하나로 합치는 것은
/// <c>CorpseDiagCollector</c>(Tools 메뉴)가 한다.
/// </summary>
public static class CorpseDiagLog
{
    /// <summary>이 프로세스가 쓰는 로그 파일 경로 — 역할(호스트/클라N/오프라인)로 갈린다.</summary>
    public static string PathForThisPeer() =>
        Path.Combine(Application.dataPath, "..", "Logs", "corpse-diag-" + Role() + ".log");

    /// <summary>지금 서버 틱 — 전 피어가 공유하는 유일한 시계라 병합의 정렬 키다. 세션이 아니면 −1.</summary>
    public static long ServerTick() =>
        NetworkManager.Singleton != null ? NetworkManager.Singleton.ServerTime.Tick : -1;

    public static string Role()
    {
        NetworkManager net = NetworkManager.Singleton;
        if (net == null || !net.IsListening)
            return "오프라인";
        if (net.IsHost)
            return "호스트";
        if (net.IsServer)
            return "서버";
        return "클라" + net.LocalClientId;
    }

    /// <summary>
    /// 콘솔과 파일 양쪽에 한 줄 남긴다. <b>한 줄이어야 한다</b> — MCP <c>read_console</c>이 첫 줄만
    /// 가져오므로 판별점이 둘째 줄로 밀리면 도구로는 안 보인다.
    /// </summary>
    /// <param name="line">이미 완성된 한 줄. 줄바꿈을 넣지 말 것.</param>
    /// <param name="alsoConsole">거짓이면 파일에만 — 프레임마다 나오는 표본으로 콘솔을 덮지 않게.</param>
    public static void Write(string line, bool alsoConsole = true)
    {
        if (alsoConsole)
            Debug.Log(line);

        try
        {
            string path = PathForThisPeer();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.AppendAllText(path, line + "\n");
        }
        catch (System.Exception)
        {
            // 파일이 안 되면 콘솔만으로 간다 — 계측이 게임을 멈추게 두지 않는다.
        }
    }
}
#endif
