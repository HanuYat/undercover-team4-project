using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 3인칭 장착 아이템 표시. (#151)
/// 장착 아이템을 NetworkVariable로 전 피어에 알리고, 각 클라가 캐릭터 모델의 손 본에
/// 아이템의 HeldModelPrefab을 표시한다 — 누가 무엇을 들고 있는지 서로 보인다.
/// 1인칭(오너 로컬) 표시는 PlayerHandView(#45)가 담당하며, 이 컴포넌트와 대칭 구조다.
///
/// 표시 모델은 순수 로컬 표현이라 네트워크로 스폰하지 않는다 — 각 클라가 자기 몫을 만든다.
/// (WorldItemPickup의 월드 비주얼과 같은 방침) 네트워크로 오가는 건 "무엇을 장착했는가" 하나뿐.
///
/// 장착 상태 동기화에 RPC가 아니라 NetworkVariable을 쓰는 이유: 늦게 접속한 클라도
/// 스폰 시점에 현재 값을 그대로 받아야 기존 플레이어들의 장착이 올바르게 보인다.
/// RPC 브로드캐스트는 접속 전에 발행된 장착 변경을 받을 방법이 없다.
/// </summary>
// TODO: #55 장착이 서버 권위로 넘어가면 쓰기 권한을 Owner → Server로 옮긴다.
[RequireComponent(typeof(PlayerItemUser))]
public class PlayerHeldItemView : NetworkBehaviour
{
    [Header("손 앵커 (캐릭터 리그의 손 본)")]
    [Tooltip("아이템을 들릴 손 본. Synty 리그의 Hand_R 하위에 배치할 것")]
    [SerializeField]
    private Transform m_handAnchor;

    /// <summary>손 본 앵커 — 손에서 뻗어 나가는 표현(밧줄 선 #269 등)이 시작점으로 쓴다. 미지정이면 null.</summary>
    public Transform HandAnchor => m_handAnchor;

    // 아이템 참조 해석 대기 상한(프레임). 스폰 메시지와 NetworkVariable 도착 순서 경쟁으로
    // 참조가 즉시 안 풀릴 수 있다 — PlayerLoadout.ResolveAndRebuildAsync와 같은 방침.
    private const int k_maxResolveWaitFrames = 120;

    // 장착 아이템 — 빈손이면 default(NetworkObjectId 0). 오너가 쓰고 전 피어가 읽는다.
    private readonly NetworkVariable<NetworkObjectReference> m_equipped =
        new NetworkVariable<NetworkObjectReference>(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner
        );

    private PlayerItemUser m_itemUser;
    private GameObject m_heldModelInstance;

    // 표시 갱신 세대 번호 — 참조 해석을 기다리는 동안 장착이 또 바뀌면 옛 갱신을 버린다.
    // (휠을 빠르게 굴리면 늦게 끝난 옛 갱신이 최신 모델을 덮어쓴다)
    private int m_refreshVersion;

    public override void OnNetworkSpawn()
    {
        m_itemUser = GetComponent<PlayerItemUser>();

        if (m_handAnchor == null)
        {
            Debug.LogWarning(
                $"[PlayerHeldItemView] 손 앵커가 지정되지 않아 3인칭 장착 표시를 끈다. "
                    + $"{name} 프리팹의 손 본(Hand_R) 하위 앵커를 인스펙터에 지정할 것."
            );
            enabled = false;
            return;
        }

        // 오너만 장착 상태를 발행한다. 구독보다 먼저 초기값을 써 둬야 아래 초기 표시 1회로 함께 처리된다.
        if (IsOwner)
        {
            m_itemUser.OnEquippedItemChanged += PublishEquipped;
            PublishEquipped(m_itemUser.EquippedItem);
        }

        // 표시는 전 피어가 한다 — 오너 자신도 포함(아래 OwnBody 레이어로 자기 카메라에서만 가린다).
        m_equipped.OnValueChanged += HandleEquippedChanged;
        HandleEquippedChanged(default, m_equipped.Value); // 늦게 접속한 클라의 초기 표시
    }

    public override void OnNetworkDespawn()
    {
        if (m_itemUser != null)
        {
            m_itemUser.OnEquippedItemChanged -= PublishEquipped;
        }

        m_equipped.OnValueChanged -= HandleEquippedChanged;
        ClearHeldModel();
    }

    // ---- 오너: 장착 상태 발행 ----

    // 장착 아이템을 전 피어가 볼 수 있게 NetworkVariable에 쓴다. 빈손이면 default.
    private void PublishEquipped(ItemBase item)
    {
        NetworkObject itemNetworkObject =
            item != null ? item.GetComponent<NetworkObject>() : null;

        // IsSpawned 검사 필수 — 스폰되지 않은 NetworkObject로 참조를 만들면 ArgumentException이 나고,
        // 이 예외가 OnEquippedItemChanged 호출부(PlayerLoadout.RebuildHeldItems)까지 거슬러 올라가
        // 인벤토리 재구성을 중단시킨다. 소모 아이템이 디스폰되며 장착 해제되는 경로(#229·#250)와
        // 인스펙터로 미리 꽂아둔 m_equippedItem이 여기로 들어온다.
        m_equipped.Value =
            itemNetworkObject != null && itemNetworkObject.IsSpawned
                ? new NetworkObjectReference(itemNetworkObject)
                : default;
    }

    // ---- 전 피어: 손 모델 표시 ----

    private void HandleEquippedChanged(
        NetworkObjectReference previous,
        NetworkObjectReference current
    )
    {
        RefreshHeldModelAsync(current, ++m_refreshVersion).Forget();
    }

    private async UniTaskVoid RefreshHeldModelAsync(NetworkObjectReference itemRef, int version)
    {
        // 교체·해제는 즉시 반영한다 — 참조 해석을 기다리는 동안 옛 아이템이 손에 남지 않게.
        ClearHeldModel();

        // 빈손 — 캐릭터 자기 손만 보인다. 표시할 모델 없음.
        if (itemRef.NetworkObjectId == 0)
        {
            return;
        }

        for (
            int frame = 0;
            frame < k_maxResolveWaitFrames && !itemRef.TryGet(out _);
            frame++
        )
        {
            await UniTask.Yield(PlayerLoopTiming.Update);

            // 대기 중 디스폰됐거나 장착이 또 바뀌었으면 이 갱신은 폐기한다.
            if (this == null || !IsSpawned || version != m_refreshVersion)
            {
                return;
            }
        }

        if (
            !itemRef.TryGet(out NetworkObject itemNetworkObject)
            || !itemNetworkObject.TryGetComponent(out ItemBase item)
            || item.HeldModelPrefab == null
        )
        {
            return; // 해석 실패했거나 표시 모델이 없는 아이템 — 빈손처럼 둔다
        }

        ShowHeldModel(item);
    }

    private void ShowHeldModel(ItemBase item)
    {
        m_heldModelInstance = Instantiate(item.HeldModelPrefab, m_handAnchor, false);

        // 아이템마다 모델 피벗이 달라(대부분 손목에 걸린다) 앵커 하나로는 못 맞춘다 —
        // 실제로 쥔 각도는 아이템이 자기 그립 오프셋으로 들고 있다. (#151)
        m_heldModelInstance.transform.localPosition = item.HeldPositionOffset;
        m_heldModelInstance.transform.localRotation = Quaternion.Euler(item.HeldRotationOffset);

        // 표시 전용 인스턴스 — 콜라이더가 플레이어·월드와 간섭하지 않게 전부 끈다 (PlayerHandView와 동일)
        foreach (Collider heldCollider in m_heldModelInstance.GetComponentsInChildren<Collider>(true))
        {
            heldCollider.enabled = false;
        }

        // 오너 화면에서는 1인칭 손 표시(#45)만 보여야 하므로 캐릭터 몸과 같은 OwnBody 레이어로 가린다.
        // 손 본은 PlayerMovement의 m_ownBodyRoot(스킨드 메시) 바깥이라 레이어가 자동 상속되지 않는다 —
        // 여기서 명시적으로 찍어야 내 화면에서 3인칭 모델과 1인칭 뷰모델이 이중으로 보이지 않는다.
        if (IsOwner)
        {
            PlayerMovement.SetLayerRecursively(
                m_heldModelInstance.transform,
                LayerMask.NameToLayer("OwnBody")
            );
        }
    }

    private void ClearHeldModel()
    {
        if (m_heldModelInstance != null)
        {
            Destroy(m_heldModelInstance);
            m_heldModelInstance = null;
        }
    }
}
