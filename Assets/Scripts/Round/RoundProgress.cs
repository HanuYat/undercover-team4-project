using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 세션 내 라운드 진행도(#377) — 지금이 몇 번째 라운드인지. 난이도 레버(할당량)가 이 값을 타고 오른다.
/// RoundManager는 게임 씬 소속이라 라운드마다 새로 생기고 죽는다 — 라운드를 세는 주체는 씬을 넘어 살아야 한다.
/// TeamFund와 같은 상주 홀더 구조다 (#214 §6): 세션 시작 시 서버가 1회 스폰(destroyWithScene:false)하고,
/// 값은 NetworkVariable이라 클라이언트도 읽는다(할당량 표시가 모든 피어에서 같아진다).
///
/// 증가 시점은 <b>라운드 종료(성공)</b>다 — 상점으로 돌아가기 직전에 다음 라운드 번호가 확정된다(RoundEndResetter).
/// 라운드 시작 시점에 올리지 않는 이유: 그러면 게임 씬이 이미 뜬 뒤에 값이 바뀌어, 복제가 도착하기 전에
/// 할당량을 읽은 클라이언트가 이전 라운드 수치를 잠깐 표시하게 된다. 종료 시점에 올리면 다음 게임 씬이
/// 로드될 때 이미 복제가 끝나 있다.
///
/// 실패는 판의 끝이라 1라운드로 되돌린다 — 팀 자금(TeamFund.ResetToStarting)과 같은 이유·같은 자리에서 한다.
/// 오프라인 단독 Play에는 이 상주 오브젝트가 없다 — 사용처는 null이면 1라운드로 취급할 것.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class RoundProgress : NetworkedManagerBase
{
    /// <summary>첫 라운드 번호 — 진행도는 1부터 센다. 표 조회(RoundQuotaTable)의 기준점이기도 하다.</summary>
    public const int k_firstRound = 1;

    private readonly NetworkVariable<int> m_round = new(k_firstRound);

    /// <summary>현재 라운드 번호(1부터). 세션 중에는 클라이언트에서도 읽을 수 있다.</summary>
    public int Current => m_round.Value;

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        // 세션 시작 시 1회 초기화
        m_round.Value = k_firstRound;
    }

    /// <summary>다음 라운드로 넘긴다 — 라운드 성공 종료 시 RoundEndResetter가 호출한다. 서버(또는 오프라인) 전용.</summary>
    public void Advance()
    {
        if (!IsServer)
        {
            Debug.LogWarning("RoundProgress.Advance는 서버에서만", this);
            return;
        }

        m_round.Value++;
        Debug.Log($"[라운드 진행도] 다음 라운드 — {m_round.Value}라운드");
    }

    /// <summary>진행도를 첫 라운드로 되돌린다 — 라운드 실패로 판이 끝났을 때. 서버(또는 오프라인) 전용.</summary>
    public void ResetToFirst()
    {
        if (!IsServer)
        {
            Debug.LogWarning("RoundProgress.ResetToFirst는 서버에서만", this);
            return;
        }

        int before = m_round.Value;
        m_round.Value = k_firstRound;
        Debug.Log($"[라운드 진행도] 라운드 실패로 초기화 — {before} → {m_round.Value}라운드");
    }
}
