using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// UFO 흡입 (#819) — 상공의 UFO가 지면에 빔을 비추고, <b>그 안에 일정 시간 머무른</b> 현장 플레이어를
/// 빨아올려 라운드에서 지운다. (GDD 6-4)
///
/// <b>납치(#371/#775)와 같은 결말, 다른 규칙이다.</b> 결말은 그쪽과 같게 맞췄다(팀 확정 2026-08-25) —
/// 몸이 회수 불가능한 곳으로 사라지고 <see cref="PlayerIncapacitation.ServerKillByBodyLost"/>로 기능 정지가
/// 확정된다. 다른 것은 <b>트리거와 구조 창</b>이다:
/// <list type="bullet">
/// <item><b>트리거는 장소지 사람이 아니다</b> — 빔은 무작위 지점을 비춘다. 누구를 노리지 않으므로
/// 뭉쳐 다녀도 안전하지 않고, 반대로 아무도 안 걸리는 판도 나온다. 그것이 이 이벤트의 성격이다.</item>
/// <item><b>구조 창은 걸어 나오는 것 그 자체다</b> — 빔 안에 있는 동안 누적이 차고, 벗어나면 식는다
/// (<see cref="m_dwellDecayScale"/>). 동료가 떼어내 주는 납치와 달리 본인이 피하는 것이 전부이므로,
/// 일단 떠오르기 시작하면 되돌릴 수 없다(납치의 하강 구간과 같은 규칙).</item>
/// </list>
///
/// <b>대상은 플레이어뿐이다</b> (팀 확정 2026-08-25). 시민까지 빨아올리면 그림은 살지만 진범이
/// 빨려 올라가 라운드가 성립하지 않는 판을 따로 막아야 한다.
///
/// <b>빨아올리는 일은 오너가 한다.</b> 서버는 플레이어 좌표를 직접 밀 수 없으므로(이동 권한이 오너에게
/// 있다) 오검거 호송·납치와 같은 경로를 탄다 — <see cref="PlayerPenaltyView.StartTowedBy"/>가 오너에게
/// "이 앵커를 따라가라"고 지시하고, 서버는 앵커(= UFO)를 올리기만 한다.
///
/// <b>이벤트 컴포넌트는 매니저와 같은 오브젝트에 둔다</b> — <see cref="SuddenEventManager"/>가 자식을
/// 훑지 않는다. 다른 이벤트와 같은 관례다.
/// </summary>
[RequireComponent(typeof(SuddenEventManager))]
public class UfoAbductionEvent : MonoBehaviour, ISuddenEvent
{
    [Header("표시")]
    [SerializeField] private string m_displayName = "UFO";

    [Header("기체")]
    [Tooltip("스폰할 UFO 프리팹 — NetworkObject + NetworkTransform + UfoCraft가 붙어 있어야 한다. 비우면 발동하지 않는다")]
    [SerializeField] private UfoCraft m_ufoPrefab;

    [Tooltip("빔 지점 위로 이만큼(m) 떠서 난다")]
    [Min(1f)]
    [SerializeField] private float m_altitude = 25f;

    [Header("빔 지점")]
    [Tooltip("빔을 비출 후보 지점 — 무작위로 고른다. 지점의 높이가 곧 빔이 닿는 지면이다. 비우면 발동하지 않는다")]
    [SerializeField] private Transform[] m_beamPoints;

    [Header("빔")]
    [Tooltip("빔 반경(m) — 이 안에 서 있으면 누적이 찬다")]
    [Min(0.5f)]
    [SerializeField] private float m_beamRadius = 3f;

    [Tooltip("빔 안에 이만큼(초) 머무르면 빨려 올라간다")]
    [Min(0.1f)]
    [SerializeField] private float m_captureSeconds = 2.5f;

    [Tooltip("빔에서 벗어났을 때 누적이 식는 속도 배율 — 1이면 머문 만큼 그대로 되돌아간다. 클수록 도망이 쉽다")]
    [Min(0f)]
    [SerializeField] private float m_dwellDecayScale = 1.5f;

