using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 스캐너 아이템. 3초 채널링 후 대상 시민의 스캔 정보를 오너 화면에 출력한다.
/// 배터리 충전식(IChargeable)이며, 스캔 1회당 배터리를 1 소모한다. (GDD 5-1/5-2)
/// </summary>
public class Scanner : ItemBase, IChargeable
{
    [Header("스캐너 설정")]
    [SerializeField]
    private float m_channelSeconds = 3f;

    [SerializeField]
    private int m_maxBattery = 5;

    // 배터리 잔량 — 아이템 NetworkObject에 실려 전 클라에 동기화되고 줍기/버리기 시 함께 이동한다 (#88).
    // 쓰기는 Server만 — 초기화·소모·충전을 전부 서버가 수행해 클라 조작을 원천 차단한다 (#55).
    // 읽기는 Everyone (본부 UI 포함).
    private readonly NetworkVariable<int> m_currentBattery = new NetworkVariable<int>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // 서버 전용 상태 — 채널링 중복 방지 및 CTS 관리.
    private bool m_isScanning;
    private CancellationTokenSource m_cts;

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
    /// 스캔 채널링 성공 이벤트 — 조회된 시민 프로필을 전달한다. 스캔 결과 프레젠터(#39)가 구독한다.
    /// 인스턴스 이벤트이므로 구독자는 자기 스캐너의 결과만 받는다 — 스캔 결과는 본인 화면 전용. (GDD 5-4)
    /// 서버가 채널링 완료 후 ScanResultRpc로 오너에게 NPC 참조를 회신하면, 수신 지점에서 발행된다 (#55).
    /// </summary>
    public event Action<CitizenProfile> OnScanCompleted;

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

        m_currentBattery.Value = Mathf.Min(m_currentBattery.Value + amount, m_maxBattery);
    }

    // ---- ItemBase — 사용 요청 진입점 ----

    /// <summary>스캔 중이 아니고 배터리가 남아 있을 때만 사용 가능. (UI 힌트용 — 최종 판정은 서버가 재검증)</summary>
    public override bool CanUse() => !m_pendingScan && !IsDepleted;

    /// <summary>
    /// 아이템 사용 진입점. 오너의 의도를 서버로 전달한다.
    /// 대상 해석(CitizenIdentity/Profile 유무)은 진단 로그·조기 반환을 위해 클라에서 수행.
    /// 실제 채널링·배터리 소모·결과 전파는 서버가 처리한다 (#55).
    /// </summary>
    public override void Use(GameObject target)
    {
        if (!CanUse())
        {
            if (IsDepleted)
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

        if (m_isScanning)
        {
            ClearPendingRpc();
            return;
        }

        if (IsDepleted)
        {
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

        ServerScanAsync(npcRef, identity.Profile).Forget();
    }

    private async UniTaskVoid ServerScanAsync(NetworkObjectReference npcRef, CitizenProfile profile)
    {
        m_isScanning = true;
        m_cts = new CancellationTokenSource();

        try
        {
            await UniTask.Delay(TimeSpan.FromSeconds(m_channelSeconds), cancellationToken: m_cts.Token);

            // 서버가 배터리를 소모 — NetworkVariable(Server 쓰기)이라 전 클라에 동기화된다.
            m_currentBattery.Value = Mathf.Max(m_currentBattery.Value - 1, 0);
            Debug.Log($"[서버] NPC 스캔됨: {GetScanInfo(profile)}. 남은 배터리: {m_currentBattery.Value}");

            // 스캔 결과를 오너 클라에만 전달 — GDD 5-4: 스캔 정보는 스캔한 플레이어 화면 전용.
            // 본부/타 클라는 배터리 감소(NetworkVariable)만 전파받는다.
            // NPC NetworkObjectReference를 넘기면 오너 클라가 CitizenIdentity.Profile을 로컬에서 해석 (#55).
            ScanResultRpc(npcRef);
        }
        catch (OperationCanceledException)
        {
            Debug.Log("[서버] 스캔 취소됨");
        }
        finally
        {
            m_isScanning = false;
            m_cts?.Dispose();
            m_cts = null;
        }
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
        OnScanCompleted?.Invoke(identity.Profile);
    }

    // 서버 → 오너: 스캔 거부 시 in-flight 플래그 해제
    [Rpc(SendTo.Owner)]
    private void ClearPendingRpc()
    {
        m_pendingScan = false;
    }

    // ---- 스캔 취소 ----

    /// <summary>진행 중인 스캔 채널링을 취소한다. (이동·피격 등 방해 시 호출)</summary>
    public void CancelScan()
    {
        if (!IsSpawned || IsServer)
        {
            // 서버(호스트)·오프라인은 직접 CTS 취소
            m_cts?.Cancel();
            return;
        }

        // 클라 → 서버에 취소 요청
        RequestCancelScanRpc();
    }

    [Rpc(SendTo.Server)]
    private void RequestCancelScanRpc()
    {
        m_cts?.Cancel();
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
            m_cts?.Cancel();
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
        m_cts?.Cancel();
        m_cts?.Dispose();
        base.OnDestroy();
    }
}
