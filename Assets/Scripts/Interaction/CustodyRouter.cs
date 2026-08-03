using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 신병 처리 — 인계존 판정(ArrestJudge) 결과에 따라 NPC의 행선지를 정한다. (GDD 7-2, #228)
/// 판정 구역과 관리 구역을 잇는 얇은 라우터다:
///
/// - 현상수배범·경범죄 → <b>여기서는 아무것도 하지 않는다</b>. 수용은 플레이어가 직접 끌고 들어가
///   좌석에 놓을 때 JailIntake가 배정·계상한다 (#492 — 자동 이송 폐기)
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

    private void Awake()
    {
        if (m_arrestJudge == null)
            m_arrestJudge = FindFirstObjectByType<ArrestJudge>();
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
            // 오검거 신병은 오검거 페널티(#277)가 원한 구역 수용까지 책임진다 — 카운트·수용·출동이
            // 한 구독자 안에서 순서 보장되어야 해서(같은 이벤트의 구독자 간 순서는 보장이 없다) 여기서 넘긴다.
            // 페널티 매니저가 없는 씬(단독 테스트 등)에서만 기존대로 석방한다 — Captured로 방치되지 않게.
            if (App.Game.WrongfulArrestPenalty != null)
                return;

            Debug.Log($"[신병 처리] 오검거 석방(페널티 매니저 없음) — 수갑 해제: {npc.name}");
            npc.ReleaseFromCustody();
            return;
        }

        // 현상수배범·경범죄의 수용은 여기서 하지 않는다 (#492) — 플레이어가 직접 끌고 들어가
        // 좌석에 놓을 때 JailIntake가 좌석을 배정하고 그 시점에 계상한다.
        // 판정만 나고 앉히지 않으면 0원이다 — 그것이 "직접 넣게 만든다"의 강제력이다.
        //
        // 경범죄도 수감하는 결정(2026-07-23)으로 #299의 '유치장은 진범 전용' 규칙은 폐기됐다:
        // 반복 수익은 ArrestJudge가 첫 판정 후 마커 보상을 0으로 만들어 막는다.
    }
}
