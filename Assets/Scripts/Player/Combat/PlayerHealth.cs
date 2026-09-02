using Unity.Netcode;
using UnityEngine;

public class PlayerHealth : NetworkBehaviour, IDamageable
{
    [Header("스테이터스")]
    [SerializeField]
    private int m_maxHp = 100;

    [Tooltip("부활 시 회복되는 HP — 부분 회복 (GDD 7-5, #105)")]
    [SerializeField]
    private int m_reviveHp = 50;

    // 서버 권위 HP — 서버만 쓰고 모든 클라이언트가 읽는다.
    // m_hp는 서버·오프라인의 진실값 (NpcController의 상태/게이지 이중 구조와 동일 패턴, #79)
    private readonly NetworkVariable<int> m_syncedHp = new NetworkVariable<int>();
    private int m_hp;

    // HP 0 도달 시 쓰러뜨릴 무력화 컴포넌트 (#105). 같은 플레이어 오브젝트에 있음.
    private PlayerIncapacitation m_incapacitation;

    // 오검거 끌려가기(#279) 표현 컴포넌트 — 라운드 사이 리셋 시 추종 상태를 함께 푼다. 같은 오브젝트에 있음.
    private PlayerPenaltyView m_penaltyView;

    public ulong PlayerId => OwnerClientId;
    public int MaxHp => m_maxHp;
    public int CurrentHp => IsSpawned ? m_syncedHp.Value : m_hp;

    /// <summary>
    /// 아직 행동 가능한 상태인지 — 살아있고(HP&gt;0) 무력화되지 않은 플레이어. (#105, #106)
    /// 돌발 이벤트·NPC가 표적을 고를 때 쓴다: 다운된 플레이어는 이미 무력화됐으므로 표적에서 뺀다.
    /// </summary>
    public bool IsTargetable =>
        CurrentHp > 0 && (m_incapacitation == null || !m_incapacitation.IsIncapacitated);

    /// <summary>
    /// <b>지금 때리면 피해가 들어가는 몸인가</b> — <see cref="IsTargetable"/>과 다른 질문이다.
    /// 저쪽은 "표적으로 고를 만한 사람인가"(돌발 이벤트 추첨·NPC 추격 선정)이고, 이쪽은 피해 판정이다.
    ///
    /// 갈리는 것은 <b>비행(Launched)</b> 하나다 — 살아 있는데(HP&gt;0) 무력화라 <see cref="IsTargetable"/>이
    /// 거짓이라, #815가 그 사유를 추가하면서 날아가는 사람이 <b>모든 피해에 면역</b>이 돼 있었다.
    /// 의도된 규칙이 아니라 enum 추가에 딸려온 부작용이다. 근거는 docs/506-explosion-ragdoll.md §13.
    ///
    /// ⚠ 다운·기능 정지(HP 0)는 여기서도 거짓이다 — 그쪽 추가 피해는 확인사살이라
    /// <see cref="ApplyDamage"/>의 <c>IsDowned</c> 분기가 따로 받는다. 기절·매달기·납치·빔은
    /// 이번 범위가 아니다(각자 다른 기획 판단이 필요하다 — 같은 문서 §13).
    /// </summary>
    public bool IsDamageable =>
        IsTargetable
        || (CurrentHp > 0 && m_incapacitation != null && m_incapacitation.IsLaunched);

    // 살아 있는 인스턴스 목록 — 플레이어 전원을 훑는 쪽이 FindObjectsByType으로 씬을 뒤지지 않게 한다
    // (PlayerIncapacitation.All과 같은 패턴, #961). 등록·해제가 OnEnable/OnDisable에 있어야
    // 기존 FindObjectsByType(FindObjectsSortMode.None)과 집합(비활성 제외)이 어긋나지 않는다.
    private static readonly System.Collections.Generic.List<PlayerHealth> s_instances = new();

    /// <summary>씬에 존재하는 모든 플레이어의 체력 컴포넌트 — 자주 순회해도 되는 무할당 목록. (#961)</summary>
    public static System.Collections.Generic.IReadOnlyList<PlayerHealth> All => s_instances;

