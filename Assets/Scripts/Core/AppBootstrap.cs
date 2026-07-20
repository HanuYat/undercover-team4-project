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
    private const int k_targetFrameRate = 60;

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
        Application.targetFrameRate = k_targetFrameRate;
    }

    // Enter Play Mode Options에서 도메인 리로드를 꺼도 이전 플레이의 인스턴스 참조가 남지 않도록 리셋 (App과 동일 방침)
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => s_instance = null;
}
