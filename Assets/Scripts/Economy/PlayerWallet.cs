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
