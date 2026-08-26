using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 상점 진열대 (#182, #814, #843) — <b>전시 전용 쇼룸 슬롯</b>. 모델만 올려 두고 글자는 두지 않는다;
/// 이름·가격·설명과 주문은 카탈로그 아이템이 여는 주문창이 맡는다.
///
/// 그래도 <b>구매 권한은 여기 남는다</b>. 슬롯마다 자기 NetworkVariable(m_entryIndex)로 품목·가격을
/// 읽으므로 클라는 "이 슬롯을 산다"는 사실만 보내면 되고 위조 경로가 없다 — 주문창이 목록을 그린다고
/// 해서 이 성질을 버릴 이유가 없다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(ShopStandDisplay))]
public class ShopStand : NetworkBehaviour
{
    private ShopStandDisplay m_display;
    private ShopCatalog m_catalog;

    private readonly NetworkVariable<int> m_entryIndex = new NetworkVariable<int>(-1); // -1 = 빈 슬롯
    private readonly NetworkVariable<EStandStatus> m_status = new NetworkVariable<EStandStatus>();

    /// <summary>이 슬롯이 파는 품목 — 빈 슬롯이면 null. 주문창이 목록을 그릴 때 읽는다.</summary>
    public ShopCatalog.Entry Entry => m_catalog != null ? m_catalog.Get(m_entryIndex.Value) : null;

    public EStandStatus Status => m_status.Value;

    public bool IsInstallable => Entry?.IsInstallable ?? false;
    public int Price => Entry?.Price ?? 0;

    /// <summary>품목·판매 상태가 바뀌었다 — 주문창이 열려 있으면 목록을 다시 그린다.</summary>
    public event Action OnChanged;

    /// <summary>구매 응답(성공·자금 부족 등). 요청자 클라에서만 울린다.</summary>
    public event Action<string> OnPurchaseReply;

    private void Awake()
    {
        m_display = GetComponent<ShopStandDisplay>();
    }

    /// <summary>ShopLineup이 Awake에서 주입한다. 멱등하게 만들어 순서 가정을 없앤다.</summary>
    public void BindCatalog(ShopCatalog catalog)
    {
        m_catalog = catalog;
        if (IsSpawned)
            RefreshDisplay();
    }

    /// <summary>서버 전용 — ShopLineup이 라운드마다 이 슬롯에 카탈로그 인덱스를 배정한다.</summary>
    public void ServerAssign(int entryIndex)
    {
        if (!IsServer)
        {
            Debug.LogWarning("ShopStand.ServerAssign은 서버에서만", this);
            return;
        }

        m_entryIndex.Value = entryIndex;

        ShopCatalog.Entry entry = Entry;
        ShopPurchases purchases = App.Game.ShopPurchases;
        m_status.Value =
            entry != null && entry.IsInstallable && purchases != null && purchases.HasInstallable(entry.Installable)
                ? EStandStatus.Owned
                : EStandStatus.Available;
    }

    public override void OnNetworkSpawn()
    {
        m_entryIndex.OnValueChanged += HandleIndexChanged;
        m_status.OnValueChanged += HandleStatusChanged;

        RefreshDisplay();
    }

    public override void OnNetworkDespawn()
    {
        m_entryIndex.OnValueChanged -= HandleIndexChanged;
        m_status.OnValueChanged -= HandleStatusChanged;
    }

    private void HandleIndexChanged(int previous, int current) => RefreshDisplay();

    private void HandleStatusChanged(EStandStatus previous, EStandStatus current) => RefreshDisplay();

    private void RefreshDisplay()
    {
        ShopCatalog.Entry entry = Entry;

        // 품절이면 모델을 치운다 — 다 팔린 자리를 빈 채로 보여준다. 이미 구매한 설치형(Owned)은
        // 그대로 둔다 — 그건 진열은 계속되고 구매만 막히는 별개 규칙이다.
        m_display.Rebuild(entry != null && m_status.Value != EStandStatus.SoldOut ? entry : null);

        OnChanged?.Invoke();
    }

    // ---- 주문 (서버 권위) ----

    /// <summary>주문창이 이 슬롯의 주문 버튼을 눌렀을 때 부른다.</summary>
    public void RequestPurchase()
    {
        if (!IsSpawned)
        {
            Debug.LogWarning("ShopStand: 세션이 없어 구매할 수 없다 (정식 경로 Title→Lobby→Shop으로 진입할 것)", this);
            return;
        }

        RequestPurchaseRpc();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)] // 진열대는 오너 없는 씬 오브젝트
    private void RequestPurchaseRpc(RpcParams rpcParams = default)
    {
        ulong requester = rpcParams.Receive.SenderClientId;

        ShopPurchases purchases = App.Game.ShopPurchases;
        TeamFund fund = App.Game.TeamFund;
        if (purchases == null || fund == null)
        {
            Debug.LogWarning("ShopStand: 상주 홀더(TeamFund/ShopPurchases)를 찾지 못해 구매를 처리할 수 없다", this);
            return;
        }

        ShopCatalog.Entry entry = Entry;
        if (entry == null)
        {
            Debug.LogWarning("ShopStand: 빈 슬롯에 구매 요청이 들어왔다", this);
            return;
        }

        if (entry.IsInstallable && purchases.HasInstallable(entry.Installable))
        {
            ReplyRpc("이미 구매한 장비", EAudioClip.None, RpcTarget.Single(requester, RpcTargetUse.Temp));
            return;
        }

        if (m_status.Value == EStandStatus.SoldOut)
        {
            ReplyRpc("품절된 품목", EAudioClip.None, RpcTarget.Single(requester, RpcTargetUse.Temp));
            return;
        }

        if (!fund.TrySpend(entry.Price))
        {
            ReplyRpc("팀 자금 부족", EAudioClip.None, RpcTarget.Single(requester, RpcTargetUse.Temp));
            return;
        }

        if (entry.IsInstallable)
            purchases.AddInstallable(entry.Installable);
        else
            purchases.AddCarried(entry.ItemPrefab);

        m_status.Value = EStandStatus.SoldOut; // 다음 라운드 재추첨 시 ServerAssign이 Owned로 재판정
        ReplyRpc("주문 완료 — 다음 라운드에 본부로 배달된다", EAudioClip.ShopPurchase, RpcTarget.Single(requester, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)] // 거절은 무음 — 값만 보려고 눌러도 실패음이 나지 않게
    private void ReplyRpc(string message, EAudioClip sound, RpcParams rpcParams)
    {
        OnPurchaseReply?.Invoke(message);
        App.Sound?.PlaySfx2D(sound);
    }
}