    [Tooltip("한 지점에 빔을 유지하는 시간(초) — 아무도 안 걸리면 접고 다음 지점으로 옮긴다")]
    [Min(0.5f)]
    [SerializeField] private float m_beamHoldSeconds = 8f;

    [Header("흡입")]
    [Tooltip("빨려 올라가는 속도(m/s) — 오너의 추종 이동에 걸리는 상한이다")]
    [Min(0.5f)]
    [SerializeField] private float m_liftSpeed = 4f;

    [Tooltip("기체까지 이 거리(m) 안으로 들어오면 흡입 완료로 본다")]
    [Min(0.5f)]
    [SerializeField] private float m_swallowDistance = 2.5f;

    [Tooltip("빨려 올라가다 이 시간(초)이 지나도 못 닿으면 완료로 친다 — 앵커를 놓쳐 영영 떠 있지 않게")]
    [Min(1f)]
    [SerializeField] private float m_liftTimeoutSeconds = 15f;

    [Header("수명")]
    [Tooltip("이 시간(초)이 지나면 UFO가 떠난다. 흡입 중이면 그것을 끝내고 떠난다")]
    [Min(5f)]
    [SerializeField] private float m_maxLifetimeSeconds = 90f;

    [Tooltip("떠날 때 이만큼(m) 더 올라간 뒤 사라진다")]
    [Min(1f)]
    [SerializeField] private float m_departAltitude = 60f;

    private enum EPhase
    {
        Roaming, // 다음 빔 지점으로 이동 중
        Beaming, // 빔을 켜고 누적을 잰다
        Lifting, // 걸린 사람을 빨아올리는 중 — 되돌릴 수 없다
        Leaving, // 수명이 다해 올라가며 사라지는 중
    }

    private UfoCraft m_craft;
    private EPhase m_phase;
    private float m_deadline;      // 수명 마감 시각
    private float m_phaseDeadline; // 지금 국면의 마감 시각 (빔 유지·흡입 상한)
    private Transform m_beamPoint; // 지금 비추는 지점 — 빔이 닿는 지면 높이의 출처
    private Transform m_victim;

    // 빔 안에 머무른 시간 — 벗어나면 식는다. 0까지 식은 항목은 버린다.
    private readonly Dictionary<Transform, float> m_dwell = new Dictionary<Transform, float>();
    private readonly List<Transform> m_inBeam = new List<Transform>();
    private readonly List<Transform> m_cooled = new List<Transform>();

    public string DisplayName => m_displayName;

    public bool IsActive => m_craft != null;

    // 하늘에 뜨는 물건이라 어차피 보인다 — 알림을 참는 이유가 없다.
    public bool AnnounceOnBegin => true;

    // 문구 키는 아직 없다 — 비워 두면 매니저가 DisplayName을 일반 포맷에 끼워 넣는다.
    // 전용 키를 만들 때 여기에 적을 것 (조사 문제는 ISuddenEvent 주석 참고).
    public string NoticeKey => null;

    public bool CanTrigger() => m_ufoPrefab != null
        && HasBeamPoint()
        && SuddenEventUtil.FindRandomFieldPlayer() != null;

    public void ServerBegin()
    {
        if (m_ufoPrefab == null || !HasBeamPoint())
        {
            Debug.LogWarning($"{nameof(UfoAbductionEvent)}: 기체 프리팹 또는 빔 지점이 배선되지 않아 발동하지 않는다 (#819)", this);
            return;
        }

        Transform point = PickBeamPoint();
        if (point == null)
            return;

        // 첫 지점 위에 바로 세우지 않고 떠날 고도에서 내려온다 — 눈앞에 솟아나는 그림을 피한다
        Vector3 entry = point.position + Vector3.up * m_departAltitude;
        m_craft = Instantiate(m_ufoPrefab, entry, Quaternion.identity);

        if (SuddenEventUtil.IsNetworkSessionActive)
            m_craft.GetComponent<NetworkObject>().Spawn();

        m_beamPoint = point;
        m_craft.ServerFlyTo(point.position + Vector3.up * m_altitude);

        m_phase = EPhase.Roaming;
        m_deadline = Time.time + m_maxLifetimeSeconds;
        m_dwell.Clear();

        Debug.Log($"[UFO] 발동 — 첫 빔 지점 {point.name}");
    }

