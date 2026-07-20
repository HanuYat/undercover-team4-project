using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 신병 처리 — 인계존 판정(ArrestJudge) 결과에 따라 NPC의 행선지를 정한다. (GDD 7-2, #228)
/// 판정 구역과 관리 구역을 잇는 얇은 라우터다:
///
/// - 현상수배범·경범죄 → 유치장(JailZone)으로 이송·수용
/// - 오검거(무고한 시민) → 수갑 해제 후 석방(배회 복귀)
///
/// ArrestJudge는 "누가 범인인가"만, JailZone은 "어디에 가두는가"만 안다 — 그 사이는 여기가 안다.
/// 판정이 서버(또는 오프라인)에서만 발행되므로(ArrestJudge) 이 라우팅도 같은 권위에서만 일어난다.
///
/// 오검거 카운트·광장 매달기 페널티는 여기서 다루지 않는다 — 같은 ArrestJudge.OnArrestJudged를
/// 구독하는 오검거 페널티(#101)가 담당한다.
/// </summary>
public class CustodyRouter : MonoBehaviour
{
    [Header("판정 (비우면 씬에서 자동 탐색)")]
    [SerializeField]
    private ArrestJudge m_arrestJudge;

    [Header("유치장 (비우면 씬에서 자동 탐색)")]
    [SerializeField]
    private JailZone m_jailZone;

    private void Awake()
    {
        if (m_arrestJudge == null)
            m_arrestJudge = FindFirstObjectByType<ArrestJudge>();

        if (m_jailZone == null)
            m_jailZone = FindFirstObjectByType<JailZone>();
    }

    private void OnEnable()
    {
        if (m_arrestJudge != null)
            m_arrestJudge.OnArrestJudged += HandleArrestJudged;
    }

    private void OnDisable()
    {
        if (m_arrestJudge != null)
            m_arrestJudge.OnArrestJudged -= HandleArrestJudged;
    }

    private void HandleArrestJudged(ArrestResult result)
    {
        NpcController npc = result.Npc;
        if (npc == null)
            return;

        // 판정은 서버(또는 오프라인)에서만 발행되지만, 상태 전이를 부르기 전 방어적으로 한 번 더 게이트한다
        if (
            NetworkManager.Singleton != null
            && NetworkManager.Singleton.IsListening
            && !NetworkManager.Singleton.IsServer
        )
            return;

        if (result.Verdict == ArrestVerdict.WrongfulArrest)
        {
            // 무고한 시민 — 수갑을 풀어 돌려보낸다 (GDD 7-3: 오검거 기록은 #101이 따로 센다)
            Debug.Log($"[신병 처리] 오검거 석방 — 수갑 해제: {npc.name}");
            npc.ReleaseFromCustody();
            return;
        }

        // 현상수배범·경범죄 — 유치장으로 이송한다
        if (m_jailZone == null)
        {
            Debug.LogWarning(
                $"CustodyRouter: 유치장(JailZone)이 없음 — 수감 불가: {npc.name}",
                this
            );
            return;
        }

        // 도착 시점에 수용 인원을 세도록 1회성 구독을 걸어 둔다 (판정 시점이 아니라 걸어 들어온 시점)
        npc.OnJailed += HandleNpcJailed;
        npc.SendToJail(m_jailZone.ReserveCell());
    }

    private void HandleNpcJailed(NpcController npc)
    {
        npc.OnJailed -= HandleNpcJailed;
        m_jailZone.Admit(npc);
    }
}
