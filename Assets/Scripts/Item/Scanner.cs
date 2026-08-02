using System;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>스캐너 아이템. 3초 채널링 후 대상 시민의 스캔 정보를 오너 화면에 출력한다.
/// 배터리는 옆에 붙은 <see cref="ItemBattery"/>가 들고 있고, 스캔 1회당 1 소모한다. (GDD 5-1/5-2)
/// 채널링·배터리·충전은 서버 권위(#55). 좌클릭 홀드로 채널링, 뗌·거리 이탈 시 취소된다 (#91).</summary>
[RequireComponent(typeof(ItemBattery))]
public class Scanner : ItemBase
{
    [Header("스캐너 설정")]
    [SerializeField] private float m_channelSeconds = 3f;

    [Tooltip("채널링 도중 대상이 이 거리(m)를 벗어나면 스캔 실패로 처리한다 (#91)")]
    [SerializeField] private float m_scanKeepRange = 5f;

    [Tooltip("스캔 진행률이 이 지점(0~1)을 넘으면 대상이 반응한다 (#400). 도주형은 여기서 달아나기 시작하므로, 남은 구간 동안 따라붙어야 스캔이 완료된다")]
    [Range(0f, 1f)]
    [SerializeField] private float m_reactionPoint = 0.5f;

    // 서버 전용 상태 — 채널링 중복 방지·CTS 관리는 ServerChannel에 위임 (#109).
    private readonly ServerChannel m_channel = new();

    // 오너 UI용 in-flight 플래그 — 서버가 수락하기 전까지 연속 요청을 클라 측에서 억제. (서버 재검증이 최종 판정)
    private bool m_pendingScan;

    // 같은 오브젝트의 배터리 — RequireComponent라 반드시 있다.
    private ItemBattery m_battery;

    /// <summary>스캔 채널링 성공 이벤트 — 조회된 시민 프로필과 대상 NPC의 NetworkObjectId를 전달한다.
    /// 스캔 결과 프레젠터(#39)가 구독해 "이 플레이어가 스캔한 NPC" 집합에 id를 기록한다 (#233).
    /// 인스턴스 이벤트이므로 구독자는 자기 스캐너의 결과만 받는다 — 스캔 결과는 본인 화면 전용. (GDD 5-4)</summary>
    public event Action<CitizenProfile, ulong> OnScanCompleted;

    /// <summary>오너 화면 토스트로 띄울 사유 문자열 (#309). ScanResultPresenter(오너 로컬)가 구독.
    /// 범위 이탈 실패·완충 상태 충전 시도에 발행 — 배터리 부족/충전완료 알림은 배터리 값 변화로 presenter가 직접 구동한다.</summary>
    public event Action<string> OnScanFeedback;

    /// <summary>기반 NotifyOwner(toast:true)가 오너 로컬에서 부르는 발행 지점. (#309)</summary>
    // 스캐너는 줍기 시 소유권이 홀더로 이전되므로(#88) 기반의 SendTo.Owner가 정확히 든 사람에게 간다.
    protected override void RaiseOwnerToast(string message) => OnScanFeedback?.Invoke(message);

    private void Awake()
    {
        m_battery = GetComponent<ItemBattery>();

        m_battery.CanCharge = () => !m_channel.IsActive;
        m_battery.ChargeBlockedReason = "충전 실패 — 스캔 채널링 중";
        m_battery.FullyChargedMessage = "스캐너 배터리 가득 참";
        m_battery.OnChargeToast += RaiseOwnerToast; // 배터리 토스트를 스캐너 토스트 채널로 중계
    }

    // ---- 전자기기 먹통 게이트 (#372) ----
    // 먹통 이벤트 참조 — 첫 조회 후 캐시한다. 씬이 바뀌어 이벤트가 파괴되면 Unity의 null 판정에
    // 걸려 자동으로 다시 해석된다. 아이템은 씬을 넘어 살아남을 수 있으므로 이 재해석이 필수다.
    private DeviceBlackoutEvent m_blackout;

