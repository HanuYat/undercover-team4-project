using UnityEngine;

/// <summary>
/// 털리는 쪽 — 약탈 대상으로서의 플레이어. (#487)
///
/// 약탈은 두 역할로 나뉘고, 이쪽은 <b>당하는 쪽의 얼굴</b>이다: 지금 털릴 수 있는 상태인가,
/// 무엇을 들고 있고 지갑은 어디인가, 그리고 <b>당했을 때 본인에게 알리는 것</b>. 실제로 무엇을
/// 가져갈지 정하고 옮기는 것은 행위 주체인 <see cref="PlayerLooter"/>다.
///
/// 소매치기(<see cref="Pickpocket"/>, #303)가 세운 축과 같다 — 피해자 쪽에서는 목록만 내주고,
/// 어느 것을 채는지는 채는 쪽이 정한다. 피해자에게 탈취 로직을 두면 "털린다"가 소지품 관리의
/// 일부가 되어 버린다.
///
/// <b><see cref="PlayerCarrier"/>처럼 두 역할을 한 컴포넌트에 합치지 않은 이유</b>는 약탈에
/// 짝 상태가 없기 때문이다. 운반은 끄는 쪽과 끌려가는 쪽이 서로를 가리키며 "끌면서 동시에
/// 끌려가는" 조합을 막아야 해서 한 몸이어야 하지만, 약탈은 서버에 세션을 두지 않고 요청마다
/// 처음부터 검증한다(<see cref="PlayerLooter"/>) — 공유할 상태가 아예 없다.
///
/// 알림이 <b>자기 오너에게</b> 가야 해서 NetworkBehaviour다. <see cref="NotifyOwner"/>는 이 컴포넌트의
/// 오너를 대상으로 하므로, 약탈자 쪽 피드백과 피해자 쪽 피드백이 각자 자기 컴포넌트에서 나간다.
/// (채널링은 쓰지 않고 오너 피드백만 빌린다 — <see cref="ItemBattery"/>와 같은 관례)
/// </summary>
public class PlayerLootable : ChanneledInteractionBehaviour
{
    private PlayerIncapacitation m_incapacitation;
    private PlayerLoadout m_loadout;
    private PlayerWallet m_wallet;
    private PlayerTheftView m_theftView; // 소매치기(#303)와 같은 도난 알림을 재사용한다

    /// <summary>
    /// 지금 이 몸을 털 수 있는가 — 기능 정지(Die)뿐. 전 피어에서 같은 답이 나온다(동기화된 원인).
    ///
    /// 테이저 기절(<c>Stun</c>)은 제외한다: 스스로 일어나는 상태까지 털 수 있으면 테이저가 최고의
    /// 강도 도구가 된다. 어차피 조준 히트박스가 <c>IsOutOfAction</c>에서만 켜져 기절한 몸은
    /// 겨냥조차 되지 않지만(<see cref="PlayerIncapacitation"/>), 위조 RPC 방어로 여기서도 본다.
    /// </summary>
    public bool CanBeLooted => m_incapacitation != null && m_incapacitation.IsDead;

    /// <summary>이 몸의 소지품 — 약탈자가 목록을 읽고(표시) 서버가 이전 대상을 확인한다.</summary>
    internal PlayerLoadout Loadout => m_loadout;

    /// <summary>이 몸의 개인 자금 — 약탈자 서버 경로가 전액 이전의 출발점으로 쓴다.</summary>
    internal PlayerWallet Wallet => m_wallet;

    private void Awake()
    {
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_loadout = GetComponent<PlayerLoadout>();
        m_wallet = GetComponent<PlayerWallet>();
        m_theftView = GetComponent<PlayerTheftView>();
    }

    // ---- 피해 알림 (서버가 약탈자 경로에서 호출 → 이 컴포넌트의 오너 = 피해자에게만 간다) ----

    /// <summary>소지품을 뺏겼다 — 본인에게만 알린다. 서버 전용.</summary>
    internal void ServerNotifyRobbedItem()
    {
        m_theftView?.ShowStolen(); // 문구·토스트는 소매치기와 같은 채널 (#303)
        NotifyOwner("[약탈] 소지품을 빼앗겼다");
    }

    /// <summary>
    /// 개인 자금을 뺏겼다 — 본인에게만 알린다. 서버 전용.
    /// 잔액 표시 UI가 없어 지금은 로그뿐이다. 금액을 인자로 받아 두는 이유는 표시가 생기면
    /// 여기만 바꾸면 되기 때문이다.
    /// </summary>
    internal void ServerNotifyRobbedFunds(int amount)
    {
        NotifyOwner($"[약탈] 개인 자금을 빼앗겼다 — {amount}");
    }
}
