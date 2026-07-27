using System;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 스캐너 아이템. 3초 채널링 후 대상 시민의 스캔 정보를 오너 화면에 출력한다.
/// 배터리 충전식(IChargeable)이며, 스캔 1회당 배터리를 1 소모한다. (GDD 5-1/5-2)
/// 채널링·배터리·충전은 서버 권위(#55). 좌클릭 홀드로 채널링, 뗌·거리 이탈 시 취소된다 (#91).
/// </summary>
public class Scanner : ItemBase, IChargeable
{
    [Header("스캐너 설정")]
    [SerializeField]
    private float m_channelSeconds = 3f;

    [Tooltip("채널링 도중 대상이 이 거리(m)를 벗어나면 스캔 실패로 처리한다 (#91)")]
    [SerializeField]
    private float m_scanKeepRange = 5f;

    [SerializeField]
    private int m_maxBattery = 5;

    // 배터리 잔량 — 아이템 NetworkObject에 실려 전 클라에 동기화되고 줍기/버리기 시 함께 이동한다 (#88).
    // 쓰기는 Server만 — 초기화·소모·충전을 전부 서버가 수행해 클라 조작을 원천 차단한다 (#55).
    // 읽기는 Everyone (본부 UI 포함).
    private readonly NetworkVariable<int> m_currentBattery = new NetworkVariable<int>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // 서버 전용 상태 — 채널링 중복 방지·CTS 관리는 ServerChannel에 위임 (#109).
    private readonly ServerChannel m_channel = new();

    // 오너 UI용 in-flight 플래그 — 서버가 수락하기 전까지 연속 요청을 클라 측에서 억제. (서버 재검증이 최종 판정)
    private bool m_pendingScan;

    // ---- IChargeable ----

    public int CurrentBattery => m_currentBattery.Value;
    public int MaxBattery => m_maxBattery;
    public bool IsFullyCharged => m_currentBattery.Value >= m_maxBattery;
    public bool IsDepleted => m_currentBattery.Value <= 0;

    // 배터리 변화 시 발행 — NetworkVariable.OnValueChanged로 구동되어 전 클라 UI가 갱신된다.
    public event Action<int> OnCharged;

    /// <summary>
    /// 스캔 채널링 성공 이벤트 — 조회된 시민 프로필과 대상 NPC의 NetworkObjectId를 전달한다.
    /// 스캔 결과 프레젠터(#39)가 구독해 "이 플레이어가 스캔한 NPC" 집합에 id를 기록한다 (#233).
    /// 인스턴스 이벤트이므로 구독자는 자기 스캐너의 결과만 받는다 — 스캔 결과는 본인 화면 전용. (GDD 5-4)
    /// 서버가 채널링 완료 후 ScanResultRpc로 오너에게 NPC 참조를 회신하면, 수신 지점에서 발행된다 (#55).
    /// </summary>
    public event Action<CitizenProfile, ulong> OnScanCompleted;

    /// <summary>
    /// 오너 화면 토스트로 띄울 사유 문자열 (#309). ScanResultPresenter(오너 로컬)가 구독.
    /// 범위 이탈 실패·완충 상태 충전 시도에 발행 — 배터리 부족/충전완료 알림은 배터리 값 변화로 presenter가 직접 구동한다.
    /// </summary>
    public event Action<string> OnScanFeedback;

    // ---- IChargeable — 충전 요청 진입점 ----

