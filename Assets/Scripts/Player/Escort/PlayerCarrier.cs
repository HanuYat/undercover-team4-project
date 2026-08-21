using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 기능 정지(Die)된 동료를 끌고 가는 운반 — 서버 권위 허브. (#365, GDD 7-5)
/// 한 컴포넌트가 <b>두 역할</b>을 든다: 남을 끄는 쪽(<see cref="CarriedTarget"/>)과 끌려가는 쪽
/// (<see cref="IsBeingCarried"/>). 양쪽 다 플레이어라 같은 프리팹에 붙고, 역할을 두 컴포넌트로
/// 쪼개면 "끌면서 동시에 끌려가는" 조합을 두 곳에서 막아야 한다.
///
/// 요청·검증·상태는 서버가 갖되(PlayerEscorter 관례), <b>이동은 끌려가는 쪽 오너가 한다</b> —
/// 플레이어 위치는 NetworkTransform 오너 권한이라 서버도 운반자도 남의 몸을 직접 못 옮긴다.
/// 그래서 서버가 대상 오너에게 RPC로 추종 지시를 내리고, 오너의 PlayerTowedMotion이
/// <see cref="PlayerTowedMotion.BeginDraggedFollow"/>로 따라간다. (오검거 호송 #279와 같은 구조)
///
/// 복구는 운반이 아니라 본부 부활 장치(<see cref="HqRevivalDevice"/>)가 한다 — 여기는 옮기기만 한다.
///
/// <b>끄는 쪽은 1인당 몸 1구, 끌리는 쪽은 여럿이 덧걸 수 있다</b> (합류 — NPC 밧줄 #390/#398과 같은
/// 규칙). 그래서 <see cref="CarriedTarget"/>는 단일 참조지만 <see cref="m_carriedBy"/>는 목록이다.
/// </summary>
public class PlayerCarrier : NetworkBehaviour
{
    [Header("운반 (서버 권위)")]
    [Tooltip(
        "이 거리(m)를 넘게 벌어지면 놓친다 — 추종 실패의 안전장치다. 정상 이동으로는 닿지 않는 값으로 "
        + "둘 것(전력 질주 추종 지연은 3.5m 안쪽). 몸이 문틀·기둥에 끼거나 끌려가는 쪽이 추종을 "
        + "못 할 때, 끊어주지 않으면 운반자는 끌고 있다고 믿는데 몸만 뒤에 남는다"
    )]
    [SerializeField]
    private float m_breakDistance = 8f;

    // 사거리는 조준·윤곽선과 같은 기준 — PlayerInteractor.Range 재사용 (#147 패턴, #184)
    private const float k_fallbackRange = 3f; // 테스트 구성 등 PlayerInteractor가 없을 때

    // 순간이동 뒤 거리 검사를 미루는 유예(초) — 오너 권한 이동이 도착할 시간을 준다. 서버(또는 오프라인). (#614)
    private const float k_teleportGraceSeconds = 1f;
    private float m_teleportGraceRemaining;

    // 목줄 발동 인원 — PlayerEscorter.k_leashDraggerCount와 같은 값·같은 규칙. 둘 이상이 함께 끌면
    // 거리 이탈로 끊지 않는다(반대로 당기면 8m를 넘는 것이 정상이다).
    private const int k_leashCarrierCount = 2;

    private PlayerInteractor m_interactor;
    private PlayerIncapacitation m_incapacitation;
    private PlayerTowedMotion m_towed;
    private PlayerEscorter m_escorter; // 밧줄 끌기와 동시에 못 하게 막는 게이트. 없을 수 있다(테스트 구성)
    private PlayerLoadout m_loadout; // 밧줄 소지 검증 — 위조 RPC 방어 (#369 관례)

    /// <summary>지금 내가 끌고 가는 동료. 없으면 null. 서버(또는 오프라인)에서만 유효.</summary>
    public PlayerCarrier CarriedTarget { get; private set; }

    // 끌고 있는 대상 — 단순 bool이 아니라 참조인 이유는 밧줄 표시(RopeDragView)가 선의 양 끝점을
    // 알아야 하기 때문이다. CarriedTarget은 서버에만 있어 원격 피어는 누구와 이어졌는지 알 수 없다.
    // (PlayerEscorter.m_tetheredNpcSynced와 같은 사정)
    private readonly NetworkVariable<NetworkObjectReference> m_carriedSynced = new();

    // 나를 끄는 인원 수의 클라 사본 — bool이 아니라 수인 이유는 오너가 목줄 반경(끊김거리 ÷ 인원)을
    // 계산해야 해서다(NpcRopeDrag.m_draggerCountSynced와 같은 사정). 0이면 아무도 안 끈다.
    private readonly NetworkVariable<byte> m_carrierCountSynced = new NetworkVariable<byte>();

    /// <summary>운반 중 여부(끄는 쪽). 서버·오프라인은 실참조, 원격 피어는 동기화값. (PlayerEscorter.IsDraggingNpc 관례)</summary>
    public bool IsCarrying =>
        IsSpawned && !IsServer
            ? m_carriedSynced.Value.NetworkObjectId != 0
            : CarriedTarget != null;

    /// <summary>
    /// 끌고 가는 동료의 트랜스폼 — 전 피어에서 유효한 표현 계층용 접근자(밧줄 선). 없으면 null.
    /// (PlayerEscorter.GetTetheredNpc과 동일 구조 — 세션 종료 중 NetworkManager 소멸 가드 포함)
    /// </summary>
    public Transform CarriedTransform
    {
        get
        {
            if (CarriedTarget != null)
                return CarriedTarget.transform;
            if (!IsSpawned)
                return null;

            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null || !manager.IsListening)
                return null;

            return m_carriedSynced.Value.TryGet(out NetworkObject targetObject, manager)
                ? targetObject.transform
                : null;
        }
    }

    /// <summary>
    /// 끌고 가는 동료의 운반 허브 — <b>전 피어에서 유효한</b> 접근자. 없으면 null.
    /// (<see cref="CarriedTransform"/>의 컴포넌트판 — 같은 동기화 참조를 푼다)
    ///
    /// ⚠ <b>오너가 읽어야 하는 값은 이쪽이다.</b> <see cref="CarriedTarget"/>는 서버 전용이라 원격
    /// 클라에서 항상 null이고, 목줄 제한(<see cref="RopeDragLoad.ConstrainByTautRopes"/>)은 오너가
    /// 로컬로 돈다 — 그쪽이 서버 참조를 보면 <b>호스트에서만</b> 목줄이 걸려 원격 클라는 줄을 무한정
    /// 늘이며 걸어간다. (플레이 테스트 확인)
    /// </summary>
    public PlayerCarrier CarriedBody
    {
        get
        {
            if (CarriedTarget != null)
                return CarriedTarget;

            Transform carried = CarriedTransform;
            return carried != null && carried.TryGetComponent(out PlayerCarrier body) ? body : null;
        }
    }

    /// <summary>누군가에게 끌려가는 중인지(끌려가는 쪽) — <see cref="CarrierCount"/>가 1 이상이면 참이다.</summary>
    public bool IsBeingCarried => CarrierCount > 0;

    /// <summary>지금 나를 끄는 인원 수 — 목줄 반경(끊김거리 ÷ 인원)을 오너가 계산해야 해서 공개한다.
    /// 서버·오프라인은 실목록, 원격 피어는 동기화값. (합류 — NPC 밧줄 #390/#398과 같은 규칙)</summary>
    public int CarrierCount =>
        IsSpawned && !IsServer ? m_carrierCountSynced.Value : m_carriedBy.Count;

    // 나를 끌고 있는 플레이어들 — 서버(또는 오프라인) 진실. 여럿이 덧걸 수 있다(합류).
    private readonly List<PlayerCarrier> m_carriedBy = new List<PlayerCarrier>();

    /// <summary>
    /// 이 플레이어가 지금 운반 대상이 될 수 있는가 — 기능 정지(Die) + 몸이 회수 가능함. (#364/#365)
    /// 부활 판정(IsRevivable)이 아니라 IsBodyLost를 직접 본다 — 운반은 부활 여부와 별개로,
    /// 몸이 맨홀 아래로 사라졌으면(#775) 애초에 회수할 몸이 없다는 뜻이다.
    ///
    /// <b>이미 끌려가는 중이어도 참이다</b> — "합류는 허용, 탈취는 차단"이 여기서도 성립한다
    /// (NPC 밧줄 #390과 같은 원칙). 남의 줄을 끊는 조작이 없으니 탈취 자체가 없다.
    /// </summary>
    public bool CanBeCarried =>
        m_incapacitation != null
        && m_incapacitation.IsDead
        && !m_incapacitation.IsBodyLost;

    private void Awake()
    {
        m_interactor = GetComponent<PlayerInteractor>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_towed = GetComponent<PlayerTowedMotion>();
        m_escorter = GetComponent<PlayerEscorter>();
        m_loadout = GetComponent<PlayerLoadout>();
    }

    // 밧줄을 들고 있는가 — 로드아웃이 없으면(테스트 구성) 통과. 에스코터가 있으면 아래 용량 게이트가
    // 이걸 포함하지만(밧줄 0개면 곧 용량 초과), 에스코터가 없는 구성에서는 이쪽만 남는다.
    private bool HasRope => m_loadout == null || m_loadout.HasRope;

    // ---- 오너 클라 진입점 (상호작용이 호출) ----

    /// <summary>운반 시작 요청 — 오너가 호출(E, IncapacitatedPlayerInteractable). (#365)</summary>
    public void RequestCarry(PlayerCarrier target)
    {
        if (target == null)
            return;

        // 서버(호스트 포함)·오프라인은 로컬 참조로 바로 실행 (PlayerEscorter.RequestRopeDrag 관례)
        if (!IsSpawned || IsServer)
        {
            ServerBeginCarry(target);
            return;
        }
        if (!IsOwner)
            return; // 남의 플레이어 오브젝트에서 온 호출 방지
        if (target.NetworkObject == null || !target.NetworkObject.IsSpawned)
        {
            Debug.LogWarning($"운반 요청 무시 — 대상이 네트워크 스폰되지 않음: {target.name}", this);
            return;
        }

        CarryRequestRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>부활 장치에 안치 요청 — 오너가 호출(운반 중 장치를 겨냥한 E). (#365)</summary>
    public void RequestPlaceInDevice(HqRevivalDevice device)
    {
        if (device == null)
            return;

        if (!IsSpawned || IsServer)
        {
            ServerPlaceInDevice(device);
            return;
        }
        if (!IsOwner)
            return;
        if (device.NetworkObject == null || !device.NetworkObject.IsSpawned)
        {
            Debug.LogWarning($"안치 요청 무시 — 장치가 네트워크 스폰되지 않음: {device.name}", this);
            return;
        }

        PlaceRequestRpc(new NetworkObjectReference(device.NetworkObject));
    }

    /// <summary>내려놓기 요청 — 오너가 호출(운반 중 E). (#365)</summary>
    public void RequestDrop()
    {
        if (!IsSpawned || IsServer)
        {
            ServerDrop("내려놓음");
            return;
        }
        if (!IsOwner)
            return;

        DropRequestRpc();
    }

    // ---- 서버 RPC (오너 → 서버) ----

    [Rpc(SendTo.Server)]
    private void CarryRequestRpc(NetworkObjectReference targetRef)
    {
        if (
            targetRef.TryGet(out NetworkObject targetObj)
            && targetObj.TryGetComponent(out PlayerCarrier target)
        )
        {
            ServerBeginCarry(target);
        }
    }

    [Rpc(SendTo.Server)]
    private void DropRequestRpc() => ServerDrop("내려놓음");

    [Rpc(SendTo.Server)]
    private void PlaceRequestRpc(NetworkObjectReference deviceRef)
    {
        if (
            deviceRef.TryGet(out NetworkObject deviceObj)
            && deviceObj.TryGetComponent(out HqRevivalDevice device)
        )
        {
            ServerPlaceInDevice(device);
        }
    }

    // 안치 실행 — 판정은 장치가 갖는다(자리 하나·상태·거리). 실패하면 계속 끌고 있는 상태로 남는다.
    private void ServerPlaceInDevice(HqRevivalDevice device)
    {
        if (IsSpawned && !IsServer)
            return;
        if (CarriedTarget == null)
            return;

        if (!device.ServerPlace(this))
            NotifyOwner("안치 실패 — 장치가 사용 중이거나 너무 멀다");
    }

    // ---- 서버 실행 (권위) ----

    // 위조 RPC 방어를 겸한 진입 검증 — 클라 조기검증(IncapacitatedPlayerInteractable.CanInteract)과 같은 기준.
    private void ServerBeginCarry(PlayerCarrier target)
    {
        if (target == null || target == this)
            return; // 자기 자신은 못 든다
        if (CarriedTarget != null)
            return; // 한 번에 1명
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
            return; // 쓰러진 사람이 남을 옮길 수는 없다
        if (!HasRope)
            return; // 끌기 수단은 밧줄이다 — 들고 있어야 한다 (#369와 같은 자원 게이트)
        if (m_escorter != null && m_escorter.IsAtRopeCapacity)
            return; // 운반도 밧줄 한 개를 쓴다 — 소지한 줄을 NPC에 전부 걸어 뒀으면 하나를 먼저 풀어야 한다 (#390)
        if (!target.CanBeCarried)
            return; // Die 상태 + 임자 없음일 때만. 다운(구조 가능)은 운반이 아니라 구조 대상이다
        if (!IsInRange(target))
            return;

        CarriedTarget = target;
        target.ServerAddCarrier(this);
        SetCarriedRef(target);

        // 동료를 묶는 것도 같은 밧줄이라 같은 소리다 (#549 · NPC 쪽은 ServerApplyRopeDrag).
        App.Game.Fx?.PlayEverywhere(EFx.RopeBind, target.transform.position);

        Debug.Log($"[운반] 시작 — {name} → {target.name}");
        NotifyOwner($"밧줄로 묶어 끌기 시작: {target.name} (E로 내려놓기)");
    }

    /// <summary>
    /// 이 운반을 잠시 거리 검사에서 빼 둔다 — <b>운반자와 몸을 함께 순간이동시키는 쪽</b>이 옮기기
    /// 직전에 부른다(감옥 문). 서버(또는 오프라인) 전용. 근거는 <see cref="Update"/>의 유예 주석. (#614)
    /// </summary>
    internal void ServerBeginTeleportGrace()
    {
        if (IsSpawned && !IsServer)
            return;

        m_teleportGraceRemaining = k_teleportGraceSeconds;
    }

    /// <summary>
    /// 운반 해제 — 서버(또는 오프라인) 전용. 내려놓기·거리 이탈·부활·운반자 무력화가 모두 여기로 모인다.
    /// 부활(<see cref="HqRevivalDevice"/>)처럼 끌려가는 쪽에서 끝내야 하는 경우를 위해 공개한다.
    /// </summary>
    public void ServerDrop(string reason)
    {
        if (IsSpawned && !IsServer)
            return;
        if (CarriedTarget == null)
            return;

        PlayerCarrier target = CarriedTarget;
        CarriedTarget = null;
        SetCarriedRef(null);

        // 대상이 이미 파괴됐으면(퇴장·라운드 종료) 정리할 상대가 없다 — Unity 가짜 null 가드 (#356 계열)
        if (target != null)
            target.ServerRemoveCarrier(this);

        Debug.Log($"[운반] 종료({reason}) — {name}");
        NotifyOwner($"운반 종료: {reason}");
    }

    /// <summary>
    /// 나를 끌고 있는 참가자 중 <paramref name="keeper"/>만 남기고 나머지를 전부 끊는다 — <b>끌려가는
    /// 쪽 사정</b>으로 끝낼 때 쓴다. <paramref name="keeper"/>가 null이면 전부 끊는다
    /// (= <see cref="ServerDropAllCarriers"/>). 서버(또는 오프라인) 전용.
    ///
    /// 감옥 문(플레이어판 #757)과 본부 부활 장치가 쓴다 — 각자 자기 사정으로 "나를 끄는 사람들"을
    /// 정리해야 하는데, 그 정리는 끄는 쪽(<see cref="ServerDrop"/>)이 아니라 끌리는 쪽에서 시작된다.
    /// 실제 해제는 각 참가자 자신의 <see cref="ServerDrop"/>을 불러 처리한다 — 그래야 그쪽의
    /// <see cref="CarriedTarget"/>도 함께 비워진다.
    /// </summary>
    public void ServerReleaseCarriersExcept(PlayerCarrier keeper, string reason)
    {
        if (IsSpawned && !IsServer)
            return;

        for (int i = m_carriedBy.Count - 1; i >= 0; i--)
        {
            PlayerCarrier holder = m_carriedBy[i];
            if (holder == null || holder == keeper)
                continue;

            holder.ServerDrop(reason); // holder 쪽의 CarriedTarget도 비우고, 여기 m_carriedBy에서도 스스로 빠진다
        }
    }

    /// <summary>나를 끌고 있는 전원을 끊는다 — <see cref="ServerReleaseCarriersExcept"/>에 keeper=null.</summary>
    public void ServerDropAllCarriers(string reason) => ServerReleaseCarriersExcept(null, reason);

    /// <summary>합류 — 참가자 한 명이 추가로 나를 끌기 시작한다. 서버(또는 오프라인) 전용.</summary>
    private void ServerAddCarrier(PlayerCarrier carrier)
    {
        m_carriedBy.Add(carrier);
        SyncCarrierCount();

        if (!IsSpawned)
        {
            m_towed?.BeginDraggedFollow(carrier.transform); // 오프라인 폴백
            return;
        }

        // 스폰된 운반자만 참조로 넘길 수 있다(NetworkObjectReference 제약)
        if (carrier.NetworkObject != null && carrier.NetworkObject.IsSpawned)
            BeginDraggedRpc(new NetworkObjectReference(carrier.NetworkObject));
    }

    /// <summary>참가자 한 명이 나를 끄는 것을 멈춘다(그 가닥만) — 서버(또는 오프라인) 전용.</summary>
    private void ServerRemoveCarrier(PlayerCarrier carrier)
    {
        m_carriedBy.Remove(carrier);
        SyncCarrierCount();

        if (!IsSpawned)
        {
            m_towed?.EndDraggedFollow(carrier.transform); // 오프라인 폴백
            return;
        }

        if (carrier.NetworkObject != null && carrier.NetworkObject.IsSpawned)
            EndDraggedRpc(new NetworkObjectReference(carrier.NetworkObject));
        else
            m_towed?.EndDraggedFollow(); // 참조를 못 보낼 만큼 이미 사라졌다 — 안전하게 전부 정리
    }

    // 매 프레임 불려도 대역폭을 안 먹는다 — NetworkVariable 세터가 같은 값이면 스스로 조기 반환한다.
    private void SyncCarrierCount()
    {
        if (IsSpawned && IsServer)
            m_carrierCountSynced.Value = (byte)m_carriedBy.Count;
    }

    // <b>전 피어로 보낸다.</b> 위치 변경 자체는 여전히 오너만 하지만(NetworkTransform 오너 권한),
    // 대상이 래그돌이면 이동이 아니라 <b>밧줄 관절</b>이 붙는다 — 그건 각 피어가 자기 로컬 시체에
    // 걸어야 한다. 안 걸면 원격 시체에는 <b>끄는 힘이 아예 없어</b> 물리를 켜 둬도 따라오지 않는다.
    //
    // 원격에서 이 호출이 안전한 근거: PlayerTowedMotion.BeginDraggedFollow는 래그돌이면 밧줄만 묶고
    // 곧장 반환하며, 원격은 PlayerMovement가 비활성이라(오너만 켜진다) 위치 추종 Tick 자체가 돌지
    // 않는다. 래그돌이 아닌 폴백 경로도 같은 이유로 트랜스폼을 건드리지 못한다. (#506 §10-3)
    [Rpc(SendTo.Everyone)]
    private void BeginDraggedRpc(NetworkObjectReference carrierRef)
    {
        if (m_towed == null)
            return;
        if (!carrierRef.TryGet(out NetworkObject carrierObj))
            return; // 운반자가 이미 디스폰 — 서버의 거리 검사가 곧 운반을 끝낸다

        m_towed.BeginDraggedFollow(carrierObj.transform);
    }

    // <b>참가자별로 가닥을 지정해 끊는다</b> — 무인자였던 옛 버전은 전부를 놓아 합류 중이던 다른
    // 참가자까지 끊었다. 참조를 못 찾으면(운반자가 이미 디스폰) 전부를 놓아 안전하게 정리한다 —
    // 그 참가자가 어차피 사라졌으므로 개별 참조로는 뗄 수도 없다.
    [Rpc(SendTo.Everyone)]
    private void EndDraggedRpc(NetworkObjectReference carrierRef)
    {
        if (carrierRef.TryGet(out NetworkObject carrierObj))
            m_towed?.EndDraggedFollow(carrierObj.transform);
        else
            m_towed?.EndDraggedFollow();
    }

    // 끌고 있는 대상 참조 동기화 — 서버(또는 오프라인)에서만 호출된다. (PlayerEscorter.SetTethered 관례)
    private void SetCarriedRef(PlayerCarrier target)
    {
        if (!IsSpawned || !IsServer)
            return;

        // 스폰된 대상만 참조로 넘길 수 있다(NetworkObjectReference 제약) — 아니면 표시 없이 운반만 진행된다
        bool syncable = target != null && target.NetworkObject != null && target.NetworkObject.IsSpawned;
        ulong desired = syncable ? target.NetworkObject.NetworkObjectId : 0;
        if (m_carriedSynced.Value.NetworkObjectId == desired)
            return; // 값이 그대로면 쓰지 않는다 — 매 프레임 정리가 호출해도 대역폭을 먹지 않게

        m_carriedSynced.Value = syncable ? new NetworkObjectReference(target.NetworkObject) : default;
    }

    private void Update()
    {
        // 운반 상태는 서버 권위 — 클라에서는 아무것도 정리하지 않는다 (PlayerEscorter.Update 관례)
        if (IsSpawned && !IsServer)
            return;
        if (CarriedTarget == null)
        {
            // 대상이 파괴되면 위 가드가 Unity 가짜 null에 걸려 동기화 참조만 남는다 — 원격 오너의 E가
            // 영구히 '내려놓기'로 소비되는 사고를 막는다 (#356과 같은 사정)
            if (IsSpawned && IsServer && m_carriedSynced.Value.NetworkObjectId != 0)
                SetCarriedRef(null);
            return;
        }

        // 부활했으면(본부 존에서 30초 경과) 끌 대상이 아니다 — 일어난 사람을 계속 끌 수는 없다
        if (!CarriedTarget.IsDeadTarget)
        {
            ServerDrop("대상 복구됨");
            return;
        }

        // 내가 쓰러지면 놓친다 — 다운·기절·매달기·기능 정지 어느 쪽이든 손을 놓는다
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
        {
            ServerDrop("운반자 행동불능");
            return;
        }

        // 순간이동 유예 — 감옥 문처럼 둘을 함께 옮기는 경로에서는 아래 거리 검사를 잠깐 쉰다. (#614)
        // <b>몸의 이동은 오너 권한이라 한 왕복 늦게 반영되고</b>, 운반자와 몸은 오너가 서로 달라 그
        // 왕복이 각자 도착한다 — 유예가 없으면 문을 지나는 프레임에 둘이 맵 양끝으로 보여 운반이
        // 스스로 끊긴다. (HqRevivalDevice.k_strayGraceSeconds와 같은 근거·같은 패턴)
        if (m_teleportGraceRemaining > 0f)
        {
            m_teleportGraceRemaining -= Time.deltaTime;
            return;
        }

        // 둘 이상이 함께 끌면(목줄) 거리로 끊지 않는다 — 반대로 당기면 8m를 넘는 것이 정상이고,
        // 그 경우 몸이 움직이지 못하게 막는 것은 RopeDragLoad의 목줄 제한이 한다.
        // (PlayerEscorter.IsLeashedTo·k_leashDraggerCount와 같은 판정·같은 임계)
        if (CarriedTarget.CarrierCount >= k_leashCarrierCount)
            return;

        // 너무 벌어지면 놓친다 — 추종이 실패한 경우다(몸이 지형에 끼거나 오너가 추종을 못 함).
        // 달리기로는 벌어지지 않는 거리라, 여기 걸렸다는 건 정상 추종이 아니라는 뜻이다. (밧줄 끊김과 같은 처리)
        Vector3 delta = CarriedTarget.transform.position - transform.position;
        delta.y = 0f; // 계단·경사에서 높이차로 오작동하지 않게 수평 거리만 본다
        if (delta.sqrMagnitude > m_breakDistance * m_breakDistance)
            ServerDrop("대상을 놓침 — 너무 멀어짐");
    }

    /// <summary>거리 이탈로 끊기는 임계(m) — 목줄 반경(끊김거리 ÷ 인원) 계산에 <see cref="RopeDragLoad"/>가 쓴다.</summary>
    internal float BreakDistance => m_breakDistance;

    // 끌려가는 쪽이 여전히 기능 정지 상태인가 — Update의 부활 감지용(서버·오프라인 실참조).
    // IsRevivable로 바꾸지 않는다 — 몸이 회수 불가(#775)로 바뀌는 경로는 이 운반(CanBeCarried)
    // 시작 전에 이미 걸러지므로 여기 도달할 일이 없고, 바꾸면 그 값이 뒤집히는 매 프레임마다
    // 오탐 ServerDrop이 난다.
    private bool IsDeadTarget => m_incapacitation != null && m_incapacitation.IsDead;

    private bool IsInRange(PlayerCarrier target) =>
        PlayerInteractor.IsWithinReach(
            m_interactor,
            target.transform,
            PlayerInteractor.RangeOf(m_interactor, k_fallbackRange),
            transform.position);

    // 판정 로그는 서버에서 찍히므로 원격 클라 오너는 결과를 볼 수 없다 — 오너 콘솔에도 전달한다 (#109 관례)
    private void NotifyOwner(string message)
    {
        Debug.Log(message);
        if (IsSpawned && IsServer && !IsOwner)
            OwnerLogRpc(message);
    }

    [Rpc(SendTo.Owner)]
    private void OwnerLogRpc(string message) => Debug.Log($"[서버 판정] {message}");

    public override void OnNetworkDespawn()
    {
        // 운반 중 퇴장·라운드 종료 — 끌려가던 쪽 추종을 남기지 않는다
        ServerDrop("운반자 퇴장");

        // 내가 끌려가던 쪽이었다면 나를 끌던 전원의 운반도 끊는다 (반대 방향 정리) — 역순 순회는
        // 각 ServerDrop이 이 목록에서 자기 자신을 빼기 때문이다.
        for (int i = m_carriedBy.Count - 1; i >= 0; i--)
            m_carriedBy[i]?.ServerDrop("대상 퇴장");
    }
}
