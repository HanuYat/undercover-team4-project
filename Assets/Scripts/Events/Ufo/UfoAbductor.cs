using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// UFO 빔 흡입 판정 (#819) — <see cref="UfoCraft"/>와 같은 오브젝트에 붙는 서버 권위 판정부.
/// 빔 안에 <b>일정 시간 머무른</b> 현장 플레이어를 기체까지 빨아올려 라운드에서 지운다.
///
/// <b>돌발 이벤트가 아니라 상주 기믹이다</b> (팀 확정 2026-08-25) — 기체가 늘 떠 있고 빔도 늘 켜져
/// 있으므로 발생·종료가 없다. 대신 <b>라운드 진행 중에만 판정한다</b>: 준비·정산 국면에 걸리면
/// 아무것도 못 하는 시간에 라운드가 끝나 버린다.
///
/// <b>납치(#371/#775)와 같은 결말, 다른 규칙이다.</b> 결말은 그쪽과 같게 맞췄다 — 몸이 회수 불가능한
/// 곳으로 사라지고 <see cref="PlayerIncapacitation.ServerKillByBodyLost"/>로 기능 정지가 확정된다.
/// 다른 것은 <b>트리거와 구조 창</b>이다:
/// <list type="bullet">
/// <item><b>트리거는 장소지 사람이 아니다</b> — 빔은 기체를 따라 무작위로 흘러 다닌다. 누구를
/// 노리지 않으므로 뭉쳐 다녀도 안전하지 않고, 반대로 한참 아무도 안 걸리기도 한다.</item>
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
/// "이 앵커를 따라가라"고 지시하고, 서버는 그동안 기체를 제자리에 세워 둔다
/// (<see cref="UfoCraft.ServerSetHold"/>).
/// </summary>
[RequireComponent(typeof(UfoCraft))]
public class UfoAbductor : MonoBehaviour
{
    [Header("빔 판정")]
    [Tooltip("빔 안에 이만큼(초) 머무르면 빨려 올라간다")]
    [Min(0.1f)]
    [SerializeField] private float m_captureSeconds = 1.5f; // 3초는 일부러 서 있지 않으면 안 걸릴 만큼 관대했다 (팀 피드백)

    [Tooltip("빔에서 벗어났을 때 누적이 식는 속도 배율 — 1이면 머문 만큼 그대로 되돌아간다. 클수록 도망이 쉽다")]
    [Min(0f)]
    [SerializeField] private float m_dwellDecayScale = 1.5f;

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

    private UfoCraft m_craft;
    private NetworkObject m_anchor;
    private Transform m_victim;
    private float m_liftDeadline;

    // 빔 안에 머무른 시간 — 벗어나면 식는다. 0까지 식은 항목은 버린다.
    private readonly Dictionary<Transform, float> m_dwell = new Dictionary<Transform, float>();
    private readonly List<Transform> m_inBeam = new List<Transform>();
    private readonly List<Transform> m_cooled = new List<Transform>();

    private RoundManager Round => App.Game.Round;

    // 씬 배치물이라 자기 Update가 스스로 돈다 — 서버 권한을 직접 게이트한다 (AbductionEvent와 같은 패턴)
    private static bool HasServerAuthority =>
        NetworkManager.Singleton == null
        || !NetworkManager.Singleton.IsListening
        || NetworkManager.Singleton.IsServer;

    private void Awake()
    {
        m_craft = GetComponent<UfoCraft>();
        m_anchor = GetComponent<NetworkObject>();
    }

    private void Update()
    {
        if (!HasServerAuthority)
            return;

        // 라운드 진행 중에만 판정한다 — 준비·정산 국면에 걸리면 아무것도 못 하는 시간에 라운드가 끝난다.
        // 이미 빨려 올라가는 중이면 그것은 끝을 봐야 한다: 여기서 손을 놓으면 몸이 굳은 채 남는다.
        if (Round != null && Round.Phase != RoundPhase.InProgress)
        {
            ReleaseVictim();
            m_dwell.Clear();
            m_craft.ServerSetHold(false);
            return;
        }

        // 흡입 중에도 체류 판정은 계속 돈다 — "벗어나면 식는다"가 이 구간에서만 멈추면,
        // 다른 사람이 빔 밖으로 걸어 나가 안전한 채로 있어도 누적이 그대로 보존된다.
        // 다만 새로 잡힌 사람을 곧장 태우지는 않는다 — 한 번에 한 명만 끌어올린다.
        Transform caught = TickDwell(Time.deltaTime);

        if (m_victim != null)
            TickLifting();
        else if (caught != null)
            BeginLifting(caught);

        // 빨아올리는 동안에만 기체를 세운다. 매 프레임 현재 상태로 다시 거는 이유는
        // 흡입이 끝나는 길이 여럿(완료·중단·소실)이라, 한 곳에서 풀면 빠뜨린 길이 생기기 때문이다.
        // 누적이 차는 중에는 세우지 않는다 — 빔이 다가오는 것을 보고 피하는 것이 이 기믹이다.
        m_craft.ServerSetHold(m_victim != null);
    }