    /// <summary>
    /// 배터리를 amount만큼 충전한다. 오너·비오너(본부 충전기 #60) 모두 호출 가능.
    /// 실제 충전은 서버에서 처리되며, 결과는 NetworkVariable로 전 클라에 동기화된다 (#55).
    /// </summary>
    public void Charge(int amount)
    {
        if (amount <= 0)
        {
            return;
        }

        // 서버(호스트)·오프라인은 로컬 참조로 즉시 실행
        if (!IsSpawned || IsServer)
        {
            ServerCharge(amount);
            return;
        }

        // 클라 → ServerRpc 경유. InvokePermission = Everyone으로 명시 — 본부 충전기(#60) 등 비오너도 호출 가능.
        RequestChargeRpc(amount);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestChargeRpc(int amount)
    {
        ServerCharge(amount);
    }

    private void ServerCharge(int amount)
    {
        // 스폰 전(오프라인)엔 IsServer 캐시가 아직 갱신되지 않아 false일 수 있으므로,
        // "스폰된 상태에서 서버가 아닐 때"만 차단한다.
        if (IsSpawned && !IsServer)
        {
            return;
        }

        // 스캔 채널링 도중엔 충전 거부 — "충전은 본부에서만·왕복 필요" 리듬 설계를 우회하는 걸 막는다 (#60 리뷰, #109).
        if (m_channel.IsActive)
        {
            NotifyOwner("충전 실패 — 스캔 채널링 중");
            return;
        }

        // 이미 완충이면 값 변화가 없어 OnCharged가 안 울리므로, 여기서 직접 오너 토스트를 띄운다 (#309).
        if (IsFullyCharged)
        {
            NotifyOwner("스캐너 배터리 가득 참", toast: true);
            return;
        }

        m_currentBattery.Value = Mathf.Min(m_currentBattery.Value + amount, m_maxBattery);
    }

    // ---- 전자기기 먹통 게이트 (#372) ----

    // 먹통 이벤트 참조 — 첫 조회 후 캐시한다. 씬이 바뀌어 이벤트가 파괴되면 Unity의 null 판정에
    // 걸려 자동으로 다시 해석된다. 아이템은 씬을 넘어 살아남을 수 있으므로 이 재해석이 필수다.
    private DeviceBlackoutEvent m_blackout;

    /// <summary>
    /// 전자기기 먹통(#106) 중인지 — 스캐너는 먹통 동안 사용할 수 없다. (GDD 6-4)
    /// 판정값은 <see cref="DeviceBlackoutEvent.IsCommsBlackout"/>이 피어별로 갈라준다
    /// (서버·오프라인은 실참조, 원격 클라는 동기화값) — 클라 힌트와 서버 판정이 같은 규칙을 쓴다.
    /// 먹통 이벤트가 없는 구성(테스트 씬·이벤트 항목 off)에서는 null이라 항상 false다.
    /// </summary>
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

    /// <summary>
    /// 스캔 중이 아니고, 배터리가 남아 있고, 먹통이 아닐 때만 사용 가능.
    /// (UI 힌트용 — 최종 판정은 서버가 재검증. CanTarget이 이 값을 보므로 윤곽선·크로스헤어도 함께 꺼진다)
    /// </summary>
    public override bool CanUse() => !m_pendingScan && !IsDepleted && !IsBlackout;

    /// <summary>스캔 가능한 대상인지 — 신원(CitizenIdentity)과 배정된 프로필이 있고 배터리·중복 스캔
    /// 게이트(CanUse)를 통과해야 한다. Use()의 조기 검증과 동일 기준 — 조준 피드백(윤곽선) 판정용. (#184)
    /// 프로필까지 보는 이유(#310 후속): 이벤트 NPC(난동꾼·침입자)는 라운드 시작 배정을 타지 않아 프로필이
    /// 없다 — 신원만 보면 윤곽선은 뜨는데 스캔은 실패하는 어긋남이 생긴다. 프로필은 NetworkVariable로
    /// 동기화되므로(#52) 이 판정은 모든 피어에서 일관된다.</summary>
    public override bool CanTarget(GameObject aimTarget)
    {
        if (!CanUse())
            return false;
        if (aimTarget == null)
            return false;

        CitizenIdentity identity = aimTarget.GetComponentInParent<CitizenIdentity>();
        return identity != null && identity.Profile != null;
    }

    /// <summary>
    /// 아이템 사용 진입점. 오너의 의도를 서버로 전달한다.
    /// 대상 해석(CitizenIdentity/Profile 유무)은 진단 로그·조기 반환을 위해 클라에서 수행.
    /// 실제 채널링·배터리 소모·결과 전파는 서버가 처리한다 (#55).
    /// </summary>
    public override void Use(GameObject target)
    {
        if (!CanUse())
        {
            // 먹통을 먼저 본다 — 배터리도 없고 먹통이기도 하면 "지금 왜 안 되는가"의 답은 먹통이다.
            // 토스트로 띄우는 이유(#309): 아무 반응이 없으면 고장으로 오인한다.
            if (IsBlackout)
            {
                NotifyOwner("스캐너 먹통 — 전자기기 장애", toast: true);
            }
            else if (IsDepleted)
            {
                Debug.Log($"스캐너 배터리 부족! (남은 배터리: {m_currentBattery.Value})");
            }

            return;
        }

        // 겨냥한 대상에서 시민 프로필을 조회한다 (#34). 신원을 확인할 수 없으면 스캔을 시작하지 않는다.
        // 실패 사유를 단계별로 구분해 로그한다 — 클라 프로필 미동기화(#52/#56) 같은 문제의 진단용.
        if (target == null)
        {
            Debug.Log("스캔 실패: 겨냥된 대상 없음");
            return;
        }

        // 콜라이더가 NPC 루트의 자식일 수 있으므로 부모까지 탐색한다 (Handcuffs.FindTarget과 동일 관례).
        CitizenIdentity identity = target.GetComponentInParent<CitizenIdentity>();
        if (identity == null)
        {
            Debug.Log($"스캔 실패: CitizenIdentity 없음 ({target.name})");
            return;
        }

        if (identity.Profile == null)
        {
            Debug.Log($"스캔 실패: 프로필 미배정 — 서버 배정 결과가 이 클라이언트에 동기화되지 않음 ({target.name})");
            return;
        }

        NetworkObject npcNetObj = identity.GetComponentInParent<NetworkObject>();
        if (npcNetObj == null)
        {
            Debug.Log($"스캔 실패: NPC에 NetworkObject 없음 ({target.name})");
            return;
        }

        // 연속 요청 억제 (서버 응답 전까지)
        m_pendingScan = true;

        NetworkObjectReference npcRef = new NetworkObjectReference(npcNetObj);

        // 서버(호스트)·오프라인은 로컬 참조로 즉시 실행
        if (!IsSpawned || IsServer)
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

    [Rpc(SendTo.Server)]
    private void RequestScanRpc(NetworkObjectReference npcRef)
    {
        ServerBeginScan(npcRef);
    }

    // ---- 서버 스캔 채널링 ----

    /// <summary>
    /// 서버에서 스캔 요청을 검증하고 채널링을 시작한다.
    /// 이미 스캔 중이거나 배터리가 없으면 거부 (클라 CanUse는 신뢰 불가이므로 재검증).
    /// </summary>
    private void ServerBeginScan(NetworkObjectReference npcRef)
    {
        // 스폰 전(오프라인)엔 IsServer 캐시가 아직 갱신되지 않아 false일 수 있으므로,
        // "스폰된 상태에서 서버가 아닐 때"만 차단한다.
        if (IsSpawned && !IsServer)
        {
            return;
        }

        if (m_channel.IsActive)
        {
            ClearPendingRpc();
            return;
        }

        if (IsDepleted)
        {
            ClearPendingRpc();
            return;
        }

        // 클라 CanUse는 신뢰할 수 없으므로 먹통도 서버가 재검증한다 — 없으면 위조 RPC로 먹통 중 스캔이 뚫린다 (#372)
        if (IsBlackout)
        {
            NotifyOwner("스캔 실패 — 전자기기 먹통", toast: true);
            ClearPendingRpc();
            return;
        }

        if (!npcRef.TryGet(out NetworkObject npcNetObj) ||
            !npcNetObj.TryGetComponent(out CitizenIdentity identity) ||
            identity.Profile == null)
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
            // 사유는 OutOfRange 하나로 묶여 오지만, 아래에서 먹통 여부로 메시지를 갈라 어긋남을 막는다
            // (공용 ServerChannel.Result에 사유를 늘리면 Escorter·Reviver까지 건드리게 되므로 여기서 해석한다).
            result = await m_channel.RunAsync(
                m_channelSeconds,
                () => identity != null && IsInRange(identity.transform) && !IsBlackout);
        }
        finally
        {
            // 완료·뗌·거리이탈·예외 어떤 경로로 끝나도 게이지 숨김을 보장한다 (#184)
            NotifyChannelGaugeEnd();
        }

        switch (result)
        {
            case ServerChannel.Result.OutOfRange:
                // keepAlive 이탈 사유를 여기서 갈라 준다 (#372) — 먹통으로 끊겼는데 "범위를 벗어남"이 뜨면
                // 플레이어가 원인을 오해한다.
                NotifyOwner(
                    IsBlackout ? "스캔 중단 — 전자기기 먹통" : "스캔 실패 — 대상이 범위를 벗어남",
                    toast: true); // 오너 화면 토스트 (#309)
                ClearPendingRpc(); // 실패로 끝나도 오너의 in-flight 플래그를 풀어야 재시도 가능 (#91)
                return;

            case ServerChannel.Result.Canceled:
                NotifyOwner("스캔 취소됨 (홀드 뗌)");
                ClearPendingRpc(); // 뗌 취소 후에도 오너가 즉시 재시도할 수 있어야 한다 (#91)
                return;

            case ServerChannel.Result.Completed:
                break; // 아래 성공 처리로 진행
        }

        // 서버가 배터리를 소모 — NetworkVariable(Server 쓰기)이라 전 클라에 동기화된다.
        m_currentBattery.Value = Mathf.Max(m_currentBattery.Value - 1, 0);
        Debug.Log($"[서버] NPC 스캔됨: {GetScanInfo(profile)}. 남은 배터리: {m_currentBattery.Value}");

        // 스캔 결과를 오너 클라에만 전달 — GDD 5-4: 스캔 정보는 스캔한 플레이어 화면 전용.
        // 본부/타 클라는 배터리 감소(NetworkVariable)만 전파받는다.
        // NPC NetworkObjectReference를 넘기면 오너 클라가 CitizenIdentity.Profile을 로컬에서 해석 (#55).
        ScanResultRpc(npcRef);
    }

    // 서버 → 오너: 스캔 결과 회신. 오너가 로컬 CitizenIdentity에서 프로필을 추출해 이벤트를 발행한다.
    [Rpc(SendTo.Owner)]
    private void ScanResultRpc(NetworkObjectReference npcRef)
    {
        m_pendingScan = false;

        if (!npcRef.TryGet(out NetworkObject npcNetObj) ||
            !npcNetObj.TryGetComponent(out CitizenIdentity identity) ||
            identity.Profile == null)
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

    // ---- 오너 로그 피드백 ----

    // 판정 로그는 서버에서 찍히므로 원격 클라 오너는 결과를 볼 수 없다 — 오너 콘솔에도 같은 로그를 전달한다.
    // PlayerEscorter.NotifyOwner와 동일 패턴 (#91). 정식 UI 피드백(#65 계열)이 생기면 그 전달 경로로 확장.
    // toast=true면 콘솔 로그에 더해 오너 화면 토스트(OnScanFeedback)도 발행한다 (#309).
    private void NotifyOwner(string message, bool toast = false)
    {
        Debug.Log(message); // 서버(호스트)·오프라인 콘솔
        if (IsSpawned && IsServer && !IsOwner)
        {
            OwnerLogRpc(message, toast); // 원격 클라가 오너면 RPC로 전달 (거기서 로그·토스트)
            return;
        }
        if (toast)
            OnScanFeedback?.Invoke(message); // 호스트 오너·오프라인은 로컬 발행
    }

    [Rpc(SendTo.Owner)]
    private void OwnerLogRpc(string message, bool toast)
    {
        Debug.Log($"[서버 판정] {message}");
        if (toast)
            OnScanFeedback?.Invoke(message);
    }

    // 채널링 게이지 피드백(NotifyChannelGaugeStart/End)은 기반 ChanneledInteractionBehaviour가 제공한다. (#184)
    // 스캐너는 줍기 시 소유권이 홀더로 이전되므로(#88) 기반의 SendTo.Owner가 정확히 든 사람에게 간다.

    // ---- 스캔 취소 ----

    /// <summary>좌클릭 뗌 — 진행 중인 스캔 채널링 취소를 서버에 요청한다 (#91).</summary>
    public override void CancelUse() => CancelScan();

    /// <summary>버리기 등 소유권 이전 경로에서 서버가 직접 스캔 채널을 끊는다 (ItemBase 훅). 서버(또는 오프라인)에서만 호출된다.</summary>
    public override void ServerCancelActiveUse() => m_channel.Cancel();

    /// <summary>진행 중인 스캔 채널링을 취소한다. (이동·피격 등 방해 시 호출)</summary>
    public void CancelScan()
    {
        if (!IsSpawned || IsServer)
        {
            // 서버(호스트)·오프라인은 직접 채널 취소
            m_channel.Cancel();
            return;
        }

        // 원격 클라 → 서버 취소 요청. 단 이미 소유권을 잃은 경우(버리기 직후)엔 RequireOwnership에
        // 막혀 서버가 거부(경고 로그)하므로 보내지 않는다 — 그 경로는 서버가 DropRpc에서
        // ServerCancelActiveUse로 직접 끊는다.
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
        // 기준점은 든 플레이어의 AimOrigin(카메라) — 조준·윤곽선 게이트와 동일 (#184).
        // 아이템은 줍기/버리기로 부모가 바뀌므로 캐시하지 않고 호출 시점에 해석한다 (Handcuffs.Escorter 관례).
        PlayerInteractor interactor = GetComponentInParent<PlayerInteractor>();
        Vector3 origin = interactor != null ? interactor.AimOrigin.position : transform.position;
        return (target.position - origin).sqrMagnitude
            <= m_scanKeepRange * m_scanKeepRange;
    }

    private static string GetScanInfo(CitizenProfile profile)
    {
        if (profile == null)
        {
            return "대상 정보 없음";
        }

        return $"이름={profile.CitizenName}, 타입={profile.m_typeView}, 세력={profile.m_factionView}";
    }

    // ---- 라이프사이클 ----

    public override void OnNetworkSpawn()
    {
        // 배터리 변화를 전 클라가 수신해 UI를 갱신한다 (본부 화면 포함).
        // 초기값 세팅보다 먼저 구독해 서버의 0→최대 변화도 이벤트로 받게 한다.
        m_currentBattery.OnValueChanged += HandleBatteryChanged;

        // 배터리 초기값은 서버가 채운다. 쓰기 권한이 Server이므로 서버만 정상적으로 쓸 수 있다 (#55).
        // 서버가 채우면 전 클라에 복제되고, 뒤늦게 접속한 클라도 스폰 동기화로 현재값을 받는다.
        if (IsServer)
        {
            m_currentBattery.Value = m_maxBattery;
        }
    }

    public override void OnNetworkDespawn()
    {
        m_currentBattery.OnValueChanged -= HandleBatteryChanged;
        // 디스폰(버리기·파괴) 시 서버에서 진행 중인 채널링도 취소
        if (IsServer)
        {
            m_channel.Cancel();
        }

        m_pendingScan = false;
    }

    private void HandleBatteryChanged(int previous, int current) => OnCharged?.Invoke(current);

    private void OnDisable()
    {
        // 장착 해제·비활성 시 채널링 취소 요청
        CancelScan();
        m_pendingScan = false;
    }

    public override void OnDestroy()
    {
        m_channel.Dispose();
        base.OnDestroy();
    }
}