    private void OnEnable() => s_instances.Add(this);

    private void OnDisable() => s_instances.Remove(this);

    /// <summary>
    /// 반경 안에서 <b>비행(Launched) 중인</b> 플레이어를 모은다 — 호출 시 목록을 비운다.
    ///
    /// <b>왜 물리 쿼리로는 안 잡히는가.</b> 래그돌이 켜진 동안에는 <c>CharacterController</c> 캡슐이
    /// 꺼지고(<c>PlayerRagdoll.SetControllerEnabled</c>) 남는 콜라이더는 뼈뿐인데, 그 뼈는 Ragdoll
    /// 레이어라 근접·치임 판정 마스크가 통째로 뺀다(<see cref="NpcResistState"/> #692 ·
    /// <c>TrafficVehicle.HitLayers</c>). 그래서 <b>날아가는 사람은 어떤 물리 쿼리에도 안 걸린다.</b>
    ///
    /// 마스크에 Ragdoll을 도로 넣는 대신 목록을 직접 훑는 이유는 두 가지다: ① 그 마스크 제외는
    /// 시체(도로에 누운 몸·자기 몸)를 빼려고 있는 것이라 되돌리면 그 버그가 돌아온다, ② 뼈가 사람당
    /// 11개라 논알록 버퍼가 넘친다(#692가 정확히 그 사고였다).
    ///
    /// ⚠ 훑는 대상은 <see cref="PlayerIncapacitation.All"/>(#365의 등록 목록)이다 —
    /// <c>FindObjectsByType</c>은 씬 전체를 뒤지고 배열까지 새로 만드는데, 이 함수는 차량 한 대마다
    /// 틱마다 불린다(<c>TrafficVehicle.ServerApplyLaunchedHits</c>). 무할당 목록이라 상시 비용이 없다.
    /// </summary>
    public static void CollectLaunched(
        Vector3 origin,
        float radius,
        System.Collections.Generic.List<PlayerHealth> results
    )
    {
        results.Clear();

        System.Collections.Generic.IReadOnlyList<PlayerIncapacitation> candidates =
            PlayerIncapacitation.All;
        float maxSqr = radius * radius;
        for (int i = 0; i < candidates.Count; i++)
        {
            PlayerIncapacitation incapacitation = candidates[i];

            // 비행 중이 아니면 볼 것도 없다 — 멀쩡한 사람은 물리 쿼리가 이미 잡았고(두 번 담지 않는다),
            // 다운·기능 정지는 애초에 이 함수의 대상이 아니다. PlayerHealth 조회를 여기서 먼저 거른다.
            if (incapacitation == null || !incapacitation.IsLaunched)
                continue;

            if ((incapacitation.transform.position - origin).sqrMagnitude > maxSqr)
                continue;

            PlayerHealth player = incapacitation.GetComponent<PlayerHealth>();
            if (player != null && player.IsDamageable && !player.IsTargetable)
                results.Add(player);
        }
    }