    /// <summary>전자기기 먹통(#106) 중인지 — 스캐너는 먹통 동안 사용할 수 없다. (GDD 6-4)
    /// 판정값은 <see cref="DeviceBlackoutEvent.IsCommsBlackout"/>이 피어별로 갈라주므로
    /// 클라 힌트와 서버 판정이 같은 규칙을 쓴다. 먹통 이벤트가 없는 구성에서는 null이라 항상 false.</summary>
    private bool IsBlackout
    {
        get
        {
            if (m_blackout == null)
                m_blackout = App.Game.SuddenEvent?.GetEvent<DeviceBlackoutEvent>();
            return m_blackout != null && m_blackout.IsCommsBlackout;
        }
    }

    // ---- ItemBase — 사용 요청 진입점 ----
    /// <summary>스캔 중이 아니고, 배터리가 남아 있고, 먹통이 아닐 때만 사용 가능.
    /// (UI 힌트용 — 최종 판정은 서버가 재검증. CanTarget이 이 값을 보므로 윤곽선·크로스헤어도 함께 꺼진다)</summary>
    public override bool CanUse() => !m_pendingScan && !m_battery.IsDepleted && !IsBlackout;

    /// <summary>스캔 가능한 대상인지 — 신원(CitizenIdentity)과 배정된 프로필이 있어야 한다. (#184)
    /// 프로필까지 보는 이유(#310 후속): 이벤트 NPC(난동꾼·침입자)는 라운드 시작 배정을 타지 않아 프로필이
    /// 없다 — 신원만 보면 윤곽선은 뜨는데 스캔은 실패하는 어긋남이 생긴다. 프로필은 NetworkVariable로</summary>
    public override bool CanTarget(GameObject aimTarget)
    {
        if (!CanUse())
            return false;
        if (aimTarget == null)
            return false;

        CitizenIdentity identity = aimTarget.GetComponentInParent<CitizenIdentity>();
        return identity != null && identity.Profile != null;
    }

    /// <summary>아이템 사용 진입점. 오너의 의도를 서버로 전달한다.
    /// 대상 해석은 진단 로그·조기 반환을 위해 클라에서 수행하고,
    /// 실제 채널링·배터리 소모·결과 전파는 서버가 처리한다 (#55).</summary>
    public override void Use(GameObject target)
    {
        if (!CanUse())
        {
            if (IsBlackout)
                NotifyOwner("스캐너 먹통 — 전자기기 장애", toast: true);
            else if (m_battery.IsDepleted)
                Debug.Log($"스캐너 배터리 부족! (남은 배터리: {m_battery.CurrentBattery})");

            return;
        }

        if (!TryResolveScanTarget(target, out NetworkObjectReference npcRef))
            return;

        m_pendingScan = true;

        if (HasServerAuthority)
        {
            ServerBeginScan(npcRef);
            return;
        }

        if (!IsOwner)
        {
            m_pendingScan = false;
            return;
        }

        RequestScanRpc(npcRef);
    }

    // 겨냥한 대상에서 스캔할 NPC를 찾는다 (#34). 실패 사유를 단계별로 구분해 로그한다.
    private static bool TryResolveScanTarget(GameObject target, out NetworkObjectReference npcRef)
    {
        npcRef = default;

        if (target == null)
        {
            Debug.Log("스캔 실패: 겨냥된 대상 없음");
            return false;
        }

        // 콜라이더가 NPC 루트의 자식일 수 있으므로 부모까지 탐색한다. (Handcuffs.FindTarget과 동일 관례)
        CitizenIdentity identity = target.GetComponentInParent<CitizenIdentity>();
        if (identity == null)
        {
            Debug.Log($"스캔 실패: CitizenIdentity 없음 ({target.name})");
            return false;
        }

        if (identity.Profile == null)
        {
            Debug.Log($"스캔 실패: 프로필 미배정 — 서버 배정 결과가 이 클라이언트에 동기화되지 않음 ({target.name})");
            return false;
        }

        NetworkObject npcNetObj = identity.GetComponentInParent<NetworkObject>();
        if (npcNetObj == null)
        {
            Debug.Log($"스캔 실패: NPC에 NetworkObject 없음 ({target.name})");
            return false;
        }

        npcRef = new NetworkObjectReference(npcNetObj);
        return true;
    }

    // 참조 해석 + 프로필 유무를 한 번에 — 서버 검증과 오너 결과 수신이 같은 기준을 쓰게 한다.
    private static bool TryResolveCitizen(
        NetworkObjectReference npcRef, out NetworkObject npcNetObj, out CitizenIdentity identity)
    {
        identity = null;
        return npcRef.TryGet(out npcNetObj)
            && npcNetObj.TryGetComponent(out identity)
            && identity.Profile != null;
    }

