using UnityEngine;

/// <summary>
/// 전자기기 먹통의 클라이언트 표현 — 먹통 이벤트의 플래그(<see cref="DeviceBlackoutEvent.IsCommsBlackout"/>)를 구독해
/// 음성 왜곡(<see cref="VivoxManager.SetVoiceDistorted"/>)을 켜고 끈다. (GDD 6-4/4-4, #106/#372/#434)
/// 서버·원격 클라·오프라인 모든 피어에서 각자 자기 무전을 처리한다
/// (플래그 변화는 <see cref="DeviceBlackoutEvent.OnCommsBlackoutChanged"/>가 전 피어에서 발행한다).
///
/// CCTV 차단은 <see cref="CCTVSwitcher"/>가 같은 플래그를 직접 구독해 처리하므로 여기서 다루지 않는다.
/// 화면을 덮는 시야 제한은 제거했다(#434) — 이동·상호작용이 사실상 정지해 플레이 감각을 해쳤고,
/// 먹통의 의도는 "앞이 안 보인다"가 아니라 "본부와 현장이 서로를 잃는다"이기 때문이다.
/// </summary>
public class DeviceBlackoutView : MonoBehaviour
{
    [Header("참조 (비우면 씬에서 자동 탐색)")]
    [SerializeField] private DeviceBlackoutEvent m_blackout;

    private VivoxManager Vivox => App.Net.Vivox;

    private void Start()
    {
        // 비워두면 돌발 이벤트 매니저에게 물어본다 — 먹통 이벤트는 App에 따로 등록하지 않는다 (#372 리뷰, R1/R3).
        // 이벤트 풀에서 항목을 껐거나 등록하지 않은 구성이면 null이 오고, 그 경우 먹통은 발생하지도 않는다.
        if (m_blackout == null)
            m_blackout = App.Game.SuddenEvent?.GetEvent<DeviceBlackoutEvent>();

        if (m_blackout == null)
        {
            Debug.LogWarning("DeviceBlackoutView: DeviceBlackoutEvent를 찾지 못해 먹통 표현이 동작하지 않는다", this);
            return;
        }

        m_blackout.OnCommsBlackoutChanged += HandleBlackoutChanged;
        // 늦게 붙었을 때(이미 먹통 진행 중) 현재 상태를 즉시 반영
        HandleBlackoutChanged(m_blackout.IsCommsBlackout);
    }

    private void OnDestroy()
    {
        if (m_blackout != null)
            m_blackout.OnCommsBlackoutChanged -= HandleBlackoutChanged;
    }

    private void HandleBlackoutChanged(bool active)
    {
        // 음성 왜곡은 이 피어의 무전(VivoxManager)에 위임 — 없으면(본부 단독·미설정) 건너뛴다.
        // 차단이 아니라 왜곡인 이유(#372): 완전 침묵은 답답하고 버그로 오인된다. 망가진 소리는
        // 이벤트 발생을 즉시 알리면서 알아듣기 어려워 통신 제한 효과도 낸다.
        if (Vivox != null)
            Vivox.SetVoiceDistorted(active);
    }
}
