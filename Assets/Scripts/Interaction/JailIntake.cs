using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 유치장 출입구 — <b>누가 유치장에 들어왔고, 어디 앉히고, 언제 내보내나</b>를 담당한다. (#492)
///
/// <see cref="JailZone"/>과 역할이 갈린다: 저쪽은 대장(수용 인원·현상금·정산 레코드·좌석 소유),
/// 이쪽은 출입구다. 한 클래스에 두면 정산 책임과 출입 책임이 섞이고 크기도 감당이 안 된다.
///
/// 서버(또는 오프라인)에서 <see cref="m_checkInterval"/>마다 훑으며 규칙 두 개를 집행한다:
///
///  <b>R1 판정</b> — 확보된 신병(Escorted/Captured)이 Jail 영역에 들어서면 그 순간 판정한다.
///                   오검거를 좌석까지 끌고 가야 알게 되는 헛수고를 없앤다(팀 확정 2026-08-03).
///  <b>R2 착석</b> — 판정에서 수감 대상으로 확정된 대상이 Jail 영역 안에서 Captured가 되면
///                   (= 플레이어가 E로 놓으면) 가장 가까운 빈 좌석을 배정하고 계상한다.
///                   좌석까지 걸어가 앉는 것은 NpcJailedState가 한다.
///
/// <b>R1을 먼저 돌리는 것이 강제다.</b> 반출 직후 플레이어가 유치장 안에서 바로 E를 눌러 되돌리는
/// 경우, 같은 틱에 재판정(R1)과 착석(R2)이 함께 일어나야 한 프레임 늦게 앉는 것을 피할 수 있다.
///
/// 판정과 계상이 분리돼 있다 — 문턱만 넘고 안 앉히면 <b>0원</b>이다. 이것이 "직접 넣게 만든다"의
/// 실질적 강제력이고, GDD 9-2의 "이송 중 라운드 종료 시 보상 없음"과도 정확히 맞는다.
///
/// 장소 오브젝트라 App 파사드에 등록하지 않는다 — JailLock·JailZone과 같은 관례로 씬 탐색을 쓴다.
/// </summary>
public class JailIntake : MonoBehaviour
{
    [Header("유치장 (비우면 같은 오브젝트·부모에서 자동 탐색)")]
    [SerializeField] private JailZone m_jailZone;

    [Header("출입 검사")]
    [Tooltip("검사 주기(초) — 매 프레임 돌 필요가 없다. 0이면 매 프레임 검사한다 (JailDoor와 같은 관례)")]
    [SerializeField] private float m_checkInterval = 0.1f;

    // 다음 검사까지 남은 시간
    private float m_cooldown;

    // 판정에서 수감 대상으로 확정된 대상과 그 보상액 — R2가 여기 있는 대상만 앉힌다.
    // 보상액을 함께 들고 있는 이유: Admit이 착석 시점이라 판정 결과를 그때까지 보관해야 한다.
    // 서버(또는 오프라인) 전용.
    private readonly Dictionary<NpcController, int> m_pendingSeat = new Dictionary<NpcController, int>();

    // 판정 자체가 불가능했던 대상(신원·경범죄 마커 둘 다 없음) — 매 틱 재시도하면 경고가 폭주한다.
    // ArrestJudge가 이 경우 MarkDelivered를 부르지 않아 IsDelivered로는 걸러지지 않는다.
    private readonly HashSet<NpcController> m_unjudgeable = new HashSet<NpcController>();

    // Jail 통행을 내준 대상 — 유치장을 벗어나면 회수한다. 서버(또는 오프라인) 전용.
    private readonly HashSet<NpcController> m_jailAccessGranted = new HashSet<NpcController>();

    // 파괴된 대상 정리용 임시 버퍼 — 매 틱 새로 할당하지 않게 재사용한다
    private readonly List<NpcController> m_deadBuffer = new List<NpcController>();

    private void Awake()
    {
        // 유치장은 같은 오브젝트에 두는 것이 기본 — 인스펙터로 따로 지정할 수도 있다
        if (m_jailZone == null)
            m_jailZone = GetComponentInParent<JailZone>();

        if (m_jailZone == null)
            Debug.LogWarning("JailIntake: 유치장(JailZone)을 찾지 못했다 — 수용이 동작하지 않는다", this);
    }

    // 판정·좌석 배정은 서버 권위 — NetworkBehaviour가 아니므로 직접 게이트한다 (CustodyRouter와 같은 패턴)
    private static bool HasServerAuthority =>
        NetworkManager.Singleton == null
        || !NetworkManager.Singleton.IsListening
        || NetworkManager.Singleton.IsServer;

    private void Update()
    {
        if (!HasServerAuthority)
            return;

        m_cooldown -= Time.deltaTime;
        if (m_cooldown > 0f)
            return;
        m_cooldown = m_checkInterval;

        PruneDestroyed();

        // 검사 주기로 호출을 눌러 두었기에 목록 훑기로 충분하다 (JailDoor와 같은 판단)
        NpcController[] npcs = FindObjectsByType<NpcController>(FindObjectsSortMode.None);

        // 통행이 먼저다 — R1/R2보다 앞서야 판정·착석이 유효한 바닥 위에서 일어난다
        for (int i = 0; i < npcs.Length; i++)
            TickJailAccess(npcs[i]);

        // 순서 강제 — R1이 R2보다 먼저다 (클래스 주석 참고)
        for (int i = 0; i < npcs.Length; i++)
            TryJudgeOnEntry(npcs[i]);

        for (int i = 0; i < npcs.Length; i++)
            TrySeat(npcs[i]);
    }

    /// <summary>
    /// Jail 영역 통행 관리 — <b>확보된 신병</b>에게 유치장 통행을 내주고, 신병에서 풀려 밖에 있으면
    /// 회수한다. (#492)
    ///
    /// 기준이 <b>신병 여부</b>인 이유는 두 진입 방식이 서로 다른 이동을 쓰기 때문이다:
    ///
    ///  · <b>끌고 들어갈 때</b>는 밧줄이 위치를 직접 대입하므로(에이전트가 꺼져 있다) NavMesh 영역을
    ///    타지 않는다. 대신 놓는 순간 에이전트가 켜지며 <c>Warp</c>로 재부착되는데, 그 부착 지점을
    ///    <b>에이전트 자신의 areaMask 안에서</b> 찾는다(NpcController.StopRopeDrag). 시민 마스크는
    ///    Jail이 빠져 있어(#415) 좌석 18개 전부가 최대 1.36m 바깥으로 스냅된다 — 실측값이다.
    ///  · <b>반출된 수감자가 따라올 때</b>는 밧줄이 없어 제 발로 NavMesh를 걷는다
    ///    (NpcEscortedState.SetDestination). 통행이 없으면 유치장으로 가는 경로가 문턱에서 끊겨
    ///    <b>다시 넣을 수 없다.</b>
    ///
    /// 위치로만 판단하면 두 번째가 막힌다: 밖에 있다고 회수해 버리면, 들어가야 통행을 얻고
    /// 통행이 있어야 들어가는 교착이 된다. 그래서 신병(Escorted/Captured/Jailed)이면 위치와 무관하게
    /// 내준다 — "경찰이 확보한 대상은 유치장에 들어갈 수 있다"가 규칙이고, 배회 시민은 여전히 못 얻는다.
    ///
    /// 회수는 <b>신병에서 풀렸고 + 밖에 있을 때만</b> 한다. 안에 선 채로 회수하면 자기가 딛고 선
    /// 폴리곤이 금지돼 경로가 아예 안 잡히고 그 자리에 굳는다 — 오검거로 판정돼 원한 구역으로
    /// 걸어 나가는 시민이 정확히 이 경우다(판정 순간 Detained라 신병에서 빠지지만 아직 유치장 안이다).
    /// </summary>
    private void TickJailAccess(NpcController npc)
    {
        if (npc == null)
            return;

        NpcState state = npc.CurrentState;
        bool inCustody =
            state == NpcState.Escorted || state == NpcState.Captured || state == NpcState.Jailed;

        // 신병이거나, 신병이 아니어도 이미 유치장 안이면 내준다(안에 선 대상은 그 폴리곤을 딛어야 한다)
        if (inCustody || JailArea.Contains(npc.transform.position))
        {
            if (m_jailAccessGranted.Add(npc))
                npc.SetJailAccess(true);
            return;
        }

        // 신병에서 풀렸고 밖으로 나갔다 — 회수해 "시민은 유치장에 못 들어간다"(#415)를 되돌린다.
        if (m_jailAccessGranted.Remove(npc))
            npc.SetJailAccess(false);
    }

    // 파괴된 대상을 걷어낸다 — 라운드 종료 잔류 정리(MisdemeanorLoiterer)로 NPC가 사라져도
    // 키가 남아 목록이 라운드마다 자란다. 위 두 규칙은 살아 있는 NPC를 훑어 도므로 스스로 지우지 못한다.
    private void PruneDestroyed()
    {
        m_deadBuffer.Clear();

        // 지우면서 돌 수 없으니 키를 먼저 모은다 — Unity의 가짜 null 비교로 파괴 여부를 본다
        foreach (NpcController npc in m_pendingSeat.Keys)
            if (npc == null)
                m_deadBuffer.Add(npc);

        for (int i = 0; i < m_deadBuffer.Count; i++)
            m_pendingSeat.Remove(m_deadBuffer[i]);

        m_unjudgeable.RemoveWhere(npc => npc == null);
        m_jailAccessGranted.RemoveWhere(npc => npc == null);
    }

    // R1 — 확보된 신병이 유치장에 들어선 순간 판정한다.
    private void TryJudgeOnEntry(NpcController npc)
    {
        if (npc == null || npc.IsDelivered || m_unjudgeable.Contains(npc))
            return;

        // 확보된 신병만 — 끌려오는 중(Escorted)과 내려놓은 대상(Captured) 둘 다 통과한다.
        // 배회 시민은 애초에 Jail 영역에 못 들어오지만(NavMesh 게이팅) 상태로도 한 번 더 막는다.
        if (npc.CurrentState != NpcState.Escorted && npc.CurrentState != NpcState.Captured)
            return;

        if (!JailArea.Contains(npc.transform.position))
            return;

        ArrestJudge judge = App.Game.ArrestJudge;
        if (judge == null)
        {
            Debug.LogWarning("JailIntake: ArrestJudge가 없어 판정할 수 없다", this);
            return;
        }

        ArrestResult? result = judge.Judge(npc);
        if (result == null)
        {
            // 신원도 경범죄 마커도 없는 대상 — 다시 물어도 답이 같으므로 한 번만 시도한다
            m_unjudgeable.Add(npc);
            return;
        }

        // 오검거는 WrongfulArrestPenalty가 Detained로 가져간다 — 앉힐 대상이 아니다
        if (result.Value.Verdict != ArrestVerdict.WrongfulArrest)
            m_pendingSeat[npc] = result.Value.Reward;
    }

    // R2 — 수감 대상이 유치장 안에서 멈추면(플레이어가 E로 놓으면) 좌석을 배정하고 계상한다.
    private void TrySeat(NpcController npc)
    {
        if (npc == null || m_jailZone == null)
            return;

        int bounty;
        if (!m_pendingSeat.TryGetValue(npc, out bounty))
            return;

        // 끌려가는 중에는 앉히지 않는다 — 놓아야(Captured) 앉는다
        if (npc.CurrentState != NpcState.Captured)
            return;

        if (!JailArea.Contains(npc.transform.position))
            return;

        // 놓은 자리에서 가장 가까운 빈 좌석 — 여기서 좌석까지는 NpcJailedState가 걸어간다(1.5~5.6m)
        Transform seat = m_jailZone.ReserveSeat(npc, npc.transform.position);
        npc.SendToJail(seat);

        // 계상은 착석 시점 (#492) — 판정만 받고 안 앉히면 0원이다
        m_jailZone.Admit(npc, bounty);
        m_pendingSeat.Remove(npc);
    }

    /// <summary>
    /// 반출 — 앉은 수감자를 일으켜 플레이어를 따라오게 한다. 서버(또는 오프라인) 전용. (#492)
    ///
    /// 밧줄을 걸지 않는다: <see cref="NpcEscortedState"/>가 밧줄 이전의 추종·근접 정지(#97)를 그대로
    /// 들고 있고 <c>IsRoped</c>일 때만 건너뛰므로, <c>StartRopeDrag</c> 없이 <c>StartEscort</c>만 부르면
    /// 추종·속도 부스트·거리 이탈이 전부 동작한다.
    ///
    /// 좌석 반납과 정산 제외가 함께 일어난다 — "끝까지 데리고 있어야 인정"(GDD 9-2)이 그대로 유지된다.
    /// </summary>
    public void ServerExtract(NpcController npc, Transform follower)
    {
        if (!HasServerAuthority)
            return;

        if (npc == null || follower == null || m_jailZone == null)
            return;

        if (npc.CurrentState != NpcState.Jailed)
            return;

        m_jailZone.ReleaseInmate(npc); // 좌석 반납 + 정산·진행도에서 제외

        // 수감 대상 기록도 지운다 — 남겨두면 재판정 결과가 오검거로 바뀌어도 R2가 옛 기록을 보고 앉힌다
        m_pendingSeat.Remove(npc);

        // 다시 넣으면 재판정 (#358) — 탈옥 방출(JailbreakEvent)이 같은 호출을 하는 것과 같은 이유다
        npc.ClearDelivered();

        // 앉은 자세를 전이보다 먼저 푼다 — 뒤에 두면 일어서는 순간이 한두 프레임 앉은 채로 보인다 (#462)
        npc.SetSeated(false);
        npc.StartEscort(follower);

        Debug.Log($"[유치장] 반출 — 따라오게 한다: {npc.name}");
    }
}