    public void ServerTick()
    {
        if (m_craft == null)
        {
            Finish(); // 기체가 사라졌다(씬 전환 등) — 상태만 정리한다
            return;
        }

        switch (m_phase)
        {
            case EPhase.Roaming:
                TickRoaming();
                break;

            case EPhase.Beaming:
                TickBeaming();
                break;

            case EPhase.Lifting:
                TickLifting();
                break;

            case EPhase.Leaving:
                TickLeaving();
                break;
        }
    }

    public void ServerReset()
    {
        // 라운드 종료 등 강제 정리 — 빨아올리던 사람을 그 자리에 풀어 준다.
        // 흡입은 되돌릴 수 없다는 규칙은 <b>플레이 중</b>의 이야기다: 라운드가 끝났는데 몸이 굳은 채로
        // 남으면 상점에 못 들어간다. 납치의 ServerReset과 같은 판단이다.
        ReleaseVictim();

        if (m_craft != null)
            SuddenEventUtil.DespawnOrDestroy(m_craft.gameObject, playVfx: false);

        m_craft = null;
        Finish();
    }

    // ---- 국면 ----

    private void TickRoaming()
    {
        if (Time.time >= m_deadline)
        {
            BeginLeaving();
            return;
        }

        if (!m_craft.HasArrived)
            return;

        m_craft.ServerHold();
        m_craft.ServerSetBeam(true);
        m_dwell.Clear(); // 지점을 옮겼으면 앞 지점의 누적은 남기지 않는다
        m_phase = EPhase.Beaming;
        m_phaseDeadline = Time.time + m_beamHoldSeconds;
    }

    private void TickBeaming()
    {
        Transform caught = TickDwell(Time.deltaTime);
        if (caught != null)
        {
            BeginLifting(caught);
            return;
        }

        if (Time.time < m_phaseDeadline)
            return;

        // 아무도 안 걸렸다 — 빔을 접고 다음 지점으로. 수명이 다했으면 그대로 떠난다.
        m_craft.ServerSetBeam(false);

        if (Time.time >= m_deadline)
        {
            BeginLeaving();
            return;
        }

        Transform next = PickBeamPoint();
        if (next == null)
        {
            BeginLeaving();
            return;
        }

        m_beamPoint = next;
        m_craft.ServerFlyTo(next.position + Vector3.up * m_altitude);
        m_phase = EPhase.Roaming;
    }

    /// <summary>
    /// 빔 안의 체류 시간을 갱신하고, 임계를 넘은 사람을 돌려준다 — 없으면 null.
    /// <b>벗어난 사람도 지운 것이 아니라 식힌다</b>: 스쳐 지나가는 것만으로 초기화되면 빔 가장자리에서
    /// 들락거리는 것이 최적 행동이 되고, 반대로 영영 남으면 한참 뒤 다시 밟았을 때 즉사한다.
    /// </summary>
    private Transform TickDwell(float deltaTime)
    {
        Vector3 ground = m_craft.BeamGroundPoint(m_beamPoint != null ? m_beamPoint.position.y : m_craft.transform.position.y);
        SuddenEventUtil.CollectFieldPlayers(ground, m_beamRadius, m_inBeam);

        Transform caught = null;
        for (int i = 0; i < m_inBeam.Count; i++)
        {
            Transform player = m_inBeam[i];
            m_dwell.TryGetValue(player, out float held);
            held += deltaTime;
            m_dwell[player] = held;

            if (held >= m_captureSeconds && caught == null)
                caught = player;
        }

        // 빔 밖으로 나간 사람은 식힌다 — 다 식으면 목록에서 뺀다
        m_cooled.Clear();
        foreach (KeyValuePair<Transform, float> entry in m_dwell)
        {
            if (entry.Key == null || !m_inBeam.Contains(entry.Key))
                m_cooled.Add(entry.Key);
        }

        for (int i = 0; i < m_cooled.Count; i++)
        {
            Transform player = m_cooled[i];
            if (player == null)
            {
                m_dwell.Remove(player);
                continue;
            }

            float cooled = m_dwell[player] - deltaTime * m_dwellDecayScale;
            if (cooled <= 0f)
                m_dwell.Remove(player);
            else
                m_dwell[player] = cooled;
        }

        return caught;
    }

