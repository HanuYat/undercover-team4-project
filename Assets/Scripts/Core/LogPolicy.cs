using UnityEngine;

/// <summary>
/// 릴리스 빌드 로그 차단 — 출시 정리(#출시). <c>Debug.Log/LogWarning/LogError</c> 출력을 전부 막는다.
///
/// <b>막는 것은 출력뿐이다.</b> 호출 지점은 그대로 남아 인자(문자열 보간·ToString·계산)는 매번 실행된다.
/// 매 프레임 도는 자리의 로그는 GC 할당이 그대로 남으므로, 비용까지 지우려면 그 호출을
/// <c>[Conditional]</c> 래퍼로 감싸야 한다 — 이 클래스가 대신해 주지 않는다.
///
/// 에디터와 Development Build는 건드리지 않는다(<see cref="Debug.isDebugBuild"/>) — 개발 중에
/// 콘솔이 통째로 죽으면 안 되고, QA용 개발 빌드도 로그가 남아야 진단이 된다.
/// 릴리스 빌드에서는 Player.log가 비므로, 유저 버그 리포트로 원인을 좇을 수단이 사라진다는 점은 감수한다.
/// (미처리 예외는 엔진이 별도로 기록하므로 크래시 원인은 남는다.)
/// </summary>
public static class LogPolicy
{
    // SubsystemRegistration = 씬 로드보다도 앞. 같은 시점의 다른 초기화(App·AppBootstrap 등)와는
    // 호출 순서가 정해져 있지 않아 부팅 초반 몇 줄은 지나갈 수 있다 — 그 뒤로는 전부 막힌다.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Apply()
    {
        if (Debug.isDebugBuild)
            return;

        Debug.unityLogger.logEnabled = false;
    }
}