    private void Awake()
    {
        m_hp = m_maxHp; // 오프라인(비네트워크) Play 테스트 폴백 초기값
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_penaltyView = GetComponent<PlayerPenaltyView>();
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            SetHp(m_maxHp);
        }
    }

    // 튜토리얼에서 HP가 내려갈 수 있는 바닥값 (#663) — 0이 아니므로 다운(무력화)에 들어가지 않는다.
    // 무적(피해 무시)이 아니라 바닥을 두는 이유는 <b>맞는 것 자체는 배워야 하기 때문</b>이다:
    // 피격 연출·체력 감소는 그대로 보이고, 아무것도 못 하게 되는 결말만 없다.
    private const int k_tutorialMinHp = 10;

    // 서버 권위로만 실제 값 변경. (데미지 소스가 클라라면 별도 ServerRpc로 요청)
    public void ModifyHp(int delta) => ApplyHpDelta(delta, skipGrace: false);

    // ModifyHp의 몸통 — '유예 없음'(skipGrace) 하나를 더 받는다. public 시그니처를 늘리지
    // 않는 이유는 밖에서 부를 때 이 개념이 없기 때문이다 — 회복(HealPack)과 납치·UFO 결말의
    // ModifyHp(-CurrentHp)는 유예를 물을 이유가 없다(후자는 이미 Die를 세운 뒤에 부른다).
    //
    // ⚠ <b>바닥(튜토리얼 하한)과 Clamp는 반드시 이 안에 남아야 한다</b> — 밖으로 빼면
    // 즉사 경로(<see cref="TakeLethalDamage"/>)가 k_tutorialMinHp를 우회해 튜토리얼에서 사람이 죽는다.
    private void ApplyHpDelta(int delta, bool skipGrace)
    {
        if (IsSpawned && !IsServer) return;

        // 바닥은 여기 하나로 둔다 — 피해 경로(진압봉·저항 NPC·폭발·차량)가 전부 이 함수를 지나므로
        // 소스가 늘어도 따라온다. 회복은 하한이라 영향이 없다.
        int floor = TutorialDirector.IsActive ? k_tutorialMinHp : 0;
        SetHp(Mathf.Clamp(CurrentHp + delta, floor, m_maxHp), skipGrace);
    }

    /// <summary>
    /// 피격 순간 <b>전 피어</b>에서 발행된다 — 표현(<see cref="PlayerHitView"/>)용. (#476)
    /// </summary>
    public event System.Action<DamageHit> OnDamaged;

    /// <summary>
    /// 피격 — 저항형 NPC 범위 타격·진압봉 오사·낙뢰 등 데미지 소스의 공통 경로. (#79)
    /// HP가 0이 되면 다운 유예(60초)로 이어진다 (SetHp 내부, GDD 7-5 / #105, #725).
    /// <b>유예를 주지 않는 피해원(차량·폭발)은 <see cref="TakeLethalDamage"/>를 쓴다.</b>
    /// 이미 유예 중인 대상이 또 맞으면 유예를 건너뛰고 즉시 완전 사망으로 확정한다 — 확인사살(#725).
    /// 연출용 <see cref="OnDamaged"/> 브로드캐스트도 여기 하나로 모인다 — 진압봉 오사(#461)·저항형
    /// NPC 공격·폭발이 전부 이 경로를 지나므로, 데미지 소스가 늘어도 연출은 따라온다 (#476).
    /// </summary>
    public void TakeDamage(int amount, GameObject attacker)
    {
        if (IsSpawned && !IsServer)
            return; // 서버 권위 — ModifyHp도 같은 가드지만 아래 브로드캐스트를 막아야 한다
        if (amount <= 0)
            return;

        ApplyDamage(amount, attacker, skipGrace: false);
    }

    /// <summary>
    /// <b>유예를 주지 않는 피격</b> — 이 피해로 HP가 0에 <b>도달하면</b> 다운 유예(60초)를 건너뛰고
    /// 곧바로 기능 정지(<see cref="IncapacitationCause.Die"/>)다. 0에 닿지 않으면 평범한 피해다 —
    /// 데미지·거리 감쇠·튜토리얼 하한은 <see cref="TakeDamage"/>와 완전히 같은 경로를 지난다.
    ///
    /// 부르는 곳은 <b>도로 차량</b>(<c>TrafficVehicle</c>, GDD 6-6 "닿으면 즉사")과
    /// <b>폭발</b>(<c>BombBlast</c>, GDD 6-4 "폭심 즉사 / 가장자리 넉백만") 둘이다.
    ///
    /// ⚠ <b>낙뢰(<c>LightningEvent</c>)는 의도적으로 제외</b>다 — 환경 피해라서가 아니라
    /// <b>피해원별 결정</b>이므로, 데미지 수치를 올려도 이 선택은 유지된다. 이름을
    /// "환경 피해"로 두지 않은 이유가 그것이다 — 경계가 규칙과 어긋나면 다음 사람이
    /// 낙뢰를 빠뜨린 것으로 보고 배선한다. (NPC 쪽 <c>NpcHealth.TakeEnvironmentalDamage</c>는
    /// 뜻이 다르다 — 저쪽은 "연행 게이트 우회"다, #690)
    /// </summary>
    public void TakeLethalDamage(int amount, GameObject attacker)
    {
        if (IsSpawned && !IsServer)
            return; // 서버 권위 — ModifyHp도 같은 가드지만 아래 브로드캐스트를 막아야 한다
        if (amount <= 0)
            return;

        ApplyDamage(amount, attacker, skipGrace: true);
    }

    // 두 진입점(TakeDamage · TakeLethalDamage)이 <b>유예 여부만</b> 다르고 이후는 같다 — 여기서
    // 합친다. NpcHealth가 같은 이유로 같은 모양이다(TakeDamage · TakeEnvironmentalDamage → ApplyDamage).
    private void ApplyDamage(int amount, GameObject attacker, bool skipGrace)
    {
        // 유예 중인 대상에게 추가 피해 — 환경·NPC·아군 진압봉 모두 즉시 완전 사망으로 확정한다 (#725)
        // skipGrace는 여기서 무의미하다 — 어느 진입점으로 왔든 이미 Die로 확정되는 길이다.
        if (m_incapacitation != null && m_incapacitation.IsDowned)
        {
            m_incapacitation.ServerFinishOff();

            // 처치 집계 — 확인사살이 유일한 '플레이어가 플레이어를 죽이는' 순간이다. 건강한 동료는
            // HP 0에서 먼저 다운(60초 유예)에 들어가므로 그 진입 자체는 사망이 아니다. 가해자가
            // 환경·NPC면 GetComponent가 null이라 저절로 무동작 — 오사만 걸린다 (#869).
            if (attacker != null && attacker.TryGetComponent(out PlayerKillCredit killCredit))
            {
                string victimName = GetComponent<PlayerNameTag>()?.DisplayName;
                killCredit.ServerCreditKill(
                    string.IsNullOrEmpty(victimName) ? "동료" : victimName,
                    friendlyFire: true
                );
            }
            return;
        }

        int before = CurrentHp;
        ApplyHpDelta(-amount, skipGrace);

        // '요청한 데미지'가 아니라 '실제로 깎인 양'을 싣는다 — 이미 0인 HP에 들어온 추가 피해는
        // 0이 되어 연출 자체가 나가지 않는다(다운된 몸이 폭발에 휘말릴 때마다 화면이 번쩍이지 않게).
        int applied = before - CurrentHp;
        if (applied <= 0)
            return;

        BroadcastDamaged(applied, attacker);
    }

    // 연출 알림을 전 피어에 돌린다 — Baton.PlaySwing과 같은 구조(오프라인은 RPC 경로가 없어 로컬 발행).
    private void BroadcastDamaged(int amount, GameObject attacker)
    {
        bool hasAttacker = attacker != null;
        Vector3 attackerPosition = hasAttacker ? attacker.transform.position : Vector3.zero;

        if (!IsSpawned)
        {
            RaiseDamaged(amount, attackerPosition, hasAttacker); // 오프라인 — 비네트워크 Play 테스트 폴백
            return;
        }

        PlayDamagedRpc(amount, attackerPosition, hasAttacker);
    }

    // 가해자를 GameObject로 실을 수 없어 월드 좌표로 환산해 보낸다 — 방향 계산은 각 피어가
    // 자기 카메라 기준으로 한다 (DamageHit 주석 참고).
    [Rpc(SendTo.Everyone)]
    private void PlayDamagedRpc(int amount, Vector3 attackerPosition, bool hasAttacker) =>
        RaiseDamaged(amount, attackerPosition, hasAttacker);

    private void RaiseDamaged(int amount, Vector3 attackerPosition, bool hasAttacker) =>
        OnDamaged?.Invoke(new DamageHit(amount, attackerPosition, hasAttacker));

    /// <summary>
    /// 부활 — HP를 일부 회복하고 무력화를 해제한다. 서버(또는 오프라인) 전용. (#105, GDD 7-5)
    /// 호출자는 현장 구조 채널링(<c>PlayerReviver</c>, 다운 유예 중, #725)과 부활 키트(<c>ReviveKit</c>,
    /// #613) 둘 — 본부 부활 장치(<c>HqRevivalDevice</c>)는 코드만 있고 씬에는 없다(#613).
    /// 회복량은 경로를 가리지 않고 <see cref="m_reviveHp"/> 하나다.
    /// </summary>
    public void ServerRevive()
    {
        if (IsSpawned && !IsServer)
            return; // 서버 권위 방어
        if (CurrentHp > 0)
            return; // 쓰러진(HP 0) 상태에서만 유효

        // 회복인데 0이면 여전히 다운이므로 최소 1 보장
        SetHp(Mathf.Clamp(m_reviveHp, 1, m_maxHp));
        m_incapacitation?.Recover();

        // 소지품 목록을 한 번 되돌려 보낸다 — 쓰러져 있는 동안 SyncHeldItemsRpc(SendTo.Owner)가
        // 서버로 새어(#820 함정 1) 본인 인벤토리 모델(Slots)이 낡은 채로 남는다. 약탈로 물건이
        // 빠진 경우가 그렇다. 위 Recover()가 소유권을 <b>먼저</b> 되돌리므로 이 호출은 본인에게
        // 간다 — #820 함정 3(ServerRevive → ServerConsume 순서)과 같은 논리다.
        GetComponent<PlayerLoadout>()?.ServerNotifyHeldItemsChanged();
    }

    private void SetHp(int value) => SetHp(value, skipGrace: false);

    // skipGrace = 이 변경이 <b>유예를 주지 않는 피해</b>에서 왔는가. HP 0 도달 순간의 무력화
    // 원인이 이 값 하나로 갈린다 (아래 주석).
    private void SetHp(int value, bool skipGrace)
    {
        int previous = CurrentHp; // 변경 전 값 — HP 0 도달 순간을 감지하기 위함
        m_hp = value;
        if (IsSpawned && IsServer)
            m_syncedHp.Value = value;

        // HP가 0에 도달하는 순간 무력화 진입. 평소는 <b>다운 유예</b>(60초 안에 구조되지 않으면
        // PlayerIncapacitation의 내부 타이머가 스스로 Die로 떨어뜨린다)이고, 유예를 주지 않는
        // 피해원(차량·폭발 — TakeLethalDamage)은 여기서 <b>곧바로 기능 정지</b>로 갈린다.
        // 즉 유예는 "HP가 남았는가"가 아니라 <b>"어떤 피해원이 0으로 내렸는가"</b>로 갈린다.
        // (#105, #725, GDD 6-6 / 7-5)
        if (value != 0 || previous <= 0)
            return;

        if (skipGrace)
        {
            // 다른 Die 전이는 전부 로그가 있다(몸 소실·유예 확인사살·유예 만료) — 즉사만 조용하면
            // "왜 유예 없이 죽었나"를 추적할 자리가 없다.
            Debug.Log($"[Die] 즉사 피해 — 유예 없이 기능 정지: {name}", this);
            m_incapacitation?.Incapacitate(IncapacitationCause.Die);
            return;
        }

        m_incapacitation?.Incapacitate(IncapacitationCause.Down);
    }

    /// <summary>라운드 사이 상태 초기화 — HP 풀 회복 + 다운 해제 + 끌려가기 해제. 서버(또는 오프라인)에서만. (상점 진입)</summary>
    public void ServerResetState()
    {
        if (IsSpawned && !IsServer)
            return;
        SetHp(m_maxHp);
        m_incapacitation?.Recover();
        m_incapacitation?.ServerResetRound();
        // 오검거 호송(#279) 도중 씬 전환되면 despawn이 안 일어나 PlayerTowedMotion 정리가 안 탄다.
        // 세션 유지 리셋 지점에서 끌려가기 추종도 함께 푼다 — 오너 권한이라 서버 전용 StopCarried(→ 오너 RPC)로. (#314 계열)
        m_penaltyView?.StopCarried();
    }
}
