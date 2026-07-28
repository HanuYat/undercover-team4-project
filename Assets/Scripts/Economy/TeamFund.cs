using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 팀 공용 자금(#104) — 세션 내내 유지되는 상주 홀더. (#214 §6 이월 구조)
/// 세션 시작 시 서버가 1회 스폰(destroyWithScene:false)해 씬을 넘어 값이 유지된다.
/// 검거 보상은 판정 즉시가 아니라 라운드 종료 시 유치장 점유로 정산된다(#340) — SettlementController가
/// 종료 시 AddSettlement로 1회 가산한다. 차감은 상점 구매(TrySpend)뿐이다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class TeamFund : NetworkedManagerBase
{
    [Tooltip("세션 시작 시 초기 자금")]
    [Min(0)]
    [SerializeField] private int m_startingFund = 0;

    private readonly NetworkVariable<int> m_fund = new();

    public NetworkVariable<int> Fund => m_fund;
    public int Balance => m_fund.Value; // 팀 자금 잔액 (조회 편의용)

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        // 세션 시작 시 1회 초기화
        m_fund.Value = m_startingFund;
    }

    // 검거 보상은 판정 즉시가 아니라 라운드 종료 시 유치장 점유 기반으로 정산된다(#340) — SettlementController가
    // 종료 시 AddSettlement를 1회 호출한다. 그래서 ArrestJudge.OnArrestJudged 구독은 더 이상 없다
    // (탈옥해 유치장에 없는 대상은 애초에 정산되지 않아 "판정 즉시 지급 + 회수 없음" 불일치가 사라진다).

    /// <summary>
    /// 라운드 종료 정산액을 팀 자금에 1회 가산한다 — SettlementController(서버·오프라인)가 호출한다. (#340)
    /// 유치장 점유 기반 정산이라 음수가 들어올 일은 없지만 방어적으로 0 이하는 무시한다.
    /// </summary>
    public void AddSettlement(int amount)
    {
        if (!IsServer)
        {
            Debug.LogWarning("TeamFund.AddSettlement는 서버에서만", this);
            return;
        }
        if (amount <= 0) return;

        m_fund.Value = Mathf.Max(0, m_fund.Value + amount);
        Debug.Log($"[팀 자금] 라운드 정산 +{amount} → 잔액 {m_fund.Value}");
    }

    /// <summary>
    /// 자금을 세션 시작값으로 되돌린다 — 라운드 실패로 판이 끝났을 때 호출한다 (#395).
    /// TeamFund는 씬을 넘어 유지되는 상주 홀더(#214)라, 실패 후 로비에서 새 판을 시작해도
    /// 스스로는 초기화되지 않는다. 되돌릴 주체가 없으면 실패해도 이월 자금이 그대로 남는다.
    /// 서버(또는 오프라인) 전용.
    /// </summary>
    public void ResetToStarting()
    {
        if (!IsServer)
        {
            Debug.LogWarning("TeamFund.ResetToStarting은 서버에서만", this);
            return;
        }

        int before = m_fund.Value;
        m_fund.Value = m_startingFund;
        Debug.Log($"[팀 자금] 라운드 실패로 초기화 — {before} → {m_fund.Value}");
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
            Debug.LogWarning("TeamFund.TrySpend는 서버에서만", this);
            return false;
        }
        if (cost < 0 || m_fund.Value < cost) return false;

        m_fund.Value -= cost;
        Debug.Log($"[팀 자금] 차감 -{cost} → 잔액 {m_fund.Value}");
        return true;
    }

    // [임시/디버그] 이월 확인용 — 서버 전용. 정식 증감은 검거 보상·상점. (#214 테스트, 확인 후 제거)
    //public void DebugAddFund(int amount)
    //{
    //    if (IsServer)
    //        m_fund.Value = Mathf.Max(0, m_fund.Value + amount);
    //}
}
