using UnityEngine;

/// <summary>
/// 잔류 경범죄자 관리 (#310 후속) — 돌발 이벤트가 손을 뗀 NPC(이탈·소란 종료·판정 인계된 난동꾼·침입자)의
/// 최종 안전망이다. NPC는 배회 시민으로 섞여 들거나 유치장에 수감돼 있지만 <see cref="MisdemeanorOffender"/>
/// 마커가 남아 있어 언제든 제압·연행해 인계하면 경범죄로 판정된다(수익은 첫 판정에만 — ArrestJudge가 비운다).
/// "아까 놓친 그 놈"을 다시 잡는 재미(GDD 6-5의 재발견 철학)가 잔류의 존재 이유다.
///
/// 판정 후 수감(유치장 이송)은 CustodyRouter가, 탈옥 방출·재검거는 기존 흐름이 처리하므로
/// 이 컴포넌트가 맡는 일은 하나뿐이다: <b>라운드가 끝나면 정리한다</b> — 이벤트에서 떨어져 나온
/// 스폰물이 다음 라운드까지 남지 않게 하는 누수 방지 장치.
///
/// 이벤트가 추적을 끊는 시점에 서버(또는 오프라인)에서만 AddComponent로 붙는다 —
/// 마커(MisdemeanorOffender)와 같은 이유로 복제가 필요 없는 plain MonoBehaviour다.
/// 이벤트는 이 시점부터 비활성(IsActive=false)이 되어 같은 종류의 새 이벤트가 다시 추첨될 수 있다.
/// </summary>
public class MisdemeanorLoiterer : MonoBehaviour
{
    private RoundManager Round => App.Game.Round;

    /// <summary>이벤트가 손을 떼는 NPC에 관리자를 붙인다 — 서버(또는 오프라인) 전용. 이미 붙어 있으면 무동작.</summary>
    public static void Attach(NpcController npc, string displayName)
    {
        if (npc == null)
            return;

        if (npc.GetComponent<MisdemeanorLoiterer>() == null)
            npc.gameObject.AddComponent<MisdemeanorLoiterer>();

        Debug.Log($"[돌발이벤트] {displayName} — 이벤트 추적 종료, 도심 잔류");
    }

    private void Update()
    {
        // 라운드가 끝나면 정리한다 — 일괄 정리라 소멸 연출은 끈다 (이벤트 ServerReset과 같은 이유)
        if (Round != null && Round.Phase != RoundPhase.InProgress)
        {
            // 연행 중이던 플레이어가 파괴된 참조를 쥐지 않게 먼저 놓게 한다 (이벤트 Despawn들과 동일)
            PlayerEscorter escorter = PlayerEscorter.FindEscorterOf(GetComponent<NpcController>());
            if (escorter != null)
                escorter.Release();

            SuddenEventUtil.DespawnOrDestroy(gameObject, playVfx: false);
        }
    }
}
