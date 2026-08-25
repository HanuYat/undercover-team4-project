using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

/// <summary>
/// 상점 진열대 (#182, #814) — 익명 슬롯. ShopLineup이 라운드마다 카탈로그에서 뽑은 품목을
/// ServerAssign으로 배정하고, 서버는 자기 NetworkVariable(m_entryIndex)로 품목·가격을 읽는다 —
/// 클라는 "산다"는 사실만 보내므로 위조 경로가 없다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(ShopStandView))]
[RequireComponent(typeof(ShopStandDisplay))]
public class ShopStand : NetworkBehaviour, IInteractable
{
    private const string k_itemTable = "ItemTable";
    private const string k_shopTable = "ShopTable";
    private const string k_nameKeyPrefix = "Item.Name.";
    private const string k_descriptionKeyPrefix = "Item.Description.";

    private ShopStandView m_view;
    private ShopStandDisplay m_display;
    private ShopCatalog m_catalog;
    private Collider[] m_aimColliders;

    private readonly NetworkVariable<int> m_entryIndex = new NetworkVariable<int>(-1); // -1 = 빈 슬롯
    private readonly NetworkVariable<EStandStatus> m_status = new NetworkVariable<EStandStatus>();

    private ShopCatalog.Entry CurrentEntry =>
        m_catalog != null ? m_catalog.Get(m_entryIndex.Value) : null;

    public bool IsInstallable => CurrentEntry?.IsInstallable ?? false;
    public int Price => CurrentEntry?.Price ?? 0;

    private void Awake()
    {
        m_view = GetComponent<ShopStandView>();
        m_display = GetComponent<ShopStandDisplay>();
        m_aimColliders = GetComponentsInChildren<Collider>(true);
    }

    /// <summary>ShopLineup이 Awake에서 주입한다. 멱등하게 만들어 순서 가정을 없앤다.</summary>
    public void BindCatalog(ShopCatalog catalog)
    {
        m_catalog = catalog;
        if (IsSpawned)
            RefreshView();
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

        ShopCatalog.Entry entry = CurrentEntry;
        ShopPurchases purchases = App.Game.ShopPurchases;
        m_status.Value =
            entry != null && entry.IsInstallable && purchases != null && purchases.HasInstallable(entry.Installable)
                ? EStandStatus.Owned
                : EStandStatus.Available;
    }

    public override void OnNetworkSpawn()
    {
        m_entryIndex.OnValueChanged += HandleAssignmentChanged;
        m_status.OnValueChanged += HandleAssignmentChanged;
        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;

        RefreshView();
    }

    public override void OnNetworkDespawn()
    {
        m_entryIndex.OnValueChanged -= HandleAssignmentChanged;
        m_status.OnValueChanged -= HandleAssignmentChanged;

        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;
    }

    private void HandleAssignmentChanged(int previous, int current) => RefreshView();

    private void HandleAssignmentChanged(EStandStatus previous, EStandStatus current) => RefreshView();

    private void HandleLocaleChanged(Locale locale) => RefreshView();

    private void RefreshView()
    {
        ShopCatalog.Entry entry = CurrentEntry;

        foreach (Collider aimCollider in m_aimColliders)
        {
            if (aimCollider != null)
                aimCollider.enabled = entry != null;
        }

        if (entry == null)
        {
            m_display.Rebuild(null);
            m_view.SetEmpty(true);
            return;
        }

        // 품절이면 모델을 치운다 — 다 팔린 자리를 빈 채로 보여준다. 이미 구매한 설치형(Owned)은
        // 그대로 둔다 — 그건 진열은 계속되고 구매만 막히는 별개 규칙이다.
        m_display.Rebuild(m_status.Value == EStandStatus.SoldOut ? null : entry);

        m_view.SetEmpty(false);
        m_view.SetContent(ResolveName(entry), ResolveDescription(entry), entry.IsInstallable, entry.Price);
        m_view.SetStatus(m_status.Value);
    }

    private string ResolveName(ShopCatalog.Entry entry)
    {
        if (entry.IsInstallable)
            return LocalizedStrings.Get(k_itemTable, k_nameKeyPrefix + entry.Installable);

        return entry.ItemPrefab != null && !entry.ItemPrefab.ItemName.IsEmpty
            ? entry.ItemPrefab.ItemName.GetLocalizedString()
            : LocalizedStrings.Get(k_shopTable, "Shop.Stand.NoItem");
    }

    private string ResolveDescription(ShopCatalog.Entry entry)
    {
        if (entry.IsInstallable)
            return LocalizedStrings.Get(k_itemTable, k_descriptionKeyPrefix + entry.Installable);

        return entry.ItemPrefab != null && !entry.ItemPrefab.ItemDescription.IsEmpty
            ? entry.ItemPrefab.ItemDescription.GetLocalizedString()
            : string.Empty;
    }

    // ---- 상호작용 (E) ----

    public bool CanInteract(GameObject interactor) => CurrentEntry != null;

    public LocalizedString PromptLabel(GameObject interactor) => CurrentEntry != null ? InteractPrompts.Shop : null;

    public void Interact(GameObject interactor)
    {
        if (!IsSpawned)
        {
            Debug.LogWarning("ShopStand: 세션이 없어 구매할 수 없다 (정식 경로 Title→Lobby→Shop으로 진입할 것)", this);
            return;
        }

        RequestPurchaseRpc();
    }

    // ---- 구매 (서버 권위) ----

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

        ShopCatalog.Entry entry = CurrentEntry;
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
        ReplyRpc("구매 완료 — 다음 라운드에 본부로 배달된다", EAudioClip.ShopPurchase, RpcTarget.Single(requester, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)] // 거절은 무음 — 값만 보려고 눌러도 실패음이 나지 않게
    private void ReplyRpc(string message, EAudioClip sound, RpcParams rpcParams)
    {
        m_view.ShowNotice(message);
        App.Sound?.PlaySfx2D(sound);
    }

    public void SetCardVisible(bool visible) => m_view.ShowCard(visible);
}
