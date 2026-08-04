using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 비밀 청탁 (#485) — 제보 전화를 <b>받은 사람에게만</b> 걸리는 개인 의뢰.
/// "유치장의 ○○○를 인도 지점까지 데려오면 개인 자금을 주겠다".
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
/// 유치장 문은 애초에 경찰 누구나 E로 연다(<see cref="JailDoor"/>).
///
/// 서버 권위 — 발행·완수·지급은 서버(또는 오프라인)에서만 돌고, 클라이언트는 자기 화면 표시만 한다.
/// <b>네트워크 세션 전용이다</b>: 보상 그릇인 PlayerWallet에 오프라인 폴백이 없어(#484) 오프라인 단독
/// Play에서는 청탁을 발행하지 않는다 — 테스트는 Multiplayer Play Mode로 한다.
///
/// 씬 배치: NetworkObject를 가진 전용 오브젝트에 둔다(전화기와 같은 오브젝트에 두지 않는다 — 그쪽은
/// "받았다"만 알리는 역할이다). 인도 지점은 <see cref="SecretFavorDropoff"/>로 따로 배치한다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class SecretFavorBroker : NetworkBehaviour
{
    [Header("전화기 (비우면 씬에서 자동 탐색)")]
    [SerializeField] private TipCallPhone m_phone;

    [Header("발생 조건")]
    [Tooltip("전화를 받았을 때 청탁이 붙을 확률(0~1). 수감자가 1명 이상이고 진행 중인 청탁이 없을 때만 굴린다")]
    [Range(0f, 1f)]
    [SerializeField] private float m_favorChance = 0.35f;

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

    private float m_cooldown;

    // 스폰 전(오프라인 단독 Play)이면 이 피어가 곧 권위다 — TipCallPhone.IsAuthority와 같은 판단
    private bool IsAuthority => !IsSpawned || IsServer;

    // 매니저·전화기 구독은 Start에서 — 모든 매니저의 Awake(=App 등록)가 끝난 뒤가 보장된다 (R6)
    private void Start()
    {
        if (m_phone == null)
            m_phone = FindFirstObjectByType<TipCallPhone>();

        if (m_phone != null)
            m_phone.OnAnswered += HandleAnswered;
        else
            Debug.LogWarning("SecretFavorBroker: TipCallPhone을 찾지 못해 청탁이 걸려오지 않는다", this);

        if (Round != null)
            Round.OnRoundEnded += HandleRoundEnded;
    }

    public override void OnDestroy()
    {
        if (m_phone != null)
            m_phone.OnAnswered -= HandleAnswered;

        if (Round != null)
            Round.OnRoundEnded -= HandleRoundEnded;

        base.OnDestroy();
    }

    // ---- 발행 (서버 · 오프라인 전용) ----

    // 전화를 받았다 — 조건을 보고 청탁을 얹는다. 수배 갱신은 전화기가 이미 처리했다.
    private void HandleAnswered(ulong clientId)
    {
        if (!IsAuthority) return;

        // 세션 전용 — 지급할 지갑(PlayerWallet)이 세션에만 존재한다 (#484)
        if (!IsSpawned)
        {
            Debug.Log("[비밀 청탁] 세션이 아니라 청탁을 발행하지 않는다 (Multiplayer Play Mode로 테스트할 것)");
            return;
        }

        if (m_active) return; // 이미 진행 중인 청탁이 있다

        NpcController target = PickTarget(FindFirstObjectByType<JailZone>());
        if (target == null) return; // 유치장이 비었거나 이름을 댈 수 없는 대상뿐이다

        if (SecretFavorDropoff.All.Count == 0)
        {
            Debug.LogWarning("SecretFavorBroker: 씬에 인도 지점(SecretFavorDropoff)이 없어 청탁을 발행할 수 없다", this);
            return;
        }

        // 확률은 조건을 다 통과한 뒤에 굴린다 — 조건 미달로 못 나온 전화가 확률을 소모하지 않게
        if (Random.value > m_favorChance) return;

        SecretFavorDropoff dropoff = SecretFavorDropoff.All[Random.Range(0, SecretFavorDropoff.All.Count)];
        CitizenIdentity identity = target.GetComponent<CitizenIdentity>();

        m_active = true;
        m_clientId = clientId;
        m_target = target;
        m_dropoff = dropoff;
        m_reward = Mathf.Max(m_minReward, identity.Bounty * m_rewardPercent / 100);

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

    // 수감자 중 무작위 1명. 이름을 댈 수 없는 대상(프로필 미배정)은 건너뛴다 — 지목이 성립하지 않는다.
    private static NpcController PickTarget(JailZone jail)
    {
        if (jail == null || jail.InmateCount <= 0)
            return null;

        int skip = Random.Range(0, jail.InmateCount);
        NpcController fallback = null;

        // Inmates는 HashSet 뷰라 인덱스 접근이 없다 — 무작위 지점부터 세어 나가고, 그 뒤가 전부
        // 부적격이면 앞에서 찾은 후보로 되돌린다(수감자가 적어 순회 비용은 무시할 수 있다)
        foreach (NpcController inmate in jail.Inmates)
        {
            if (inmate == null)
                continue;

            CitizenIdentity identity = inmate.GetComponent<CitizenIdentity>();
            if (identity == null || identity.Profile == null || string.IsNullOrEmpty(identity.Profile.CitizenName))
                continue;

            fallback ??= inmate;

            if (skip-- <= 0)
                return inmate;
        }

        return fallback;
    }

    // ---- 완수 판정 (서버 · 오프라인 전용) ----

    private void Update()
    {
        if (!m_active || !IsAuthority) return;

        m_cooldown -= Time.deltaTime;
        if (m_cooldown > 0f) return;
        m_cooldown = m_checkInterval;

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

    // 라운드 종료 — 미완수 의뢰를 접는다. 다음 라운드로 새면 이미 사라진 대상을 계속 추적한다.
    private void HandleRoundEnded(RoundResult result, RoundEndReason reason)
    {
        if (m_active)
            Debug.Log("[비밀 청탁] 라운드 종료 — 미완수 의뢰를 정리한다");

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