    private void BeginLifting(Transform victim)
    {
        PlayerIncapacitation incap = victim.GetComponent<PlayerIncapacitation>();

        // 이미 무력화된 몸은 접수하지 않는다 — 원인을 가리지 않는다. 근거는 납치와 같다:
        // 무력화 원인이 하나뿐이라 덮어쓰면 오검거 호송·납치 중인 몸을 가로채 두 결말이 한 몸을 다툰다.
        if (incap != null && incap.IsIncapacitated)
        {
            m_dwell.Remove(victim); // 누적을 비워 다음 사람에게 순서를 넘긴다
            return;
        }

        m_victim = victim;
        m_phase = EPhase.Lifting;
        m_phaseDeadline = Time.time + m_liftTimeoutSeconds;

        if (incap != null)
            incap.Incapacitate(IncapacitationCause.Beamed);

        // 오너가 기체를 따라 올라온다 — 속도 상한을 넘겨 서버 값대로 떠오르게 한다
        PlayerPenaltyView view = victim.GetComponent<PlayerPenaltyView>();
        if (view != null && m_craft != null)
            view.StartTowedBy(m_craft.GetComponent<NetworkObject>(), m_liftSpeed);

        Debug.Log($"[UFO] 흡입 시작 — {victim.name}");
    }

    private void TickLifting()
    {
        // 대상이 사라졌다(접속 종료 등) — 기체만 남기고 국면을 되돌린다
        if (m_victim == null)
        {
            EndLifting();
            return;
        }

        // 빨아올리는 동안 다른 사유로 쓰러졌다 — 흡입은 접는다. 몸은 그 자리에 그대로 둔다
        // (폭탄 사망이면 운반해 부활시킬 몸이다 — 납치의 HandleVictimCauseChanged와 같은 판단).
        PlayerIncapacitation incap = m_victim.GetComponent<PlayerIncapacitation>();
        if (incap != null && incap.Cause != IncapacitationCause.Beamed)
        {
            Debug.Log($"[UFO] 흡입 중단 — {m_victim.name}이 흡입 밖의 사유로 쓰러졌다 ({incap.Cause})");
            StopTow(m_victim);
            m_victim = null;
            EndLifting();
            return;
        }

        float reached = (m_victim.position - m_craft.transform.position).sqrMagnitude;
        bool swallowed = reached <= m_swallowDistance * m_swallowDistance;

        // 상한을 두는 이유는 앵커를 놓친 경우다 — 오너가 추종을 못 걸면 영영 닿지 않는다
        if (!swallowed && Time.time < m_phaseDeadline)
            return;

        Swallow(m_victim);
        m_victim = null;
        EndLifting();
    }

