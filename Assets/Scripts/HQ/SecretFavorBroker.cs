using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 비밀 청탁 (#485) — 본부 전화를 <b>받은 사람에게만</b> 걸리는 개인 의뢰.
/// "유치장의 ○○○를 인도 지점까지 데려오면 개인 자금을 주겠다".
///
/// <b>제보 전화와 별개로 걸려온다.</b> 새 수감자가 앉을 때마다 "이 사람을 빼달라는 전화가 올지"를
/// 대상별로 굴려(<see cref="m_favorChance"/>) 예약하고, 시간이 되면 <see cref="TipCallPhone.TryRingExternal"/>로
/// 벨을 울린다. 그 벨은 제보 전화 횟수를 소모하지 않고 수배도 승격하지 않으므로, <b>팀은 이 전화 때문에
/// 갱신 기회를 잃지 않는다</b>. 대신 받은 사람 화면에 수배가 안 늘어나는 것이 본부에 남는 희미한 단서다
/// (승격 후보가 없어 갱신이 안 되는 경우와 구분되지 않으므로 확증은 아니다).
///
/// 팀에 손해를 끼치고 나만 이득을 보는 경로다. 트레이드오프는 별도 배선 없이 성립한다:
/// 반출(<see cref="JailIntake.ServerExtract"/>)이 정산 레코드를 지우므로 그 순간
/// <see cref="RoundManager.CurrentFund"/>(=JailZone.BountyTotal)가 대상의 현상금만큼 줄어든다 (#340/#395).
/// 대상은 인도 지점에서 소멸하므로 다시 잡아 메울 수도 없다 — 꺼냈다 다시 앉혀 양쪽을 다 챙기는 구멍이 없다.
///
/// <b>정보 비대칭이 이 기능의 전부다.</b> 의뢰 발행·완수 통보는 요청자 한 명에게만 가는 타깃 RPC이고
/// (<see cref="RpcTarget.Single"/> — ShopStand.ReplyRpc와 같은 관례), 보상은 오너만 읽는
/// <see cref="PlayerWallet"/>으로 들어간다. NetworkVariable로 상태를 두면 전 피어가 읽으므로 쓰지 않는다.
///
/// <b>들킬 위험</b>은 유치장 CCTV·사이렌(#488)과, 목표 진행 금액이 줄어드는 것(RoundFundBoard)이다.
/// 자물쇠 경보(<see cref="JailAlarmBeacon"/>)는 울리지 않는다 — 반출은 <see cref="JailLock"/>을 건드리지 않고,
/// 감옥 문은 이제 통로가 아니라 순간이동 지점이다(<see cref="JailDoor"/>, #537).
///
/// 서버 권위 — 발행·완수·지급은 서버(또는 오프라인)에서만 돌고, 클라이언트는 자기 화면 표시만 한다.
/// <b>네트워크 세션 전용이다</b>: 보상 그릇인 PlayerWallet에 오프라인 폴백이 없어(#484) 오프라인 단독
/// Play에서는 청탁을 발행하지 않는다 — 테스트는 Multiplayer Play Mode로 한다.
///
/// 씬 배치: NetworkObject를 가진 전용 오브젝트에 둔다(전화기와 같은 오브젝트에 두지 않는다 — 그쪽은
/// 울리고 받는 장치일 뿐 무엇을 위한 전화인지 모른다). 인도 지점은 <see cref="SecretFavorDropoff"/>로 따로 배치한다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class SecretFavorBroker : NetworkBehaviour
{
    [Header("전화기 (비우면 씬에서 자동 탐색)")]
    [SerializeField] private TipCallPhone m_phone;

    [Header("발생 조건")]
    [Tooltip(
        "<b>수감자 한 명당</b> 그 사람을 빼달라는 전화가 올 확률(0~1). 새 수감자가 좌석에 앉을 때마다 굴린다.\n\n"
            + "라운드 예산이 아니라 대상별 추첨이라, 유치장이 붐빌수록 제안이 잦아진다. 낮게 둘 것 — "
            + "0.1이면 한 라운드에 5명을 잡았을 때 한 번쯤 걸려온다(41%).\n\n"
            + "제보 전화 횟수와는 별개다: 청탁 전화는 자기 예산으로 따로 오며 수배 갱신을 하지 않는다"
    )]
    [Range(0f, 1f)]
    [SerializeField] private float m_favorChance = 0.1f;

    [Tooltip("대상이 수감된 뒤 청탁 전화가 걸려오기까지의 최소 대기(초) — 잡아 온 직후 바로 울리면 짜인 느낌이 난다")]
    [Min(0f)]
    [SerializeField] private float m_callDelayMin = 30f;

    [Tooltip("최대 대기(초). 최소값보다 작으면 최소값이 쓰인다")]
    [Min(0f)]
    [SerializeField] private float m_callDelayMax = 90f;

    [Header("보상")]
    [Tooltip("대상 현상금의 몇 %를 개인 자금으로 줄 것인가. 인계 몫(10%, SettlementController)과 비교되는 값이다")]
    [Min(0)]
    [SerializeField] private int m_rewardPercent = 40;

    [Tooltip("보상 하한(원) — 경범죄자(현상금 500~4,000)가 뽑혀도 의뢰가 시시해지지 않게 한다")]
    [Min(0)]
    [SerializeField] private int m_minReward = 2000;

    [Header("완수 판정")]
    [Tooltip("검사 주기(초) — 매 프레임 돌 필요가 없다 (JailIntake와 같은 관례)")]
    [Min(0f)]
    [SerializeField] private float m_checkInterval = 0.2f;

    [Tooltip(
        "발행 후 이 시간(초)이 지나면 의뢰를 거둬들인다 — \"저쪽도 마냥 기다리지 않는다\". 0이면 만료 없음.\n\n"
            + "이게 없으면 받은 사람이 청탁을 무시할 때 슬롯이 라운드 끝까지 잠겨, 그 뒤 들어온 수감자 전원이 "
            + "추첨 기회를 잃는다(동시 1건이므로)"
    )]
    [Min(0f)]
    [SerializeField] private float m_favorExpireSeconds = 180f;

    [Header("문구 (HudTable)")]
    [Tooltip("받은 순간 잠깐 뜨는 알림 — Hud.SecretFavor.Offer")]
    [SerializeField] private LocalizedString m_offerToast;

    [Tooltip("라운드 내내 남는 한 줄 — Hud.SecretFavor.Line ({0} 대상 / {1} 보상)")]
    [SerializeField] private LocalizedString m_offerLine;

    [Tooltip("완수 알림 — Hud.SecretFavor.Complete ({0} 보상)")]
    [SerializeField] private LocalizedString m_completeToast;

    [Tooltip("토스트가 화면에 머무는 시간(초)")]
    [Min(1f)]
    [SerializeField] private float m_toastSeconds = 8f;

    private RoundManager Round => App.Game.Round;

    // 진행 중인 의뢰 — 서버(또는 오프라인) 전용. 동시에 한 건만 둔다: 여러 건을 허용하면 같은 대상이
    // 두 명에게 지목되는 경합(한쪽이 데려가면 다른 쪽은 영구 미완수)이 생기고, 그 처리는 이 기능의 본질이 아니다.
    private bool m_active;
    private ulong m_clientId;
    private NpcController m_target;
    private SecretFavorDropoff m_dropoff;
    private int m_reward;
    private float m_expireTime; // 이 시각을 넘기면 의뢰를 거둬들인다 (m_favorExpireSeconds가 0이면 안 본다)

    private float m_cooldown;

    // 청탁 전화가 예약된 대상과 그 시각 — 아직 벨이 울리지 않은 상태다. 서버(또는 오프라인) 전용.
    private NpcController m_pendingTarget;
    private float m_callTime;

    // 이미 추첨을 거친 대상 — <b>대상 한 명은 평생 한 번만 굴린다.</b> (서버·오프라인 전용)
    //
    // 없으면 반출(#492)로 일으켰다 다시 앉히는 것만으로 추첨이 다시 돌아, 청탁이 뜰 때까지 리롤할 수 있다.
    // 우연히 걸려오는 제안이라는 전제가 깨지고 배신 기회를 의도적으로 낚을 수 있게 된다.
    // JailIntake.ServerExtract가 ClearDelivered를 일부러 부르지 않는 것과 같은 계열의 방어다.
    //
    // 추첨을 건너뛴 경우(이미 진행 중인 청탁이 있어서)에도 등록한다 — 건너뛴 대상을 남겨 두면
    // 그 대상으로 리롤이 다시 열린다. 대신 그 수감자는 기회를 잃는다(아래 HandleInmateAdmitted 주석).
    private readonly HashSet<NpcController> m_rolled = new HashSet<NpcController>();

    // 유치장 — 수감 훅을 걸어 두려고 잡는다. 장소 오브젝트라 App 파사드 대상이 아니다(JailIntake와 같은 관례).
    private JailZone m_jail;

    // 감옥 문 — 반출 대상이 문 밖으로 나오는 순간을 받으려고 잡는다 (#548).
    private JailIntake m_intake;

    // 스폰 전(오프라인 단독 Play)이면 이 피어가 곧 권위다 — TipCallPhone.IsAuthority와 같은 판단
    private bool IsAuthority => !IsSpawned || IsServer;

    // 매니저·전화기 구독은 Start에서 — 모든 매니저의 Awake(=App 등록)가 끝난 뒤가 보장된다 (R6)
    private void Start()
    {
        if (m_phone == null)
            m_phone = FindFirstObjectByType<TipCallPhone>();

        if (m_phone == null)
            Debug.LogWarning("SecretFavorBroker: TipCallPhone을 찾지 못해 청탁이 걸려오지 않는다", this);

        m_jail = FindFirstObjectByType<JailZone>();
        if (m_jail != null)
            m_jail.OnInmateAdmitted += HandleInmateAdmitted;
        else
            Debug.LogWarning("SecretFavorBroker: JailZone을 찾지 못해 청탁이 걸려오지 않는다", this);

        // 반출 대상이 문 밖으로 나오는 순간을 받는다 — 목적지를 아는 것은 이쪽뿐이다 (#548).
        // JailZone과 같은 관례로 찾는다(장소 오브젝트라 App 파사드 대상이 아니다).
        m_intake = FindFirstObjectByType<JailIntake>();
        if (m_intake != null)
            m_intake.OnInmateExited += HandleInmateExited;
        else
            Debug.LogWarning("SecretFavorBroker: JailIntake를 찾지 못해 반출 대상이 인도 지점으로 걸어가지 않는다", this);

        if (Round != null)
            Round.OnRoundEnded += HandleRoundEnded;
    }

    public override void OnDestroy()
    {
        if (m_jail != null)
            m_jail.OnInmateAdmitted -= HandleInmateAdmitted;

        if (m_intake != null)
            m_intake.OnInmateExited -= HandleInmateExited;

        if (Round != null)
            Round.OnRoundEnded -= HandleRoundEnded;

        base.OnDestroy();
    }

    // ---- 전화 예약 (서버 · 오프라인 전용) ----

    /// 새 수감자가 앉았다 — <b>그 사람을 빼달라는 전화가 올지</b> 여기서 한 번 굴린다.
    /// 대상을 이 시점에 정하므로 발행 때 다시 고를 일이 없고, 유치장이 붐빌수록 제안이 잦아진다.
    ///
    /// 이미 예약·진행 중인 청탁이 있으면 굴리지도 않는다 — 동시에 한 건만 두기 때문이다(m_active 주석).
    /// 그 결과 확률은 "비어 있을 때만" 평가되어, 붐빌 때 제안이 쏟아지지 않는다.
    private void HandleInmateAdmitted(NpcController npc)
    {
        if (!IsAuthority || npc == null) return;

        // 세션 전용 — 지급할 지갑(PlayerWallet)이 세션에만 존재한다 (#484)
        if (!IsSpawned) return;

        // 재수감 리롤 방어 — 굴리기 전에 먼저 등록한다. 아래 게이트에 걸려 추첨을 못 해도 등록은 남는다:
        // 그 대상은 이번 라운드에 기회를 잃지만(청탁이 이미 진행 중이었으니 기능은 돌고 있다),
        // 남겨 두면 그 대상으로 리롤이 열린다.
        if (!m_rolled.Add(npc)) return;

        if (m_active || m_pendingTarget != null) return;

        // 이름을 댈 수 없으면 지목이 성립하지 않는다
        CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();
        if (identity == null || identity.Profile == null || string.IsNullOrEmpty(identity.Profile.CitizenName))
            return;

        if (SecretFavorDropoff.All.Count == 0)
        {
            Debug.LogWarning("SecretFavorBroker: 씬에 인도 지점(SecretFavorDropoff)이 없어 청탁이 성립하지 않는다", this);
            return;
        }

        if (Random.value > m_favorChance) return;

        m_pendingTarget = npc;
        float max = Mathf.Max(m_callDelayMin, m_callDelayMax);
        m_callTime = Time.time + Random.Range(m_callDelayMin, max);
        Debug.Log($"[비밀 청탁] {identity.Profile.CitizenName} 건으로 전화 예약 — {m_callTime - Time.time:0}초 뒤");
    }

    // 예약된 시각이 되면 전화기에 벨을 요청한다. 대상이 그 사이 유치장에서 빠졌으면 예약을 버린다.
    private void TickCallSchedule()
    {
        if (m_pendingTarget == null || m_phone == null) return;

        // 탈옥·반출로 이미 나갔거나 파괴된 대상 — "유치장에 있는 ○○○"가 성립하지 않는다
        if (m_pendingTarget.CurrentState != NpcState.Jailed)
        {
            Debug.Log("[비밀 청탁] 대상이 이미 유치장에서 빠져 전화 예약을 취소한다");
            m_pendingTarget = null;
            return;
        }

        if (Time.time < m_callTime) return;

        // 제보 전화가 울리는 중이면 다음 틱에 다시 시도한다 — 두 벨이 겹치면 어느 쪽인지 알 수 없다
        NpcController target = m_pendingTarget;
        if (!m_phone.TryRingExternal(clientId => Issue(clientId, target))) return;

        // 벨을 울린 것으로 이 예약은 소모된다 — 놓치면 그 기회는 사라진다(제보 전화와 같은 규칙).
        // 대상은 콜백이 들고 있으므로 여기서 비워도 받았을 때 지목이 유지된다.
        m_pendingTarget = null;
        Debug.Log("[비밀 청탁] 청탁 전화 수신 — 받은 사람에게만 의뢰가 간다");
    }

    // ---- 발행 (서버 · 오프라인 전용) ----

    // 청탁 전화를 받았다 — 받은 사람에게만 의뢰를 보낸다. 수배는 승격되지 않는다(TipCallPhone).
    private void Issue(ulong clientId, NpcController target)
    {
        if (m_active) return; // 벨이 울리는 사이에 다른 청탁이 시작됐다

        if (target == null || target.CurrentState != NpcState.Jailed)
        {
            Debug.Log("[비밀 청탁] 전화를 받았지만 대상이 이미 유치장에 없다 — 의뢰가 성립하지 않는다");
            return;
        }

        if (SecretFavorDropoff.All.Count == 0)
        {
            Debug.LogWarning("SecretFavorBroker: 씬에 인도 지점(SecretFavorDropoff)이 없어 청탁을 발행할 수 없다", this);
            return;
        }

        SecretFavorDropoff dropoff = SecretFavorDropoff.All[Random.Range(0, SecretFavorDropoff.All.Count)];
        CitizenIdentity identity = target.GetComponent<CitizenIdentity>();

        m_active = true;
        m_clientId = clientId;
        m_target = target;
        m_dropoff = dropoff;
        m_reward = Mathf.Max(m_minReward, identity.Bounty * m_rewardPercent / 100);
        m_expireTime = Time.time + m_favorExpireSeconds;

        // 이름은 수배 리스트·인명부와 같은 정본을 쓴다(WantedListManager도 CitizenName으로 등재한다) —
        // 표시값(m_nameView)을 쓰면 위조범이 지목됐을 때 대조로 대상을 찾을 수 없다 (#223)
        OfferRpc(
            identity.Profile.CitizenName.ToFixed64(),
            dropoff.Id,
            m_reward,
            RpcTarget.Single(clientId, RpcTargetUse.Temp)
        );

        Debug.Log($"[비밀 청탁] {clientId}번에게 발행 — 대상 {identity.Profile.CitizenName}, 보상 {m_reward}원");
    }

    // ---- 반출 보행 (서버 · 오프라인 전용, #548) ----

    // 반출 대상이 문을 나섰다 — 내 청탁 대상이면 인도 지점을 목적지로 준다.
    // 여기서 걷기 시작하는 것이 곧 <b>저지 창이 열리는 순간</b>이다: 대상이 혼자 길 위에 나오고,
    // 그 시간 동안 다른 플레이어가 알아채면 기절시켜 밧줄로 묶어 되돌릴 수 있다.
    //
    // 내 대상이 아니면 아무것도 하지 않는다 — 목적지를 못 받은 대상은 문 쪽에서 도주로 보낸다.
    private void HandleInmateExited(NpcController npc)
    {
        if (!IsAuthority || !m_active)
            return;

        if (npc == null || npc != m_target || m_dropoff == null)
            return;

        npc.Custody.StartRelease(m_dropoff.Center);
        Debug.Log($"[비밀 청탁] 대상이 인도 지점으로 걸어간다: {npc.name}");
    }

    // ---- 완수 판정 (서버 · 오프라인 전용) ----

    private void Update()
    {
        if (!IsAuthority) return;

        m_cooldown -= Time.deltaTime;
        if (m_cooldown > 0f) return;
        m_cooldown = m_checkInterval;

        TickCallSchedule();

        if (!m_active) return;

        // 시간이 다 됐다 — 의뢰를 거둬들인다. 슬롯이 풀려 다음 수감자가 다시 추첨 대상이 된다
        if (m_favorExpireSeconds > 0f && Time.time >= m_expireTime)
        {
            Debug.Log("[비밀 청탁] 시간이 지나 의뢰가 거둬들여졌다");
            Clear();
            return;
        }

        // 대상이 사라졌다 — 라운드 종료 잔류 정리 등. 완수할 수 없으니 의뢰를 접는다
        if (m_target == null || m_dropoff == null)
        {
            Debug.Log("[비밀 청탁] 대상이 사라져 의뢰를 취소한다");
            Clear();
            return;
        }

        // 의뢰인이 나갔다 — 지급할 지갑이 없다. 보낼 곳도 없으니 화면 정리 RPC 없이 상태만 비운다
        Transform requester = ResolveRequester();
        if (requester == null)
        {
            Debug.Log("[비밀 청탁] 의뢰인이 접속을 끊어 의뢰를 취소한다");
            ClearServerState();
            return;
        }

        // <b>대상과 의뢰인이 함께 인도 범위 안에 있어야 완수다.</b> 대상만 보면 남이 데려다 놓은 것으로
        // 보상이 나가고, 의뢰인만 보면 대상 없이 지점만 밟아도 된다. 대상의 상태는 보지 않는다 —
        // 밧줄로 끌고 왔든 따라오게 했든 세워 두고 왔든 "여기까지 데려왔다"는 사실은 같다.
        if (!m_dropoff.Contains(m_target.transform.position) || !m_dropoff.Contains(requester.position))
            return;

        Complete();
    }

    // 완수 — 대상을 도시에서 지우고 의뢰인 개인 자금에 즉시 넣는다.
    private void Complete()
    {
        NpcController target = m_target;
        ulong clientId = m_clientId;
        int reward = m_reward;

        // 파괴 전에 줄을 끊는다 — 끌고 있는 채로 사라지면 그 플레이어가 영영 연행 중이 된다.
        // 줄다리기로 여러 명이 걸려 있을 수 있어 전원에게서 이 대상만 뺀다 (JailbreakEvent.Despawn과 동일)
        foreach (PlayerEscorter escorter in PlayerEscorter.FindEscortersOf(target))
            escorter.ReleaseDrag(target);

        // "탈출해서 사라졌다" — 소멸 연출을 함께 낸다(NpcDespawnVfx가 프리팹에 배선돼 있으면)
        SuddenEventUtil.DespawnOrDestroy(target.gameObject);

        // 팀 자금·수배 리스트는 건드리지 않는다. 목표 진행 금액은 반출 시점에 이미 줄었고(JailZone),
        // 수배 재등재도 하지 않는다 — 필드에 없는 대상을 목록에 올리면 팀이 영영 못 잡는 건을 쫓는다.
        PlayerWallet wallet = PlayerWallet.FindByClientId(clientId);
        if (wallet != null)
            wallet.ServerAdd(reward);
        else
            Debug.LogWarning($"SecretFavorBroker: {clientId}번의 지갑을 찾지 못해 보상을 지급하지 못했다", this);

        ClearServerState();
        CompleteRpc(reward, RpcTarget.Single(clientId, RpcTargetUse.Temp));
        Debug.Log($"[비밀 청탁] 완수 — {clientId}번에게 개인 자금 {reward}원");
    }

    // 라운드 종료 — 미완수 의뢰와 걸려 있던 전화 예약을 접는다.
    // 다음 라운드로 새면 이미 사라진 대상을 계속 추적하거나, 그 대상 이름으로 전화가 걸려온다.
    private void HandleRoundEnded(RoundResult result, RoundEndReason reason)
    {
        if (m_active)
            Debug.Log("[비밀 청탁] 라운드 종료 — 미완수 의뢰를 정리한다");

        m_pendingTarget = null;
        m_rolled.Clear(); // 다음 라운드는 새 판이다 — 파괴된 NPC 참조도 여기서 함께 정리된다
        Clear();
    }

    // 의뢰인 화면의 표식·한 줄까지 함께 지운다 (취소 경로).
    private void Clear()
    {
        if (!m_active) return;

        ulong clientId = m_clientId;
        ClearServerState();

        if (IsSpawned && IsServer)
            ClearRpc(RpcTarget.Single(clientId, RpcTargetUse.Temp));
    }

    private void ClearServerState()
    {
        m_active = false;
        m_target = null;
        m_dropoff = null;
        m_reward = 0;
    }

    // 의뢰인의 플레이어 오브젝트 — 서버에서만 채워지는 ConnectedClients를 쓴다 (PlayerWallet.FindByClientId와 동일)
    private Transform ResolveRequester()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null
            || !nm.ConnectedClients.TryGetValue(m_clientId, out NetworkClient client)
            || client.PlayerObject == null)
            return null;

        return client.PlayerObject.transform;
    }

    // ---- 의뢰인 화면 (요청자 1명에게만) ----

    // 지점 이름을 문자열로 싣지 않는다 — 서버의 언어 설정이 남의 화면에 나온다. 번호만 보내고
    // 그 지점의 표식을 이 피어에서 켜는 것으로 위치를 알린다(표식은 켠 피어에서만 보인다).
    [Rpc(SendTo.SpecifiedInParams)]
    private void OfferRpc(FixedString64Bytes targetName, int dropoffId, int reward, RpcParams rpcParams)
    {
        SecretFavorDropoff dropoff = SecretFavorDropoff.Find(dropoffId);
        if (dropoff != null)
            dropoff.SetMarkerVisible(true);
        else
            Debug.LogWarning($"SecretFavorBroker: {dropoffId}번 인도 지점을 찾지 못해 표식을 켤 수 없다", this);

        // Smart String 인자 — SignalDecoder.m_receivedFormat과 같은 방식
        m_offerLine.Arguments = new object[] { targetName.ToString(), reward };

        // 토스트는 "전화에 뭔가 왔다"를 눈에 띄게 하고, 내용은 라운드 내내 남는 한 줄이 들고 있는다
        App.UI.Toast?.Show(m_offerToast, m_toastSeconds);
        App.UI.SecretFavor?.Show(m_offerLine);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void CompleteRpc(int reward, RpcParams rpcParams)
    {
        SecretFavorDropoff.HideAllMarkers();
        App.UI.SecretFavor?.HideImmediate();

        m_completeToast.Arguments = new object[] { reward };
        App.UI.Toast?.Show(m_completeToast, m_toastSeconds);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void ClearRpc(RpcParams rpcParams)
    {
        SecretFavorDropoff.HideAllMarkers();
        App.UI.SecretFavor?.HideImmediate();
    }
}
