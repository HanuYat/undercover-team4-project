using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 범인 탈출 (돌발 이벤트 · 본부) — 본부가 무인일 때 확률적으로 침입자가 본부에 들어와
/// 유치장 자물쇠를 열고, 수감돼 있던 범인들을 탈출시킨다. (GDD 6-4, #231)
/// 본부 상주 트레이드오프(GDD 4-1)를 강화한다 — 전원이 현장에 나가 있으면 잡아 둔 범인을 잃는다.
///
/// 흐름(전부 서버 권위 · #56):
///  1. <see cref="CanTrigger"/> — 본부 무인 + 무인 지속 시간 충족 + 자물쇠 잠김 + 수감자 존재일 때만 성립.
///  2. <see cref="ServerBegin"/> — 침입자 NPC를 스폰해 자물쇠 지점까지 걸어가게 한다(StartIntrude).
///  3. 도착(OnIntrudeFinished reached=true) — 자물쇠를 열고(ServerUnlock) 수감자를 전원 방출한다.
///     · 방출: JailZone.ReleaseInmate + NpcController.ClearDelivered + StartFlee(재검거 가능하게)
///     · 진범만: RoundManager.ReportCriminalEscaped + WantedListManager.ReinstateByNpcId
///  4. 침입자는 잠시 머문 뒤(또는 경로 실패 시 즉시) 물러난다(디스폰).
///
/// 발동 빈도(추첨 주기)는 <see cref="SuddenEventManager"/>가 쥐고, 이 이벤트는 "지금 발동 가능한가"만 판정한다.
/// 스폰물(침입자)은 자기 NetworkObject로, 자물쇠·수배·할당량 상태는 각 소유 컴포넌트가 전파한다 —
/// 이 이벤트는 매니저처럼 상태를 얹지 않는다(ISuddenEvent 규약).
/// </summary>
[RequireComponent(typeof(SuddenEventManager))]
public class JailbreakEvent : MonoBehaviour, ISuddenEvent
{
    [Header("침입자 프리팹 (NpcController)")]
    [SerializeField] private NpcController m_intruderPrefab;

    [Header("스폰 지점 (본부 입구 — 비우면 자물쇠 위치)")]
    [Tooltip("침입자가 나타나는 지점 — NavMesh 위에 둘 것. 비우면 자물쇠 위치에서 스폰된다")]
    [SerializeField] private Transform m_spawnPoint;

    [Header("본부 무인 감지 (비우면 씬에서 자동 탐색)")]
    [SerializeField] private HqOccupancyZone m_occupancyZone;

    [Header("유치장 / 자물쇠 (비우면 자동 탐색)")]
    [SerializeField] private JailZone m_jailZone;
    [SerializeField] private JailLock m_jailLock;

    [Header("발동 조건")]
    [Tooltip("본부가 이 시간(초) 이상 비어 있어야 발동한다 — 잠깐 자리를 비운 것으로는 터지지 않게")]
    [SerializeField] private float m_minUnmannedSeconds = 20f;

    [Header("침입자 지속")]
    [Tooltip("자물쇠를 연 뒤 침입자가 이 시간(초) 머문 뒤 물러난다(디스폰)")]
    [SerializeField] private float m_intruderLingerSeconds = 3f;

    [Tooltip("도착 통보가 끝내 오지 않는 이례적 상황(경로 교착 등)의 안전장치 — 이 시간(초) 넘기면 강제 정리")]
    [SerializeField] private float m_maxActiveSeconds = 30f;

    private NpcController m_intruder;
    private bool m_unlocked; // 자물쇠를 이미 열었는가 — 도착 통보 이후 단계
    private float m_despawnTime; // 물러날 예정 시각(Time.time)
    private float m_beginTime;

    // 방출 대상 스냅샷 — Inmates(HashSet 뷰)를 순회하며 ReleaseInmate로 수정하면 열거 예외가 나므로 복사한다
    private readonly List<NpcController> m_releaseBuffer = new List<NpcController>();

    public string DisplayName => "범인 탈출";

    public bool IsActive => m_intruder != null;

    // 매니저는 App 파사드 단일 경로로 읽는다 — 캐싱하지 않는다(파괴된 참조를 쥐지 않게). (#245 아키텍처 규칙 R1/R8)
    private WantedListManager WantedList => App.Game.WantedList;
    private RoundManager Round => App.Game.Round;

    private void Awake()
    {
        // 매니저가 아닌 장소·부품만 여기서 찾는다 — 본부 트리거 존·유치장·자물쇠는 App 등록 대상이 아니다
        if (m_occupancyZone == null)
            m_occupancyZone = FindFirstObjectByType<HqOccupancyZone>();
        if (m_jailZone == null)
            m_jailZone = FindFirstObjectByType<JailZone>();
        if (m_jailLock == null)
            m_jailLock = m_jailZone != null ? m_jailZone.GetComponent<JailLock>() : FindFirstObjectByType<JailLock>();
    }

    public bool CanTrigger()
    {
        if (m_intruderPrefab == null || m_occupancyZone == null || m_jailZone == null || m_jailLock == null)
            return false;

        // 본부가 충분히 오래 비어 있어야 하고(GDD 4-1 트레이드오프), 자물쇠가 아직 잠겨 있어야 하며,
        // 풀어 줄 수감자가 실제로 있어야 성립한다 — 빈 유치장에서 자물쇠만 여는 무의미 발동을 막는다.
        // (자물쇠는 새 수감자가 들어올 때 JailZone.Admit이 다시 잠그므로 연속 발동은 자연히 막힌다)
        if (!m_occupancyZone.IsUnmanned)
            return false;
        if (m_occupancyZone.UnmannedSeconds < m_minUnmannedSeconds)
            return false;
        if (!m_jailLock.IsLocked)
            return false;
        if (m_jailZone.InmateCount <= 0)
            return false;

        return true;
    }