    /// <summary>
    /// 흡입 완료 — 라운드 아웃을 확정한다 (팀 확정 2026-08-25, 납치와 같은 결말).
    ///
    /// 시점을 먼저 지상으로 빼는 것은 납치의 하강과 같은 이유다 — 1인칭 그대로 기체 안으로 들어가면
    /// 화면이 접시 내부로 덮인다. 몸이 회수 불가능하다는 표시(<c>BodyLost</c>)까지 그쪽 경로가 함께 든다.
    /// </summary>
    private void Swallow(Transform victim)
    {
        PlayerPenaltyView view = victim.GetComponent<PlayerPenaltyView>();
        if (view != null && m_beamPoint != null)
            view.SetSpectatePivot(m_beamPoint.position);

        PlayerIncapacitation incap = victim.GetComponent<PlayerIncapacitation>();
        if (incap != null)
        {
            incap.ServerKillByBodyLost();

            // 체력도 0으로 내린다 — 때린 적이 없어 HP가 가득한 채였고, 그러면 "기능 정지"인데 체력바는
            // 100인 어긋남이 남는다. Die를 먼저 걸어야 이 0이 다운을 다시 걸지 않는다 (납치와 같은 순서).
            PlayerHealth health = victim.GetComponent<PlayerHealth>();
            if (health != null && health.CurrentHp > 0)
                health.ModifyHp(-health.CurrentHp);
        }

        StopTow(victim);
        Debug.Log($"[UFO] 흡입 완료 — {victim.name}이 실려 갔다");
    }

    // 한 번 빨아올린 뒤에는 그 지점에 머물 이유가 없다 — 빔을 끄고 떠난다.
    private void EndLifting()
    {
        m_dwell.Clear();
        BeginLeaving();
    }

    private void BeginLeaving()
    {
        if (m_craft == null)
        {
            Finish();
            return;
        }

        m_craft.ServerSetBeam(false);
        m_craft.ServerFlyTo(m_craft.transform.position + Vector3.up * m_departAltitude);
        m_phase = EPhase.Leaving;

        Debug.Log("[UFO] 이탈 — 상공으로 떠난다");
    }

    private void TickLeaving()
    {
        if (!m_craft.HasArrived)
            return;

        SuddenEventUtil.DespawnOrDestroy(m_craft.gameObject, playVfx: false);
        m_craft = null;
        Finish();
    }

    // ---- 보조 ----

    // 흡입이 확정되기 전에 정리되면 몸을 풀어 준다 — 그대로 두면 라운드가 끝날 때까지 굳는다
    private void ReleaseVictim()
    {
        if (m_victim == null)
            return;

        Transform victim = m_victim;
        m_victim = null;

        StopTow(victim);

        PlayerIncapacitation incap = victim.GetComponent<PlayerIncapacitation>();
        if (incap != null && incap.Cause == IncapacitationCause.Beamed)
            incap.Recover();
    }

    private static void StopTow(Transform victim)
    {
        PlayerPenaltyView view = victim != null ? victim.GetComponent<PlayerPenaltyView>() : null;
        if (view != null)
            view.StopCarried();
    }

    private void Finish()
    {
        m_phase = EPhase.Roaming;
        m_beamPoint = null;
        m_victim = null;
        m_dwell.Clear();
    }

    private bool HasBeamPoint()
    {
        if (m_beamPoints == null)
            return false;

        for (int i = 0; i < m_beamPoints.Length; i++)
            if (m_beamPoints[i] != null)
                return true;

        return false;
    }

    // 미배선 칸이 섞여 있어도 배선된 것 중에서만 고른다 — 인스펙터에서 배열 크기만 늘려 둔 상태가 흔하다.
    // 지금 비추는 지점은 후보에서 뺀다: 같은 자리에 다시 켜면 옮겨 다니는 그림이 되지 않는다.
    private Transform PickBeamPoint()
    {
        Transform picked = null;
        int seen = 0;

        for (int i = 0; i < m_beamPoints.Length; i++)
        {
            Transform candidate = m_beamPoints[i];
            if (candidate == null || candidate == m_beamPoint)
                continue;

            seen++;
            if (Random.Range(0, seen) == 0)
                picked = candidate;
        }

        // 배선된 지점이 하나뿐이면 그 자리를 다시 쓴다 — 후보가 없다고 이벤트를 접을 이유는 없다
        return picked != null ? picked : m_beamPoint;
    }
}
