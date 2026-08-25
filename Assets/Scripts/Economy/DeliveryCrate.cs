using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 배달 상자 (#824) — DeliveryPad에 놓인다. 내용물 상태를 들고 있지 않는다: 열 때 서버가
/// ShopPurchases.Carried를 다시 읽어 그 자리에서 스폰한다. 오너 없는 서버 소유 오브젝트라
/// JailSirenButton과 같은 Everyone 권한 RPC를 쓴다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class DeliveryCrate : NetworkBehaviour, IInteractable
{
    private const float k_interactRange = 3f;

    [Tooltip("강하 연출 길이(초)")]
    [SerializeField]
    private float m_descentSeconds = 1.2f;

    private readonly NetworkVariable<double> m_landTimeSynced = new NetworkVariable<double>();
    private bool m_opened;

    public float DescentSeconds => m_descentSeconds;

    // 오프라인 폴백(IsSpawned false)은 착지 판정 없이 즉시 상호작용 가능하다.
    private bool HasLanded =>
        !IsSpawned || NetworkManager == null || NetworkManager.ServerTime.Time >= m_landTimeSynced.Value;

    public bool CanInteract(GameObject interactor) => !m_opened && HasLanded;

    public LocalizedString PromptLabel(GameObject interactor) =>
        CanInteract(interactor) ? InteractPrompts.OpenCrate : null;

    public void Interact(GameObject interactor)
    {
        if (!CanInteract(interactor))
            return;

        RequestOpenRpc();
    }

    /// <summary>서버가 스폰 전에 착지 시각(ServerTime 기준)을 정한다 — Spawn() 이전에 불러야
    /// 초기 동기화 페이로드에 실려 늦게 접속한 클라도 남은 시간을 정확히 계산한다.</summary>
    public void ServerScheduleLanding(double landTime)
    {
        m_landTimeSynced.Value = landTime;
    }

    public override void OnNetworkSpawn()
    {
        double remaining = m_landTimeSynced.Value - NetworkManager.ServerTime.Time;
        if (remaining <= 0d)
            return;

        DeliveryPad pad = DeliveryPad.All.Count > 0 ? DeliveryPad.All[0] : null;
        pad?.PlayDroneDelivery(transform, (float)remaining);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestOpenRpc(RpcParams rpcParams = default)
    {
        if (m_opened || !HasLanded || !IsRequesterInRange(rpcParams.Receive.SenderClientId))
            return;

        ServerOpen();
    }

    private bool IsRequesterInRange(ulong clientId)
    {
        NetworkManager manager = NetworkManager.Singleton;
        if (
            manager == null
            || !manager.ConnectedClients.TryGetValue(clientId, out NetworkClient client)
            || client.PlayerObject == null
        )
            return false;

        PlayerInteractor interactor = client.PlayerObject.GetComponent<PlayerInteractor>();
        return PlayerInteractor.IsWithinReach(
            interactor,
            transform,
            PlayerInteractor.RangeOf(interactor, k_interactRange),
            transform.position
        );
    }

    private void ServerOpen()
    {
        m_opened = true;
        SpillItems();
        PlayOpenSoundRpc();
        NetworkObject.Despawn(destroy: true);
    }

    [Rpc(SendTo.Everyone)]
    private void PlayOpenSoundRpc() =>
        App.Sound?.PlaySfxAt(EAudioClip.CrateOpen, transform.position);

    private void SpillItems()
    {
        DeliveryPad pad = DeliveryPad.All.Count > 0 ? DeliveryPad.All[0] : null;
        if (pad == null)
        {
            Debug.LogWarning("[배달 상자] 배달 지점을 찾지 못해 아이템을 놓지 못했다", this);
            return;
        }

        ShopPurchases purchases = App.Game.ShopPurchases;
        if (purchases == null)
            return;

        int index = 0;
        foreach (ItemBase itemPrefab in purchases.Carried)
        {
            if (itemPrefab == null)
                continue;

            ItemBase item = Instantiate(
                itemPrefab,
                pad.ResolveItemPosition(index++),
                Quaternion.identity
            );

            // 구매품 표식 — 소매치기에게 잃으면 구매 목록에서도 빼야 한다 (#303)
            item.gameObject.AddComponent<ShopDeliveredItem>().SourcePrefab = itemPrefab;
            item.NetworkObject.Spawn(destroyWithScene: false);
        }

        if (index > 0)
            Debug.Log($"[배달 상자] 소지형 {index}개 개봉");
    }
}
