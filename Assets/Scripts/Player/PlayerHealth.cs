using Unity.Netcode;
using UnityEngine;

public class PlayerHealth : NetworkBehaviour, IDamageable
{
    [Header("스테이터스")]
    [SerializeField] private int m_maxHp = 100;

    [Tooltip("부활 시 회복되는 HP — 부분 회복 (GDD 7-5, #105)")]
    [SerializeField] private int m_reviveHp = 50;

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

    // 서버 권위로만 실제 값 변경. (데미지 소스가 클라라면 별도 ServerRpc로 요청)
    public void ModifyHp(int delta)
    {
        if (IsSpawned && !IsServer) return;

        SetHp(Mathf.Clamp(CurrentHp + delta, 0, m_maxHp));
    }

    /// <summary>
    /// 피격 순간 <b>전 피어</b>에서 발행된다 — 표현(<see cref="PlayerHitView"/>)용. (#476)
    /// HP는 동기화 값이라 폴링할 수 있지만 "지금 맞았다"는 순간은 값 비교로 잡을 수 없다
    /// (같은 프레임에 여러 번 맞거나, 이미 0인 HP에 또 맞는 경우가 구분되지 않는다).
    ///
    /// 오너 전용이 아니라 전 피어인 이유: NPC 쪽 OnStateChanged/OnStunnedChanged와 같은 방침으로
    /// 발행은 넓게 하고 <b>연출 컴포넌트가 각자 판단</b>한다 (#56/#292). 본부가 CCTV로 현장을 보는
    /// 게임이라 월드 연출(피격 스파크 등)은 결국 전 피어여야 한다.
    /// </summary>
    public event System.Action<DamageHit> OnDamaged;

    /// <summary>
    /// 피격 — 저항형 NPC 범위 타격 등 데미지 소스의 공통 경로. (#79)
    /// HP가 0이 되면 기능 정지(무력화) 처리로 이어진다 (SetHp 내부, GDD 7-5 / #105, #524).
    /// 연출용 <see cref="OnDamaged"/> 브로드캐스트도 여기 하나로 모인다 — 진압봉 오사(#461)·저항형
    /// NPC 공격·폭발이 전부 이 경로를 지나므로, 데미지 소스가 늘어도 연출은 따라온다 (#476).
    /// </summary>
    public void TakeDamage(int amount, GameObject attacker)
    {
        if (IsSpawned && !IsServer) return; // 서버 권위 — ModifyHp도 같은 가드지만 아래 브로드캐스트를 막아야 한다
        if (amount <= 0) return;

        int before = CurrentHp;
        ModifyHp(-amount);

        // '요청한 데미지'가 아니라 '실제로 깎인 양'을 싣는다 — 이미 0인 HP에 들어온 추가 피해는
        // 0이 되어 연출 자체가 나가지 않는다(다운된 몸이 폭발에 휘말릴 때마다 화면이 번쩍이지 않게).
        int applied = before - CurrentHp;
        if (applied <= 0) return;

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
    /// 실제 호출자는 본부 부활 장치(<c>HqRevivalDevice</c>, #365) 하나다 — HP 0이 곧 기능 정지가 된
    /// 뒤로는 현장 구조 채널링(<c>PlayerReviver</c>)이 성립하지 않는다. (#524)
    /// </summary>
    public void ServerRevive()
    {
        if (IsSpawned && !IsServer) return; // 서버 권위 방어
        if (CurrentHp > 0) return; // 쓰러진(HP 0) 상태에서만 유효

        // 회복인데 0이면 여전히 다운이므로 최소 1 보장
        SetHp(Mathf.Clamp(m_reviveHp, 1, m_maxHp));
        m_incapacitation?.Recover();
    }

    private void SetHp(int value)
    {
        int previous = CurrentHp; // 변경 전 값 — HP 0 도달 순간을 감지하기 위함
        m_hp = value;
        if (IsSpawned && IsServer)
            m_syncedHp.Value = value;

        // HP가 0에 도달하는 순간 기능 정지(Die) 진입. 서버(또는 오프라인)에서만 실행되며
        // Incapacitate 자체에도 서버 가드가 있다. (#105, GDD 7-5)
        // 예전에는 Down(현장 구조 가능)으로 들어가 60초 방치 시 Die로 떨어졌지만, 상태가 둘로 갈려
        // 구조·전멸 판정·HUD·운반이 모두 두 갈래로 분기되는 값이 그 비용보다 작았다 — HP 0이 곧
        // 기능 정지이고, 복구 경로는 본부 이송 부활 하나다. (#524)
        if (value == 0 && previous > 0)
            m_incapacitation?.Incapacitate(IncapacitationCause.Die);
    }

    /// <summary>라운드 사이 상태 초기화 — HP 풀 회복 + 다운 해제 + 끌려가기 해제. 서버(또는 오프라인)에서만. (상점 진입)</summary>
    public void ServerResetState()
    {
        if (IsSpawned && !IsServer) return;
        SetHp(m_maxHp);
        m_incapacitation?.Recover();
        // 오검거 호송(#279) 도중 씬 전환되면 despawn이 안 일어나 PlayerTowedMotion 정리가 안 탄다.
        // 세션 유지 리셋 지점에서 끌려가기 추종도 함께 푼다 — 오너 권한이라 서버 전용 StopCarried(→ 오너 RPC)로. (#314 계열)
        m_penaltyView?.StopCarried();
    }
}
