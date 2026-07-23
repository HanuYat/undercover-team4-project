using UnityEngine;

/// <summary>
/// 잔류 경범죄자 관리 (#310 후속) — 돌발 이벤트가 손을 뗀 NPC(제압 실패로 이탈했거나 소란이 끝난
/// 난동꾼·침입자)를 도심에 남겨 두고 뒷일을 맡는다. NPC는 배회 시민처럼 섞여 들지만
/// <see cref="MisdemeanorOffender"/> 마커가 남아 있어 언제든 제압·연행해 인계하면 경범죄 수익이 난다 —
/// "아까 놓친 그 놈"을 다시 잡는 재미(GDD 6-5의 재발견 철학)가 이 컴포넌트의 존재 이유다.
///
/// 맡는 뒷일은 둘뿐이다:
///  · 인계 판정 — 판정되면 임시 거처로 걸어가게 하고, 도착하면 잔상을 남기고 정리한다
///    (원 소유 이벤트의 판정 경로와 동일 — 판정 후에도 남겨 두면 같은 NPC로 수익이 반복된다).
///  · 라운드 종료 — 남아 있으면 연출 없이 일괄 정리한다 (스폰물 누수 방지의 최종 안전망).
///
/// 이벤트가 추적을 끊는 시점에 서버(또는 오프라인)에서만 AddComponent로 붙는다 —
/// 마커(MisdemeanorOffender)와 같은 이유로 복제가 필요 없는 plain MonoBehaviour다.
/// 이벤트는 이 시점부터 비활성(IsActive=false)이 되어 같은 종류의 새 이벤트가 다시 추첨될 수 있다.
/// </summary>
public class MisdemeanorLoiterer : MonoBehaviour
{
    private RoundManager Round => App.Game.Round;

    private NpcController m_npc;
    private ArrestJudge m_judge;
    private Transform m_holdingPoint;
    private string m_displayName;
    private bool m_despawnQueued; // 홀딩 도착 통보(FSM 틱 안)에서 즉시 파괴하지 않기 위한 지연 플래그

    /// <summary>
    /// 이벤트가 손을 떼는 NPC에 관리자를 붙인다 — 판정 구독과 임시 거처 배선을 이벤트에게서 물려받는다.
    /// 서버(또는 오프라인) 전용. 이미 붙어 있으면(재이탈 등) 기존 것을 재사용한다.
    /// </summary>
    public static void Attach(NpcController npc, ArrestJudge judge, Transform holdingPoint, string displayName)
    {
        if (npc == null)
            return;

        MisdemeanorLoiterer loiterer = npc.GetComponent<MisdemeanorLoiterer>();
        if (loiterer == null)
            loiterer = npc.gameObject.AddComponent<MisdemeanorLoiterer>();

        loiterer.m_judge = judge;
        loiterer.m_holdingPoint = holdingPoint;
        loiterer.m_displayName = displayName;
        loiterer.Subscribe();
    }

    private void Awake()
    {
        m_npc = GetComponent<NpcController>();
    }

    private void OnDestroy()
    {
        if (m_judge != null)
            m_judge.OnArrestJudged -= HandleArrestJudged;
        if (m_npc != null)
            m_npc.OnReachedHolding -= HandleReachedHolding;
    }

    // Attach 시점에 호출 — OnDestroy와 짝. 중복 구독을 피하려고 항상 빼고 다시 건다(-=는 미구독에 무해)
    private void Subscribe()
    {
        if (m_judge != null)
        {
            m_judge.OnArrestJudged -= HandleArrestJudged;
            m_judge.OnArrestJudged += HandleArrestJudged;
        }
        if (m_npc != null)
        {
            m_npc.OnReachedHolding -= HandleReachedHolding;
            m_npc.OnReachedHolding += HandleReachedHolding;
        }
    }

    private void Update()
    {
        // 홀딩 도착분을 다음 프레임에 정리한다 — 발행 체인·FSM 틱 안 즉시 파괴 금지 (이벤트들의 지연 despawn과 동일)
        if (m_despawnQueued)
        {
            Despawn(playVfx: true);
            return;
        }

        // 라운드가 끝나면 남은 잔류자를 정리한다 — 이벤트에서 떨어져 나온 스폰물의 최종 안전망.
        // 일괄 정리라 소멸 연출은 끈다 (이벤트 ServerReset과 같은 이유)
        if (Round != null && Round.Phase != RoundPhase.InProgress)
            Despawn(playVfx: false);
    }

    // 인계 판정 수신 — 임시 거처로 이송해 도착 시 정리한다 (SpawnedNpcEvent.HandleArrestJudged와 동일 경로)
    private void HandleArrestJudged(ArrestResult result)
    {
        if (m_npc == null || result.Npc != m_npc)
            return;

        if (m_holdingPoint != null)
        {
            Debug.Log($"[돌발이벤트] {m_displayName}(잔류) — 경범죄 판정, 임시 거처로 이송");
            m_npc.SendToHolding(m_holdingPoint);
            return;
        }

        Debug.Log($"[돌발이벤트] {m_displayName}(잔류) — 경범죄 판정 완료, 정리 예약");
        m_despawnQueued = true;
    }

    private void HandleReachedHolding(NpcController npc)
    {
        if (npc != m_npc)
            return;

        Debug.Log($"[돌발이벤트] {m_displayName}(잔류) — 임시 거처 도착, 정리 예약");
        m_despawnQueued = true;
    }

    private void Despawn(bool playVfx)
    {
        // 연행 중이던 플레이어가 파괴된 참조를 쥐지 않게 먼저 놓게 한다 (이벤트 Despawn들과 동일)
        PlayerEscorter escorter = PlayerEscorter.FindEscorterOf(m_npc);
        if (escorter != null)
            escorter.Release();

        SuddenEventUtil.DespawnOrDestroy(gameObject, playVfx);
    }
}
