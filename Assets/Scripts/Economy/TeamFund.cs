using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 팀 공용 자금(#104, GDD 9-1/9-2) — 검거 보상을 서버 권위로 합산해 아이템 구매에 쓰는 팀 자산.
/// 자금은 0 밑으로 내려가지 않는다(GDD 9-2). 값은 NetworkVariable로 전 클라이언트에 동기화되며,
/// 정산 화면(#107)·상점(#182)이 <see cref="Fund"/>를 구독·조회한다.
///
/// 배정·판정이 서버 권위이므로(#56 패턴) 자금 변경도 서버(또는 오프라인 호스트)에서만 한다.
/// 상점의 구매 차감은 이 컴포넌트의 <see cref="TrySpend"/>를 서버(구매 ServerRpc, #182)에서 호출한다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class TeamFund : NetworkBehaviour
{
    [Header("검거 판정 (비우면 씬에서 자동 탐색)")]
    [SerializeField] private ArrestJudge m_arrestJudge;

    [Tooltip("라운드 시작(서버 스폰) 시 초기 자금. 밸런싱 보류 항목(GDD 12장)이라 인스펙터에 둔다")]
    [Min(0)]
    [SerializeField] private int m_startingFund = 0;

    // 서버만 쓰기(기본 쓰기 권한 = Server), 전 클라이언트 읽기. UI는 Fund.OnValueChanged로 갱신을 받는다.
    private readonly NetworkVariable<int> m_fund = new NetworkVariable<int>();

    /// <summary>동기화된 팀 자금 — 정산 UI(#107)·상점(#182)이 구독(OnValueChanged)·조회(Value)한다. 서버 외에는 읽기 전용.</summary>
    public NetworkVariable<int> Fund => m_fund;

    /// <summary>현재 팀 자금 잔액 — 조회 편의용. 쓰기는 서버 전용 API로만.</summary>
    public int Balance => m_fund.Value;

    private void Awake()
    {
        if (m_arrestJudge == null)
            m_arrestJudge = FindFirstObjectByType<ArrestJudge>();
    }

    public override void OnNetworkSpawn()
    {
        // 서버만 자금을 채우고 지운다 — 판정이 서버 권위이므로 (#56 패턴)
        if (!IsServer)
            return;

        // 재시작(Shutdown 후 StartHost) 시 씬 NetworkObject의 값에는 이전 세션 자금이 남는다 —
        // 서버가 새로 뜨면 항상 초기값으로 시작한다. (WantedListManager의 Clear와 같은 이유, #209)
        m_fund.Value = m_startingFund;

        if (m_arrestJudge != null)
            m_arrestJudge.OnArrestJudged += HandleArrestJudged;
        else
            Debug.LogWarning("TeamFund: ArrestJudge를 찾지 못해 검거 보상을 합산할 수 없다", this);
    }

    public override void OnNetworkDespawn()
    {
        if (m_arrestJudge != null)
            m_arrestJudge.OnArrestJudged -= HandleArrestJudged;
    }

    // 검거 판정 수신 — 판정이 정한 보상을 자금에 가산한다. (서버 전용, ArrestJudge)
    // 수배범(10000)·경범죄(이벤트 지정액)는 양수, 오검거는 0이라 자금에 영향이 없다 (GDD 9-2, ArrestResult.Reward).
    private void HandleArrestJudged(ArrestResult result)
    {
        if (result.Reward == 0)
            return;

        m_fund.Value = Mathf.Max(0, m_fund.Value + result.Reward);
        Debug.Log($"[팀 자금] 보상 +{result.Reward} → 잔액 {m_fund.Value}");
    }

    /// <summary>
    /// 자금을 차감한다 — 상점 구매(#182)가 서버(구매 ServerRpc)에서 호출한다.
    /// 잔액이 부족하면 차감하지 않고 false를 반환한다(자금은 0 밑으로 내려가지 않음, GDD 9-2).
    /// </summary>
    /// <returns>차감 성공 여부.</returns>
    public bool TrySpend(int cost)
    {
        if (!IsServer)
        {
            Debug.LogWarning("TeamFund.TrySpend는 서버에서만 호출해야 한다 (구매 ServerRpc 경유)", this);
            return false;
        }
        if (cost < 0)
            return false;
        if (m_fund.Value < cost)
            return false;

        m_fund.Value -= cost;
        Debug.Log($"[팀 자금] 차감 -{cost} → 잔액 {m_fund.Value}");
        return true;
    }
}
