using Cysharp.Threading.Tasks;

/// <summary>
/// 튜토리얼 출입 단일 경로 (#663) — 들어가기·나가기·"이미 권했는가" 플래그를 한자리에 모은다.
/// 타이틀(버튼·첫 접속 안내)과 튜토리얼 씬(<see cref="TutorialDirector"/>) 양쪽이 부른다.
///
/// 매니저가 아니다 — 상태가 PlayerPrefs 한 칸뿐이라 씬 오브젝트일 이유가 없다.
/// <see cref="SessionFlow"/>와 같은 결의 static 진입점이다.
/// </summary>
public static class TutorialFlow
{
    // 점 계층 접두사 관례 — AuthBootstrap의 "player.nickname."과 같다.
    private const string k_offeredPrefKey = "tutorial.offered";

    /// <summary>
    /// 이미 튜토리얼을 권했는가 — 첫 접속 안내를 두 번 띄우지 않기 위한 기억. (#663 완료기준 5)
    /// "완주했는가"가 아니라 "권했는가"다. 건너뛴 사람에게도 다시 묻지 않는 것이 요구사항이다.
    /// </summary>
    public static bool WasOffered => UnityEngine.PlayerPrefs.GetInt(k_offeredPrefKey, 0) == 1;

    /// <summary>권했다고 기록한다 — 안내를 띄운 순간과 튜토리얼에 들어간 순간 양쪽에서 부른다.</summary>
    public static void MarkOffered()
    {
        UnityEngine.PlayerPrefs.SetInt(k_offeredPrefKey, 1);
        UnityEngine.PlayerPrefs.Save();
    }

    /// <summary>튜토리얼로 들어간다 — 타이틀의 [튜토리얼] 버튼과 첫 접속 안내가 부른다.</summary>
    public static void Enter()
    {
        MarkOffered();
        App.LoadScene(EScene.Tutorial);
    }

    /// <summary>
    /// 튜토리얼에서 타이틀로 나간다 — 건너뛰기·완주·일시정지의 나가기가 모두 이 길이다.
    ///
    /// <see cref="SessionFlow.LeaveToMainAsync"/>를 그대로 쓴다. 튜토리얼은 UGS 세션 없이
    /// 로컬 호스트만 띄우므로 그쪽 세션 이탈 단계는 무동작으로 지나가고, NGO를 내리는 일은
    /// 같은 함수의 "세션 없는 로컬 호스트" 갈래가 맡는다 — 여기서 다시 구현하지 않는다.
    /// </summary>
    public static void Exit() => SessionFlow.LeaveToMainAsync().Forget();
}