    private void OnDisable()
    {
        // 라운드 종료·씬 전환 등으로 기체가 꺼지면 빨아올리던 몸을 풀어 준다 —
        // 흡입은 되돌릴 수 없다는 규칙은 플레이 중의 이야기다. 굳은 채 남으면 상점에 못 들어간다.
        if (HasServerAuthority)
            ReleaseVictim();
    }

    /// <summary>
    /// 빔 안의 체류 시간을 갱신하고, 임계를 넘은 사람을 돌려준다 — 없으면 null.
    /// <b>벗어난 사람도 지우는 것이 아니라 식힌다</b>: 스쳐 지나가는 것만으로 초기화되면 빔 가장자리에서
    /// 들락거리는 것이 최적 행동이 되고, 반대로 영영 남으면 한참 뒤 다시 밟았을 때 즉사한다.
    /// </summary>
    private Transform TickDwell(float deltaTime)
    {
        SuddenEventUtil.CollectFieldPlayers(m_craft.BeamGroundPoint(), m_craft.BeamRadius, m_inBeam);

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
        m_liftDeadline = Time.time + m_liftTimeoutSeconds;
        m_dwell.Remove(victim);

        if (incap != null)
            incap.Incapacitate(IncapacitationCause.Beamed);

        // 오너가 기체를 따라 올라온다 — 속도 상한을 넘겨 서버 값대로 떠오르게 한다
        PlayerPenaltyView view = victim.GetComponent<PlayerPenaltyView>();
        if (view != null && m_anchor != null)
            view.StartTowedBy(m_anchor, m_liftSpeed);

        Debug.Log($"[UFO] 흡입 시작 — {victim.name}");
    }

    private void TickLifting()
    {
        // 대상이 사라졌다(접속 종료 등)
        if (m_victim == null)
            return;

        // 빨아올리는 동안 다른 사유로 쓰러졌다 — 흡입은 접는다. 몸은 그 자리에 그대로 둔다
        // (폭탄 사망이면 운반해 부활시킬 몸이다 — 납치의 HandleVictimCauseChanged와 같은 판단).
        PlayerIncapacitation incap = m_victim.GetComponent<PlayerIncapacitation>();
        if (incap != null && incap.Cause != IncapacitationCause.Beamed)
        {
            Debug.Log($"[UFO] 흡입 중단 — {m_victim.name}이 흡입 밖의 사유로 쓰러졌다 ({incap.Cause})");
            StopTow(m_victim);
            m_victim = null;
            return;
        }

        bool swallowed =
            (m_victim.position - transform.position).sqrMagnitude <= m_swallowDistance * m_swallowDistance;

        if (swallowed)
        {
            Swallow(m_victim);
            m_victim = null;
            return;
        }

        // 상한을 두는 이유는 앵커를 놓친 경우다 — 오너 RPC 유실·배선 누락·지형에 낀 경우가 전부
        // 여기로 떨어진다. <b>닿지 못했다면 풀어 준다</b> — 죽인 것으로 치면 네트워크·배선 실패의
        // 대가가 화면에 아무 일도 없이 15초 뒤 라운드 아웃으로 나타난다.
        if (Time.time < m_liftDeadline)
            return;

        Debug.Log($"[UFO] 흡입 타임아웃 — {m_victim.name}에 닿지 못해 풀어준다");
        ReleaseVictim();
    }

    /// <summary>
    /// 흡입 완료 — 라운드 아웃을 확정한다 (팀 확정 2026-08-25, 납치와 같은 결말).
    ///
    /// 시점은 기체 위치 근처로 뺀다 — 1인칭 그대로 기체 안으로 들어가면 화면이 기체 내부로
    /// 덮이는 것은 납치의 하강과 같은 이유로 피해야 하지만, 지상으로 빼면 방금 하늘로 빨려
    /// 올라간 시점이 갑자기 바닥으로 뚝 떨어져 보여 어색했다(팀 피드백) — 잡혀간 방향과 맞게
    /// 기체 쪽에 남긴다. 몸이 회수 불가능하다는 표시(<c>BodyLost</c>)까지 그쪽 경로가 함께 들고,
    /// <see cref="PlayerRagdoll"/>이 그 표시를 보고 몸을 감춘다 — 상공에 시체가 걸리지 않게.
    /// </summary>
    private void Swallow(Transform victim)
    {
        PlayerPenaltyView view = victim.GetComponent<PlayerPenaltyView>();
        if (view != null)
            view.SetSpectatePivot(transform.position);

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
}