    [Rpc(SendTo.Server)]
    private void RequestScanRpc(NetworkObjectReference npcRef)
    {
        ServerBeginScan(npcRef);
    }

    // ---- 서버 스캔 채널링 ----
    /// <summary>서버에서 스캔 요청을 검증하고 채널링을 시작한다.
    /// 클라 CanUse는 신뢰할 수 없으므로 스캔 중복·배터리·먹통·대상을 전부 재검증한다.</summary>
    private void ServerBeginScan(NetworkObjectReference npcRef)
    {
        if (!HasServerAuthority)
            return;

        if (m_channel.IsActive || m_battery.IsDepleted)
        {
            ClearPendingRpc();
            return;
        }

        // 먹통을 서버가 다시 보지 않으면 위조 RPC로 먹통 중 스캔이 뚫린다 (#372)
        if (IsBlackout)
        {
            NotifyOwner("스캔 실패 — 전자기기 먹통", toast: true);
            ClearPendingRpc();
            return;
        }

        if (!TryResolveCitizen(npcRef, out _, out CitizenIdentity identity))
        {
            Debug.Log("스캔 실패(서버): NPC 참조 해석 실패 또는 프로필 없음");
            ClearPendingRpc();
            return;
        }

        ServerScanAsync(npcRef, identity).Forget();
    }

    // 거리 이탈 판정에 대상 위치가 필요해 프로필이 아닌 신원 컴포넌트째 받는다 (#91)
    private async UniTaskVoid ServerScanAsync(NetworkObjectReference npcRef, CitizenIdentity identity)
    {
        // 프로필은 시작 시점 값으로 고정 — 채널링 도중 재배정될 일은 없다
        CitizenProfile profile = identity.Profile;

        NotifyOwner($"스캔 채널링 시작: {identity.name} ({m_channelSeconds}초)");
        NotifyChannelGaugeStart(m_channelSeconds);

        ServerChannel.Result result;
        try
        {
            // 먹통을 keepAlive에 포함한다 — 없으면 먹통 직전에 시작한 스캔이 먹통 한복판에서 성공한다 (#372).
            // 스캔 중간에 대상이 반응한다 (#400). 시작이면 도주형은 사거리 이탈로 영영 스캔되지 않고,
            // 완료 후면 대가 없이 정보를 얻는다 — 중간이라야 남은 구간을 따라붙어야 정보가 나온다.
            result = await m_channel.RunAsync(
                m_channelSeconds,
                () => identity != null && IsInRange(identity.transform) && !IsBlackout,
                m_reactionPoint,
                () => ServerTriggerScanReaction(identity));
        }
        finally
        {
            // 완료·뗌·거리이탈·예외 어떤 경로로 끝나도 게이지 숨김을 보장한다 (#184)
            NotifyChannelGaugeEnd();
        }

        // 실패로 끝나도 오너의 in-flight 플래그를 풀어야 재시도가 된다 (#91)
        switch (result)
        {
            case ServerChannel.Result.OutOfRange:
                // 공용 ServerChannel.Result는 이탈 사유를 하나로 묶어 주므로, 먹통 여부를 여기서 갈라
                // "먹통으로 끊겼는데 범위 이탈로 표시되는" 어긋남을 막는다 (#372).
                // (Result에 사유를 늘리면 Escorter·Reviver까지 건드리게 되므로 해석은 이쪽 몫이다)
                NotifyOwner(
                    IsBlackout ? "스캔 중단 — 전자기기 먹통" : "스캔 실패 — 대상이 범위를 벗어남",
                    toast: true);
                ClearPendingRpc();
                return;

            case ServerChannel.Result.Canceled:
                NotifyOwner("스캔 취소됨 (홀드 뗌)");
                ClearPendingRpc();
                return;
        }

        m_battery.ServerConsume();
        Debug.Log($"[서버] NPC 스캔됨: {GetScanInfo(profile)}. 남은 배터리: {m_battery.CurrentBattery}");

        // 스캔 결과는 오너 클라에만 — GDD 5-4: 스캔 정보는 스캔한 플레이어 화면 전용.
        // 본부/타 클라는 배터리 감소(NetworkVariable)만 전파받는다. 참조를 넘기면 오너가 로컬에서 프로필을 해석한다 (#55).
        ScanResultRpc(npcRef);
    }

