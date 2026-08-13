using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 개인 자금(#484) — 플레이어별 잔액.
/// 수배범을 직접 끌고 와 유치장에 앉힌 사람에게 라운드 정산 시 현상금의 10%가 들어간다.
/// 팀 자금과 달리 상주 홀더가 아니라 플레이어 오브젝트에 붙는다.
///
/// 잔액은 본인만 본다. — 읽기 권한 Owner.
/// 후속 이슈(#485 비밀 청탁 · #487 약탈)가 의존하는 정보 비대칭이라 전원 브로드캐스트하면 안 된다.
///
/// 오프라인(비네트워크) 폴백은 두지 않는다.
/// </summary>
public class PlayerWallet : NetworkBehaviour
{
    // 플레이어 잔고
    private readonly NetworkVariable<int> m_balance = new NetworkVariable<int>(
        0, 
        NetworkVariableReadPermission.Owner,
        NetworkVariableWritePermission.Server);

    // 이번 라운드에 번 금액(개인)
    private readonly NetworkVariable<int> m_roundEarned = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Owner,
        NetworkVariableWritePermission.Server);

    // 잔액
    public NetworkVariable<int> BalanceVar => m_balance;
    public int Balance => m_balance.Value;

    // 이번 라운드 개인 몫
    public NetworkVariable<int> RoundEarnedVar => m_roundEarned;
    public int RoundEarned => m_roundEarned.Value;

    // 이 지갑 주인의 UGS PlayerId — 세이브의 키다 (#373). 오너가 스폰 시 보고해야 서버가 안다.
    // clientId는 세션마다 재발급되므로 판을 넘어 사람을 가리키지 못한다. 서버에서만 채워진다.
    private string m_ownerPlayerId;

    /// <summary>이 지갑 주인의 UGS PlayerId — 세이브 기록용. 보고 전이거나 서버가 아니면 빈 문자열.</summary>
    public string OwnerPlayerId => m_ownerPlayerId;

    public override void OnNetworkSpawn()
    {
        // 서버가 알 수 없는 로컬 값이라 오너가 올린다 — SessionRoster가 닉네임·PlayerId를 올리는 것과 같은 구조.
        if (IsOwner)
            ReportPlayerIdRpc((App.Net.Auth != null ? App.Net.Auth.PlayerId : null).ToFixed64());
    }

    // 플레이어 오브젝트는 오너가 이 클라이언트라 기본 권한(오너만 발신)으로 충분하다.
    [Rpc(SendTo.Server)]
    private void ReportPlayerIdRpc(FixedString64Bytes playerId)
    {
        m_ownerPlayerId = playerId.ToString();

        // 이어하기로 시작한 판이면 저장된 잔액을 여기서 한 번 돌려준다 (#373).
        // 세이브에 없는 사람(새로 합류)은 0에서 시작한다 — 그 판정은 SaveService가 한다.
        if (SaveService.TryTakeWalletBalance(m_ownerPlayerId, out int saved))
        {
            m_balance.Value = saved;
            Debug.Log($"[개인 자금] 세이브 복원 — {OwnerClientId}번 잔액 {saved}");
        }
    }

    /// <summary>로컬 플레이어의 지갑 — 표시 전용. 잔액은 오너만 읽으므로 남의 지갑을 잡으면 0만 보인다.</summary>
    public static PlayerWallet Local
    {
        get
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || nm.LocalClient == null || nm.LocalClient.PlayerObject == null) return null;

            return nm.LocalClient.PlayerObject.GetComponent<PlayerWallet>();
        }
    }

    public void ServerAdd(int amount)
    {
        if (!IsServer)
        {
            Debug.LogWarning("PlayerWallet.ServerAdd는 서버에서만", this);
            return;
        }
        if (amount <= 0) return;

        m_balance.Value = Mathf.Max(0, m_balance.Value + amount);
        m_roundEarned.Value += amount;
        Debug.Log($"[개인 자금] {OwnerClientId}번 + {amount} -> 잔액 {m_balance.Value}");
    }

    /// <summary>
    /// 잔액 전액을 다른 지갑으로 옮긴다 — 아군 약탈(#487)의 유일한 자금 이동 경로. 서버 전용.
    ///
    /// <b>발행이 아니라 이전이다.</b> 약탈로 자금이 새로 생기지 않으므로 두 사람이 서로 번갈아 털어도
    /// 총량이 늘지 않는다 — #487이 경고한 무한 증식은 이 한 가지 성질로 막힌다. 그래서 "동료를
    /// 눕힌 대가로 자금을 준다"(발행)는 경로는 만들지 않았다.
    ///
    /// <b>전액인 이유</b>는 부분 이전이면 "몇 번 더 털기"가 최적 플레이가 되기 때문이다.
    ///
    /// 뺏은 쪽의 <see cref="RoundEarned"/>는 늘지만(<see cref="ServerAdd"/>) 털린 쪽은 줄지 않는다 —
    /// 그 값은 "이번 라운드에 번 금액"이라, 뺏겼다고 벌지 않은 것이 되지는 않는다.
    /// </summary>
    /// <returns>실제로 옮긴 금액. 빈 지갑이면 0.</returns>
    public int ServerTransferAllTo(PlayerWallet to)
    {
        if (!IsServer)
        {
            Debug.LogWarning("PlayerWallet.ServerTransferAllTo는 서버에서만", this);
            return 0;
        }
        if (to == null || to == this) return 0;

        int amount = m_balance.Value;
        if (amount <= 0) return 0;

        m_balance.Value = 0;
        to.ServerAdd(amount);
        Debug.Log($"[개인 자금] 이전 — {OwnerClientId}번 → {to.OwnerClientId}번, {amount}");
        return amount;
    }

    public void ServerResetRound()
    {
        if (!IsServer) return;
        m_roundEarned.Value = 0;
    }

    /// <summary>
    /// clientId로 그 플레이어의 지갑을 찾는다 — 정산 지급(서버)이 쓴다. 없으면 null.
    /// ConnectedClients는 서버에서만 채워지므로 서버 전용이다.
    /// </summary>
    public static PlayerWallet FindByClientId(ulong clientId)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.ConnectedClients.TryGetValue(clientId, out NetworkClient client) || client.PlayerObject == null) return null;

        return client.PlayerObject.GetComponent<PlayerWallet>();
    }
}
