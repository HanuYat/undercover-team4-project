using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 팀 공용 자금(#104) — 세션 내내 유지되는 상주 홀더. (#214 §6 이월 구조)
/// 세션 시작 시 서버가 1회 스폰(destroyWithScene:false)해 씬을 넘어 값이 유지된다.
/// 검거 보상 가산은 게임 씬의 ArrestJudge에 붙어야 하므로, 게임 씬 진입마다 재배선한다.
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

        // 게임 씬의 ArrestJudge에 라운드마다 다시 붙는다.
        App.OnSceneLoaded += HandleSceneLoaded;
        if (App.CurrentScene == EScene.Game)
            HandleSceneLoaded(EScene.Game);
    }

    public override void OnNetworkDespawn()
    {
        if (!IsServer) return;

        App.OnSceneLoaded -= HandleSceneLoaded;
        // 현재 ArrestJudge 구독은 씬 언로드와 동시에 자동 정리됨.
    }

    // 게임 씬 진입 시 그 씬의 ArrestJudge에 구독한다. 이전 씬의 ArrestJudge는 파괴됐으므로 중복/누수 없음.
    private void HandleSceneLoaded(EScene scene)
    {
        if (scene != EScene.Game) return;

        ArrestJudge judge = App.Game.ArrestJudge;
        if (judge != null)
            judge.OnArrestJudged += HandleArrestJudged;
        else
            Debug.LogWarning($"[TeamFund] 게임 씬에 ArrestJudge가 없어 검거 보상을 받지 못함.", this);
    }

    private void HandleArrestJudged(ArrestResult result)
    {
        if (result.Reward == 0) return;

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