    // 스캔 중간 지점 — 대상을 반응시킨다. RunAsync가 서버 경로라 서버(또는 오프라인)에서만 불린다. (#400)
    private void ServerTriggerScanReaction(CitizenIdentity identity)
    {
        if (identity == null)
            return;

        NpcController npc = identity.GetComponent<NpcController>();
        if (npc == null)
            return;

        // 스캐너는 줍기·버리기로 부모가 바뀌므로 호출 시점에 해석한다 (IsInRange와 같은 관례)
        PlayerInteractor interactor = GetComponentInParent<PlayerInteractor>();
        npc.ServerReactTo(ReactionTrigger.Scan, interactor != null ? interactor.transform : null);
    }

    // 서버 → 오너: 스캔 결과 회신. 오너가 로컬 CitizenIdentity에서 프로필을 추출해 이벤트를 발행한다.
    [Rpc(SendTo.Owner)]
    private void ScanResultRpc(NetworkObjectReference npcRef)
    {
        m_pendingScan = false;

        if (!TryResolveCitizen(npcRef, out NetworkObject npcNetObj, out CitizenIdentity identity))
        {
            Debug.LogWarning("ScanResultRpc: NPC 참조 해석 실패 또는 프로필 없음 (클라 동기화 지연?)");
            return;
        }

        Debug.Log($"스캔 결과 수신: {GetScanInfo(identity.Profile)}");
        OnScanCompleted?.Invoke(identity.Profile, npcNetObj.NetworkObjectId);
    }

    // 서버 → 오너: 스캔 거부·취소·실패 시 in-flight 플래그 해제
    [Rpc(SendTo.Owner)]
    private void ClearPendingRpc()
    {
        m_pendingScan = false;
    }

    // ---- 스캔 취소 ----
    /// <summary>좌클릭 뗌 — 진행 중인 스캔 채널링 취소를 서버에 요청한다 (#91).</summary>
    public override void CancelUse() => CancelScan();

    /// <summary>버리기 등 소유권 이전 경로에서 서버가 직접 스캔 채널을 끊는다 (ItemBase 훅).</summary>
    public override void ServerCancelActiveUse() => m_channel.Cancel();

    /// <summary>진행 중인 스캔 채널링을 취소한다. (이동·피격 등 방해 시 호출)</summary>
    public void CancelScan()
    {
        if (HasServerAuthority)
        {
            m_channel.Cancel();
            return;
        }

        // 원격 클라 → 서버 취소 요청. 단 이미 소유권을 잃은 경우(버리기 직후)엔 RequireOwnership에
        // 막혀 서버가 거부(경고 로그)하므로 보내지 않는다 — 그 경로는 서버가 DropRpc에서 ServerCancelActiveUse로 직접 끊는다.
        if (IsOwner)
            RequestCancelScanRpc();
    }

    [Rpc(SendTo.Server)]
    private void RequestCancelScanRpc()
    {
        m_channel.Cancel();
    }

    private bool IsInRange(Transform target)
    {
        PlayerInteractor interactor = GetComponentInParent<PlayerInteractor>(); // 아이템은 주인이 바뀔 수 있으므로 주인을 캐시하지 않고 호출 시점에 해석한다. (Handcuffs.Escorter 관례).
        Vector3 origin = interactor != null ? interactor.AimOrigin.position : transform.position;

        return (target.position - origin).sqrMagnitude <= m_scanKeepRange * m_scanKeepRange
            && (interactor == null || interactor.HasLineOfSightTo(target));
    }

    private static string GetScanInfo(CitizenProfile profile)
    {
        if (profile == null)
            return "대상 정보 없음";

        return $"이름={profile.CitizenName}, 타입={profile.m_typeView}, 세력={profile.m_factionView}";
    }

    // ---- 라이프사이클 ----
    public override void OnNetworkDespawn()
    {
        if (IsServer)
            m_channel.Cancel();

        m_pendingScan = false;
    }

    private void OnDisable()
    {
        CancelScan();
        m_pendingScan = false;
    }

    public override void OnDestroy()
    {
        m_channel.Dispose();
        base.OnDestroy();
    }
}
