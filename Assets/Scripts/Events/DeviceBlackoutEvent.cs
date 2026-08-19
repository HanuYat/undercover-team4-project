using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 전자기기 먹통 (돌발 이벤트 · 전역) — 도시 인프라 장애로 통신·감시 설비가 마비된다. (GDD 6-4/4-4/7-4, #106)
/// 먹통 플래그를 <b>스스로 소유</b>해 서버 권위로 켜고 끄며, NetworkVariable로 전 클라에 동기화한다.
/// 실제 표현은 이 플래그를 구독하는 쪽이 각자 담당한다 — 무전 음성 왜곡은 <see cref="DeviceBlackoutView"/>,
/// CCTV 송출 차단은 <see cref="CCTVSwitcher"/>, 스캐너 사용 불가는 <see cref="Scanner"/>가 본다.
///
/// <b>스스로 풀리지 않는다</b> (#689). 라운드가 끝날 때까지 유지되고, 본부의 복구 단말이
/// <see cref="ServerRecover"/>를 불러야 해제된다. 예전에는 12초 뒤 자동 복구였는데 그러면 양쪽 다
/// "잠깐 참으면 되는 시간"이라 이벤트에 대한 <b>대응이 존재하지 않았다</b> — 해제 권한을 본부에 주면
/// 먹통이 관제의 일거리가 된다. <b>상한 시간을 두지 않는 것도 확정 사항이다</b>: 결국 알아서
/// 돌아오면 복구 절차가 다시 무의미해진다 (GDD 6-4 확정 노트).
///
/// 서버 권위 — 발생·해제 판정은 서버(또는 오프라인)에서만. 프레임워크(<see cref="SuddenEventManager"/>)가
/// ServerBegin/Tick/Reset을 서버에서만 부르므로 이 안에서는 권위를 다시 검사하지 않는다. (#56)
///
/// 전역 효과라 스폰물 대신 동기화 플래그로 전파한다 — 이 상태를 매니저가 아니라 이벤트가 들고 있어야
/// 프레임워크가 "먹통"이라는 이벤트의 존재를 몰라도 된다. NetworkVariable을 쓰려면 NetworkBehaviour여야 하는데,
/// 매니저가 요구하는 NetworkObject가 같은 오브젝트에 이미 있으므로 여기에 얹으면 된다.
///
/// <b>밖에서 어떻게 찾나</b>(#372) — 먹통 플래그를 아이템(스캐너)·무전 왜곡이 조회해야 하는데,
/// 아이템은 런타임 스폰 프리팹이라 인스펙터로 씬 오브젝트를 물릴 수 없다. 그렇다고 이벤트 하나마다
/// App에 필드를 다는 것도, <see cref="SuddenEventManager"/>에 먹통 상태를 얹는 것도(그러면 프레임워크가
/// 특정 이벤트를 알게 된다) 답이 아니다. 대신 <see cref="SuddenEventManager.GetEvent{T}"/>로 물어본다 —
/// 매니저는 여전히 이 타입을 모르고, App에는 매니저 하나만 남는다 (#372 리뷰, R3).
/// </summary>
[RequireComponent(typeof(SuddenEventManager))]
public class DeviceBlackoutEvent : NetworkBehaviour, ISuddenEvent
{
    // 복구 완료 알림 문구 키 — 발생 알림(NoticeKey)과 <b>같은 테이블</b>에서 찾는다 (SuddenEventToastView).
    // 발생용 키를 재활용하면 "먹통이 발생했습니다"가 복구 시점에 뜬다.
    private const string k_recoveredNoticeKey = "Hud.Event.Notice.BlackoutRecovered";

    // 먹통 전역 상태 — 서버만 쓰고 모든 클라가 읽는다. (PlayerHealth.m_syncedHp와 동일 이중 구조)
    private readonly NetworkVariable<bool> m_blackoutSynced = new NetworkVariable<bool>();
    private bool m_blackout; // 서버·오프라인의 진실값 (비네트워크 Play 폴백)

    public string DisplayName => "전자기기 먹통";

    public string NoticeKey => "Hud.Event.Notice.Blackout";

    // 먹통이 켜져 있는 동안이 곧 이벤트 진행 중 — 매니저는 이 값이 false가 될 때까지 재발생시키지 않는다.
    // (매니저가 IsActive를 읽는 것은 서버·오프라인에서뿐이므로 서버 진실값을 그대로 준다)
    public bool IsActive => m_blackout;

    /// <summary>통신 먹통이 활성인지 — 서버·오프라인은 실참조, 원격 피어는 동기화값으로 판정.</summary>
    public bool IsCommsBlackout => IsSpawned && !IsServer ? m_blackoutSynced.Value : m_blackout;

    /// <summary>먹통 상태가 바뀔 때 발행 — 음성 왜곡·CCTV 차단 등 표현 계층이 구독한다. (#67 무전 연동)</summary>
    public event Action<bool> OnCommsBlackoutChanged;

    public override void OnNetworkSpawn()
    {
        // 원격 피어는 서버의 Set 경로를 타지 않으므로, 동기화값 변화를 이벤트로 중계한다.
        m_blackoutSynced.OnValueChanged += HandleBlackoutSyncedChanged;

        // 늦게 접속한 클라: 이미 먹통이 진행 중이면 현재 상태를 즉시 반영한다.
        if (!IsServer && m_blackoutSynced.Value)
            OnCommsBlackoutChanged?.Invoke(true);
    }

    public override void OnNetworkDespawn()
    {
        m_blackoutSynced.OnValueChanged -= HandleBlackoutSyncedChanged;
    }

    // 서버(호스트 포함)는 SetBlackout에서 직접 발행하므로 여기선 원격 클라만 중계 (이중 발행 방지)
    private void HandleBlackoutSyncedChanged(bool previous, bool current)
    {
        if (IsServer)
            return;
        OnCommsBlackoutChanged?.Invoke(current);
    }

    // 전역 이벤트라 특별한 선행 조건이 없다 — 프레임워크가 라운드 진행 중에만 호출한다
    public bool CanTrigger() => true;

    public void ServerBegin()
    {
        SetBlackout(true);
    }

    // 할 일이 없다 — 지속 판정이 사라졌기 때문이다 (#689). 인터페이스가 요구하므로 선언만 남긴다.
    // 여기에 상한 시간을 되살리지 말 것: "결국 알아서 돌아온다"가 되면 복구 절차가 무의미해진다.
    public void ServerTick() { }

    /// <summary>
    /// 복구 — 본부 복구 단말이 절차를 통과시켰을 때 부른다. 서버(또는 오프라인) 전용. (#689)
    ///
    /// 실제로 풀렸을 때만 true다 — 이미 복구됐거나 애초에 먹통이 아니면 false. 부르는 쪽이 연출·소리를
    /// 중복해서 내지 않게 하려는 것이다(같은 프레임에 두 사람이 입력을 확정하는 경합이 있다).
    ///
    /// 표현 계층은 건드리지 않는다 — <see cref="SetBlackout"/> 하나로 무전 왜곡·CCTV·스캐너가
    /// 한꺼번에 돌아온다. 각자 <see cref="OnCommsBlackoutChanged"/>를 보고 있기 때문이다.
    /// </summary>
    public bool ServerRecover()
    {
        if (IsSpawned && !IsServer)
            return false;

        if (!m_blackout)
            return false;

        Debug.Log("[돌발이벤트] 전자기기 먹통 — 본부 복구 단말로 해제");
        SetBlackout(false);

        // 현장이 "이제 스캐너 되냐"고 묻지 않아도 되게 전원에게 알린다. 매니저는 캐싱하지 않는다 (R8).
        App.Game.SuddenEvent?.Announce(DisplayName, k_recoveredNoticeKey);
        return true;
    }

    public void ServerReset()
    {
        SetBlackout(false);
    }

    // 먹통 상태 설정 — 서버(또는 오프라인)에서만 호출된다.
    // 동기화 변수와 로컬 진실값을 함께 갱신하고, 표현 계층(무전·CCTV)에 이벤트로 알린다.
    private void SetBlackout(bool value)
    {
        if (m_blackout == value)
            return; // 중복 트리거·중복 해제 무시

        m_blackout = value;
        if (IsSpawned && IsServer)
            m_blackoutSynced.Value = value; // OnValueChanged로 원격 클라에 중계
        OnCommsBlackoutChanged?.Invoke(value); // 서버·오프라인 로컬 발행 (원격은 위 동기화 콜백이 담당)
    }
}