    public void ServerBegin()
    {
        if (m_intruderPrefab == null || m_jailLock == null)
        {
            Debug.LogWarning("JailbreakEvent: 침입자 프리팹 또는 자물쇠가 없어 발동 취소", this);
            return;
        }

        Transform target = m_jailLock.transform;
        Vector3 spawnPos = m_spawnPoint != null ? m_spawnPoint.position : target.position;
        Quaternion spawnRot = m_spawnPoint != null ? m_spawnPoint.rotation : Quaternion.identity;

        m_intruder = Instantiate(m_intruderPrefab, spawnPos, spawnRot);

        if (SuddenEventUtil.IsNetworkSessionActive)
            m_intruder.GetComponent<NetworkObject>().Spawn();

        m_unlocked = false;
        m_beginTime = Time.time;

        // 도착·실패 통보를 받아 자물쇠 해제/불발 정리를 판정한다
        m_intruder.OnIntrudeFinished += HandleIntrudeFinished;
        m_intruder.StartIntrude(target);
    }

    public void ServerTick()
    {
        if (m_intruder == null)
            return;

        // 자물쇠를 연 뒤 머무는 시간이 지나면 침입자가 물러난다
        if (m_unlocked && Time.time >= m_despawnTime)
        {
            Despawn();
            return;
        }

        // 도착 통보가 끝내 오지 않는 이례적 상황의 안전장치 (정상 경로는 OnIntrudeFinished로 즉시 정리된다)
        if (!m_unlocked && Time.time - m_beginTime >= m_maxActiveSeconds)
        {
            Debug.LogWarning("[돌발이벤트] 범인 탈출 — 침입 시간 초과, 강제 정리");
            Despawn();
        }
    }

    public void ServerReset()
    {
        // 라운드 종료 등으로 즉시 끝난다 — 침입자만 정리한다.
        // 이미 열린 자물쇠·방출된 수감자는 되돌리지 않는다: 라운드가 끝났으므로 의미가 없고,
        // 새 라운드 준비 시 유치장/자물쇠가 스스로 초기화된다.
        Despawn();
    }

    // 침입 이동 종료 — 도착이면 자물쇠를 열고 수감자를 방출, 경로 실패면 불발로 정리한다.
    private void HandleIntrudeFinished(NpcController npc, bool reached)
    {
        if (npc != m_intruder)
            return;

        m_intruder.OnIntrudeFinished -= HandleIntrudeFinished;

        if (!reached)
        {
            Debug.Log("[돌발이벤트] 범인 탈출 — 침입 경로 실패, 불발 정리");
            Despawn();
            return;
        }

        m_jailLock.ServerUnlock();
        ReleaseAllInmates();

        m_unlocked = true;
        m_despawnTime = Time.time + m_intruderLingerSeconds;
    }

    // 수감자를 전원 방출한다 — 자물쇠가 열린 순간 모두 뛰쳐나간다.
    private void ReleaseAllInmates()
    {
        // Inmates는 JailZone 내부 HashSet의 뷰라, ReleaseInmate로 수정하며 순회하면 열거 예외가 난다 — 스냅샷 후 처리
        m_releaseBuffer.Clear();
        foreach (NpcController inmate in m_jailZone.Inmates)
        {
            if (inmate != null)
                m_releaseBuffer.Add(inmate);
        }

        for (int i = 0; i < m_releaseBuffer.Count; i++)
            ReleaseInmate(m_releaseBuffer[i]);

        Debug.Log($"[돌발이벤트] 범인 탈출 — 수감자 {m_releaseBuffer.Count}명 방출");
    }

    private void ReleaseInmate(NpcController inmate)
    {
        m_jailZone.ReleaseInmate(inmate);

        // 재검거의 핵심 — 판정 완료 표식을 지워야 인계존이 다시 판정한다 (#230)
        inmate.ClearDelivered();

        // 진범만 할당량·수배 후처리를 되돌린다. 경범죄(난동꾼)는 할당량·수배 대상이 아니므로 건드리지 않는다
        // (난동꾼은 CitizenIdentity.IsCriminal 대조를 타지 않는다 — MisdemeanorOffender).
        // 신원은 서버 전용 값이라 서버(또는 오프라인)에서만 도는 이 경로에서 안전하게 읽는다.
        CitizenIdentity identity = inmate.GetComponent<CitizenIdentity>();
        if (identity != null && identity.IsCriminal)
        {
            if (Round != null)
                Round.ReportCriminalEscaped();
            if (WantedList != null)
                WantedList.ReinstateByNpcId(inmate.NetworkObjectId);
        }

        // 유치장을 뛰쳐나와 도주한다 — 침입자를 위협으로 삼아 반대로 달아난 뒤 배회로 섞여 든다.
        // 본부가 무인이라(발동 조건) 근처에 플레이어가 없으면 도주 상태가 곧 배회로 복귀한다(NpcFleeState).
        inmate.StartFlee(m_intruder != null ? m_intruder.transform : null);
    }

    private void Despawn()
    {
        if (m_intruder == null)
            return;

        // HandleIntrudeFinished가 이미 해제했더라도 -=는 중복 호출이 안전하다(미구독 시 무동작)
        m_intruder.OnIntrudeFinished -= HandleIntrudeFinished;
        SuddenEventUtil.DespawnOrDestroy(m_intruder.gameObject);
        m_intruder = null;
        m_unlocked = false;
    }
}
