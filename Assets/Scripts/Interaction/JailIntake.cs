using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 감옥 출입구 — <b>누가 감옥에 들어가고, 어디 세우고, 언제 나오나</b>를 담당한다. (#492/#537)
///
/// <see cref="JailZone"/>과 역할이 갈린다: 저쪽은 대장(수용 인원·현상금·정산 레코드·배치 지점 소유),
/// 이쪽은 출입구다. 한 클래스에 두면 정산 책임과 출입 책임이 섞이고 크기도 감당이 안 된다.
///
/// <b>폴링이 사라졌다</b> (#537). 예전에는 매 틱 NPC 전부를 훑으며 세 규칙(게이트 판정 → Jail 통행 →
/// 착석)을 집행했다. 감옥이 격리 공간이 되면서 셋 다 근거를 잃었다:
///
///  · <b>게이트 판정</b> → 문 앞 E가 트리거다. 명시적 입력이라 "언제 판정됐는지"를 폴링으로 추적할
///    이유가 없고, 통과당 1회 중복 가드(<c>m_judgedThisPass</c>)도 함께 사라진다.
///  · <b>Jail NavMesh 통행</b> → 감옥이 도시와 이어지지 않은 별도 NavMesh 섬이라 걸어 들어올 경로
///    자체가 없다. 영역 마스크로 시민을 막을 일이 없어졌다 (#415가 하던 일).
///  · <b>착석</b> → 판정과 동시에 순간이동해 배치된다. "문턱은 넘었는데 안 앉힌 상태"라는 중간이 없다.
///
/// 그래서 이 클래스는 이제 <b>서버에서 불릴 때만 도는 서비스</b>다 — 문(<see cref="JailDoor"/>)과
/// 반출 요청(<see cref="PlayerEscortCommands"/>)이 호출한다.
///
/// <b>계상 시점이 판정 즉시로 앞당겨졌다.</b> 예전에는 "문턱만 넘고 안 앉히면 0원"이 직접 넣게 만드는
/// 강제력이었는데(#492), 앉히는 단계가 없어져 그 창이 닫혔다. 강제력은 <b>문 앞까지 끌고 오는 것</b>으로
/// 줄어들고, GDD 9-2의 "이송 중 라운드 종료 시 보상 없음"은 그대로 성립한다 — 문에 닿기 전에 라운드가
/// 끝나면 여전히 0원이다.
///
/// 장소 오브젝트라 App 파사드에 등록하지 않는다 — JailLock·JailZone과 같은 관례로 씬 탐색을 쓴다.
/// </summary>
public class JailIntake : MonoBehaviour
{
    [Header("감옥 (비우면 같은 오브젝트·부모에서 자동 탐색)")]
    [SerializeField] private JailZone m_jailZone;

    [Tooltip(
        "문 앞 E가 신병으로 인정하는 거리(m) — 밧줄로 끌고 있지 않아도 이 안에 놓아둔 Captured면 함께 판정한다. "
            + "밧줄 길이(1.6m)보다 넉넉히 둘 것"
    )]
    [SerializeField] private float m_admitReach = 4f;

    // 판정 대상을 모으는 임시 버퍼 — E 입력마다 새로 할당하지 않게 재사용한다. 서버(또는 오프라인) 전용.
    private readonly List<NpcController> m_admitBuffer = new List<NpcController>();

    // 판정 자체가 불가능했던 대상(신원·경범죄 마커 둘 다 없음) — 누를 때마다 경고가 폭주하지 않게 기억한다.
    private readonly HashSet<NpcController> m_unjudgeable = new HashSet<NpcController>();

    /// <summary>감옥 — 문이 배치 지점·퇴장 지점을 물어볼 때 쓴다. 미배선이면 null.</summary>
    public JailZone Zone => m_jailZone;

    private void Awake()
    {
        // 감옥은 같은 오브젝트에 두는 것이 기본 — 인스펙터로 따로 지정할 수도 있다
        if (m_jailZone == null)
            m_jailZone = GetComponentInParent<JailZone>();

        if (m_jailZone == null)
            Debug.LogWarning("JailIntake: 감옥(JailZone)을 찾지 못했다 — 수용이 동작하지 않는다", this);
    }

    // 판정·배치는 서버 권위 — NetworkBehaviour가 아니므로 직접 게이트한다 (CustodyRouter와 같은 패턴)
    private static bool HasServerAuthority =>
        NetworkManager.Singleton == null
        || !NetworkManager.Singleton.IsListening
        || NetworkManager.Singleton.IsServer;

    // ---- 수감 (문 앞 E) ----

    /// <summary>
    /// 수감 버튼 처리 — <paramref name="interactor"/>가 확보 중인 신병을 <b>줄을 걷고 일으켜 세운 뒤</b>
    /// 판정하고, 범죄자만 감옥 안으로 순간이동시켜 계상한다. 서버(또는 오프라인) 전용. (#537)
    ///
    /// 돌려주는 값은 <b>확보한 대상 수</b>이지 수감된 수가 아니다 — 판정이 일어나기 뒤에 나오므로
    /// 이 시점에는 결과를 모른다. 부르는 쪽(<see cref="JailIntakeButton"/>)이 "확보한 신병이 없다"만
    /// 가르는 데 쓴다.
    ///
    /// <b>확보의 기준은 둘이다</b> — 이 사람의 밧줄에 걸린 대상 전부, 그리고 버튼 앞
    /// <see cref="m_admitReach"/> 안에 놓아둔 <see cref="NpcState.Captured"/>. 후자를 넣는 이유는
    /// 밧줄을 풀어 문 앞에 세워 둔 뒤 누르는 조작이 자연스럽기 때문이고, 여럿을 끌고 왔으면
    /// <b>한 번에 전부 판정된다</b>(팀 확정 2026-08-06).
    ///
    /// 오검거는 감옥에 들이지 않고 그 자리에서 놓는다 — <see cref="WrongfulArrestPenalty"/>가
    /// Detained로 가져가 페널티를 굴린다(#101/#277). 감옥이 격리 공간이 된 뒤로는 "안에서 확정된
    /// 오검거가 통행을 잃고 굳는다"는 옛 사고(#492)가 애초에 생길 수 없다.
    /// </summary>
    public int ServerAdmitHeldBy(GameObject interactor)
    {
        if (!HasServerAuthority || interactor == null || m_jailZone == null)
            return 0;

        CollectHeldBy(interactor);
        if (m_admitBuffer.Count == 0)
            return 0;

        if (App.Game.ArrestJudge == null)
        {
            Debug.LogWarning("JailIntake: ArrestJudge가 없어 판정할 수 없다", this);
            return 0;
        }

        ulong presserId = ClientIdOf(interactor);

        // 확보한 수를 먼저 센다 — 판정은 일어나기(약 0.6초) 뒤에 나므로 결과를 기다려 셀 수 없다.
        // 버튼이 이 수로 "확보한 신병이 없다"만 가르면 되고, 판정 결과는 각자 배너로 나간다.
        int held = m_admitBuffer.Count;

        for (int i = 0; i < held; i++)
        {
            NpcController npc = m_admitBuffer[i];
            if (npc == null || m_unjudgeable.Contains(npc))
                continue;

            // 인계자는 <b>지금</b> 잡아 둔다 — 아래에서 줄을 빼고 나면 누가 끌고 있었는지 알 수 없다.
            ulong[] deliverers = CollectDeliverers(npc, presserId);

            // 줄을 걷고 일어난 <b>뒤에</b> 판정한다 — 누운 몸이 그대로 감옥으로 순간이동하거나
            // 누운 채 석방되는 그림을 없앤다. 묶인 적 없는 대상은 곧바로 실행된다.
            PlayerEscorter.ReleaseAllTethersOn(npc, () => ServerResolveVerdict(npc, deliverers));
        }

        m_admitBuffer.Clear();
        return held;
    }

    // 일어난 뒤의 판정·행선지 — 서버(또는 오프라인) 전용.
    private void ServerResolveVerdict(NpcController npc, ulong[] deliverers)
    {
        if (npc == null || m_jailZone == null)
            return;

        ArrestJudge judge = App.Game.ArrestJudge;
        if (judge == null)
            return;

        ArrestResult? result = judge.Judge(npc);
        if (result == null)
        {
            // 신원도 경범죄 마커도 없는 대상 — 다시 물어도 답이 같으므로 한 번만 시도한다
            m_unjudgeable.Add(npc);
            return;
        }

        // 오검거 — 감옥에 들어가지 않는다. 줄은 이미 풀렸고 몸도 일어난 상태이므로 여기서는
        // 표식만 걷고 그 자리에 둔다. 행선지(원한 구역 수용)는 WrongfulArrestPenalty가 같은
        // 판정 이벤트를 받아 정하고, 그 매니저가 없는 씬에서는 CustodyRouter가 배회로 돌려보낸다.
        if (result.Value.Verdict == ArrestVerdict.WrongfulArrest)
        {
            npc.SetJailExtracted(false); // 반출했던 대상이 오검거로 뒤집힌 경우 표식을 걷어낸다 (#517)
            Debug.Log($"[감옥] 오검거 — 감옥에 들이지 않고 문 앞에서 놓는다: {npc.name}");
            return;
        }

        ServerPlaceInJail(npc, result.Value.Reward, deliverers);
    }

    /// <summary>
    /// 대상을 감옥 안 배치 지점으로 순간이동시키고 계상한다 — 서버(또는 오프라인) 전용. (#537)
    /// 밧줄은 이 앞 단계에서 이미 걷혔고(<see cref="PlayerEscorter.ReleaseAllTethersOn"/>) 몸도 서 있다.
    /// </summary>
    private void ServerPlaceInJail(NpcController npc, int bounty, ulong[] deliverers)
    {
        Transform spot = m_jailZone.ReservePlacement(npc);
        npc.SendToJail(spot);
        m_jailZone.Admit(npc, bounty, deliverers);

        Debug.Log($"[감옥] 수감 — {npc.name}을(를) {spot.name}에 배치했다 (현상금 {bounty}원)");
    }

    // 이 플레이어가 확보 중인 대상을 모은다 — 자기 밧줄에 걸린 전부 + 문 앞에 놓아둔 Captured.
    private void CollectHeldBy(GameObject interactor)
    {
        m_admitBuffer.Clear();

        PlayerEscorter escorter = interactor.GetComponent<PlayerEscorter>();
        if (escorter != null)
        {
            for (int i = 0; i < escorter.TetheredCount; i++)
            {
                NpcController roped = escorter.GetTetheredNpc(i);
                if (roped != null && !m_admitBuffer.Contains(roped))
                    m_admitBuffer.Add(roped);
            }
        }

        // 문 앞에 놓아둔 신병 — 기준은 <b>문</b>이 아니라 누른 사람이다. 문에서 재려면 문이 여럿일 때
        // 어느 문인지를 또 물어야 하는데, 사거리는 이미 PlayerInteractor가 걸러 줬으므로 사람 기준이면 충분하다.
        Vector3 origin = interactor.transform.position;
        float sqrReach = m_admitReach * m_admitReach;

        NpcController[] npcs = FindObjectsByType<NpcController>(FindObjectsSortMode.None);
        for (int i = 0; i < npcs.Length; i++)
        {
            NpcController npc = npcs[i];
            if (npc == null || npc.CurrentState != NpcState.Captured)
                continue;
            if ((npc.transform.position - origin).sqrMagnitude > sqrReach)
                continue;
            if (!m_admitBuffer.Contains(npc))
                m_admitBuffer.Add(npc);
        }
    }

    // 인계 몫(#484)의 귀속자 — <b>밧줄이 걸려 있던 사람 전원 + E를 누른 사람</b>. (팀 확정 2026-08-06)
    //
    // 예전에는 게이트 판정부터 착석까지 밧줄 보유자를 누적했다. 그 창은 "판정과 착석이 다른 시점"이라
    // 존재했던 것이고, 둘이 한 순간이 된 지금은 누적할 구간이 없다 — 누르는 그 프레임이 전부다.
    // 누른 사람을 함께 넣는 이유: 남이 끌고 온 신병을 문 앞에서 대신 넣어 주는 것도 인계에 든 손이다.
    // 반대로 누른 사람만 세면 끌고 온 사람이 손해를 본다.
    private static ulong[] CollectDeliverers(NpcController npc, ulong presserId)
    {
        var ids = new HashSet<ulong>();

        List<PlayerEscorter> escorters = PlayerEscorter.FindEscortersOf(npc);
        for (int i = 0; i < escorters.Count; i++)
            ids.Add(escorters[i].OwnerClientId);

        ids.Add(presserId);

        var result = new ulong[ids.Count];
        ids.CopyTo(result);
        return result;
    }

    // 누른 사람의 clientId — 오프라인 단독 테스트에서는 0이다(서버 자신).
    private static ulong ClientIdOf(GameObject interactor)
    {
        NetworkObject netObject = interactor.GetComponentInParent<NetworkObject>();
        return netObject != null ? netObject.OwnerClientId : 0UL;
    }

    // ---- 반출 ----

    /// <summary>
    /// 반출 — 배치된 수감자를 플레이어를 따라오게 한다. 서버(또는 오프라인) 전용. (#492/#537)
    ///
    /// 밧줄을 걸지 않는다: <see cref="NpcEscortedState"/>가 밧줄 이전의 추종·근접 정지(#97)를 그대로
    /// 들고 있고 <c>IsRoped</c>일 때만 건너뛰므로, <c>StartRopeDrag</c> 없이 <c>StartEscort</c>만 부르면
    /// 추종·속도 부스트·거리 이탈이 전부 동작한다.
    ///
    /// <b>여기서 감옥 밖으로 나가지는 않는다</b> (#537) — 따라오게만 하고, 실제로 데리고 나오는 것은
    /// 플레이어가 문에 E를 누를 때다(<see cref="ServerExitJail"/>). 배치 반납과 정산 제외는 여기서
    /// 일어나므로 "끝까지 데리고 있어야 인정"(GDD 9-2)은 꺼내는 순간부터 적용된다.
    /// </summary>
    public void ServerExtract(NpcController npc, Transform follower)
    {
        if (!HasServerAuthority)
            return;

        if (npc == null || follower == null || m_jailZone == null)
            return;

        if (npc.CurrentState != NpcState.Jailed)
            return;

        // 수감 자격은 <b>남겨 둔다</b> — 판정 결과(현상금)를 정산 기록에서 꺼내 되살린다. (#517)
        // 감옥 안에서 다시 세우면(E 정지) 문 앞 재판정 없이 그 자리에서 다시 수감돼야 하는데,
        // 지우면 계상할 근거가 없어진다. 읽기는 ReleaseInmate <b>앞</b>이어야 한다 — 저쪽이 레코드를 지운다.
        if (m_jailZone.TryGetBounty(npc, out int bounty))
            m_pendingBounty[npc] = bounty;
        else
            m_pendingBounty.Remove(npc);

        m_jailZone.ReleaseInmate(npc); // 배치 반납 + 정산·진행도에서 제외

        // <b>ClearDelivered는 부르지 않는다 — 반출은 탈옥이 아니다.</b>
        // 재판정은 문 앞에서 다시 E를 누르면 열리고, 여기서 '첫 인계' 표식까지 되돌리면 반출→재수감을
        // 반복해 진범 검거 수(RoundManager.CriminalArrestCount)를 부풀릴 수 있다. 탈옥(JailbreakEvent)이
        // ClearDelivered를 부르는 것은 대상이 실제로 달아나 도시에서 다시 잡아야 하는 진짜 재검거이기
        // 때문이다 (#358) — 플레이어가 스스로 꺼낸 것과는 다르다.

        // 반출 표식 — 거리 이탈로 멈춰도(Captured) 남아, E가 밧줄이 아니라 추종 재개로 가게 한다 (#517).
        npc.SetJailExtracted(true);
        npc.StartEscort(follower);

        Debug.Log($"[감옥] 반출 — 따라오게 한다: {npc.name}");
    }

    // 반출한 대상의 현상금 보관 — 감옥 안에서 다시 세울 때 같은 값으로 계상한다. 서버(또는 오프라인) 전용. (#517)
    private readonly Dictionary<NpcController, int> m_pendingBounty = new Dictionary<NpcController, int>();

    /// <summary>
    /// 감옥 안에서 반출을 되돌린다 — 따라오던 대상을 그 자리에 다시 수감한다. 서버(또는 오프라인) 전용. (#517/#537)
    ///
    /// 예전에는 폴링(R2)이 "Captured + Jail 영역 안 + 사람이 안에 있음"을 보고 알아서 재착석시켰다.
    /// 폴링이 사라져(#537) 되돌리는 순간을 명시적으로 받는다 — 부르는 곳은 추종 정지
    /// (<see cref="PlayerEscortCommands"/>) 하나다. 감옥 밖에서 세운 것은 여기 걸리지 않는다.
    /// </summary>
    public bool ServerReturnToJail(NpcController npc)
    {
        if (!HasServerAuthority || npc == null || m_jailZone == null)
            return false;

        if (!npc.IsJailExtracted || !m_jailZone.ContainsPoint(npc.transform.position))
            return false;

        // 보관해 둔 현상금이 없으면 판정 결과를 잃은 것이다 — 0원으로 넣지 않고 문 앞 재판정에 맡긴다.
        if (!m_pendingBounty.TryGetValue(npc, out int bounty))
            return false;

        m_pendingBounty.Remove(npc);
        ServerPlaceInJail(npc, bounty, System.Array.Empty<ulong>());
        return true;
    }

    // ---- 플레이어 출입 (문 E) ----

    /// <summary>플레이어를 감옥 안 입장 지점으로 옮긴다. 서버(또는 오프라인) 전용. (#537)</summary>
    public void ServerEnterJail(PlayerMovement mover)
    {
        if (!HasServerAuthority || mover == null || m_jailZone == null)
            return;

        Transform entry = m_jailZone.PlayerEntryPoint;
        mover.ServerTeleport(entry.position, entry.rotation);
        Debug.Log($"[감옥] 입장 — {mover.name}");
    }

    /// <summary>
    /// 플레이어를 문 밖 퇴장 지점으로 옮긴다 — <b>따라오던 반출 대상도 함께</b> 나온다. 서버(또는 오프라인). (#537)
    ///
    /// 대상을 먼저 옮긴다: 나중에 옮기면 한두 프레임 동안 추종이 감옥 안에 남은 몸을 문 밖으로 끌려 해
    /// 벽을 향해 달리는 그림이 나온다. 대상은 퇴장 지점 <b>주변</b>에 붙인다(<c>TryWarpNear</c>) —
    /// 플레이어와 같은 좌표에 겹쳐 세우지 않기 위해서다.
    /// </summary>
    public void ServerExitJail(PlayerMovement mover)
    {
        if (!HasServerAuthority || mover == null || m_jailZone == null)
            return;

        Transform exit = m_jailZone.ExitPoint;

        List<NpcController> followers = NpcController.FindFollowersOf(mover.transform);
        for (int i = 0; i < followers.Count; i++)
            followers[i].ServerExitJail(exit);

        mover.ServerTeleport(exit.position, exit.rotation);
        Debug.Log($"[감옥] 퇴장 — {mover.name} (동행 {followers.Count}명)");
    }
}
