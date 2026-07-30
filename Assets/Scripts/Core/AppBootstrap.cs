using UnityEngine;

/// <summary>
/// 상주 매니저 루트 — 자신을 DontDestroyOnLoad로 승격하고, 이미 상주 인스턴스가 있으면
/// 자신(자식 매니저 포함)을 즉시 비활성화 후 파괴하는 중복 가드. (#247, 3단계)
/// 실행 순서 Bootstrap(-400): 자식 매니저 Awake(-300)보다 가드가 먼저 돌아야
/// 파괴될 사본이 App에 등록되는 일 자체가 없다 (등록 후 파괴 시 필드가 비는 경계 사례 차단).
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.Bootstrap)]
public class AppBootstrap : MonoBehaviour
{
    // 1 = 매 수직 공백마다 표시. 프레임 상한은 모니터 주사율이 정한다.
    private const int k_vSyncCount = 1;

    private static AppBootstrap s_instance;

    private void Awake()
    {
        if (s_instance != null)
        {
            // SetActive(false)를 먼저 — Destroy는 프레임 끝에 실행되므로, 끄지 않으면
            // 이 프레임에 자식 매니저들의 Awake(App 등록)가 돌아버린다.
            // (Awake가 안 돈 컴포넌트는 OnDestroy도 호출되지 않아 상주 인스턴스의 등록을 지울 일이 없다)
            gameObject.SetActive(false);
            Destroy(gameObject);
            return;
        }

        s_instance = this;
        DontDestroyOnLoad(gameObject);
        ApplyDisplaySettings();
    }

    /// <summary>
    /// 표시(present) 설정 — 빌드에서 수직동기를 켠다 (에디터는 끈다, 아래 주석 참고).
    ///
    /// VSync가 꺼져 있으면 모니터가 화면을 위에서 아래로 그리는 중간에 새 프레임 버퍼로 갈아타서,
    /// 가로 절단선을 기준으로 위아래가 좌우로 어긋나 보인다(티어링). 시점을 빠르게 돌릴 때
    /// 프레임 간 그림 차이가 커지므로 회전 중에 특히 두드러진다.
    ///
    /// 품질 레벨 에셋(QualitySettings.asset)에도 같은 값을 넣어 두지만, 레벨이 늘거나 누군가
    /// 되돌려도 새지 않게 여기서 한 번 더 못 박는다. (런타임에 품질 레벨을 바꾸는 코드는 없다 —
    /// 만약 추가한다면 SetQualityLevel이 그 레벨의 vSyncCount로 덮으므로 이 값을 다시 적용해야 한다)
    /// </summary>
    private static void ApplyDisplaySettings()
    {
        // 에디터에서는 끈다 — 에디터는 빌드보다 무거워 주사율 타이밍을 자주 놓치는데, VSync가 켜져 있으면
        // 한 번 놓칠 때마다 다음 수직 공백까지 기다려 프레임이 절반(100Hz면 50)으로 떨어진다.
        // 이 왕복이 개발 중 계속 끊김으로 체감된다. Game 뷰는 에디터 창 안에서 합성되므로
        // VSync를 꺼도 티어링이 거의 보이지 않아, 끄는 쪽이 손해가 없다.
        QualitySettings.vSyncCount = Application.isEditor ? 0 : k_vSyncCount;

        // vSyncCount != 0이면 targetFrameRate는 무시되므로 상한을 따로 걸지 않는다.
        // (기존 60 고정은 60Hz 모니터에서만 성립했고, VSync를 끈 채 캡을 걸어 티어링을 만들고 있었다)
        Application.targetFrameRate = -1;

        // 표시 환경을 Player.log에 남긴다 — 모니터가 제각각인 팀에서 화면 관련 제보를 받을 때
        // 주사율·전체화면 모드부터 확인할 수 있어야 한다.
        Debug.Log(
            $"[AppBootstrap] 표시 설정 — vSync={QualitySettings.vSyncCount}"
                + $" 상한={Application.targetFrameRate}"
                + $" 주사율={Screen.currentResolution.refreshRateRatio.value:F1}Hz"
                + $" 모드={Screen.fullScreenMode}"
        );
    }

    // Enter Play Mode Options에서 도메인 리로드를 꺼도 이전 플레이의 인스턴스 참조가 남지 않도록 리셋 (App과 동일 방침)
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => s_instance = null;
}
