using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 1인칭 손·장착 아이템 표시. (#45)
/// 오너의 카메라 하위 손 앵커에 손 모델을 표시하고, PlayerItemUser의 장착 변경 이벤트를 구독해
/// 장착 아이템의 HeldModelPrefab을 손에 갈아끼운다.
/// 순수 시각 표현 — 아이템 사용 로직(PlayerItemUser → ItemBase.Use)에는 관여하지 않는다.
/// 1인칭(오너 로컬) 전용: 다른 플레이어에게 보이는 3인칭 장착 표시와 장착 상태의
/// 네트워크 동기화는 이 이슈 범위 밖이므로 후속 이슈로 다룬다.
/// </summary>
[RequireComponent(typeof(PlayerItemUser))]
public class PlayerHandView : NetworkBehaviour
{
    [Header("손 앵커 (비우면 카메라 하위에 자동 생성)")]
    [SerializeField]
    private Transform m_handAnchor;

    [Header("1인칭 손 모델 (선택)")]
    [Tooltip("카메라 하위에 배치할 것. PlayerMovement의 OwnBody 루트 바깥에 둬야 내 카메라에 보인다")]
    [SerializeField]
    private GameObject m_handsModel;

    // 앵커 자동 생성 시 카메라 기준 기본 오프셋 (화면 오른쪽 아래)
    private static readonly Vector3 s_defaultAnchorOffset = new Vector3(0.4f, -0.2f, 0.5f);

    private PlayerItemUser m_itemUser;
    private GameObject m_heldModelInstance;

    public override void OnNetworkSpawn()
    {
        m_itemUser = GetComponent<PlayerItemUser>();

        // 1인칭 뷰모델은 내 화면 전용 — 남의 플레이어 오브젝트에서는 아무것도 표시하지 않는다
        // (PlayerMovement가 오너 외 카메라를 끄는 것과 같은 방침)
        if (!IsOwner)
        {
            if (m_handsModel != null)
            {
                m_handsModel.SetActive(false);
            }
            enabled = false;
            return;
        }

        EnsureHandAnchor();

        if (m_handsModel != null)
        {
            m_handsModel.SetActive(true);
        }

        m_itemUser.OnEquippedItemChanged += RefreshHeldModel;
        RefreshHeldModel(m_itemUser.EquippedItem);
    }

    public override void OnNetworkDespawn()
    {
        if (m_itemUser != null)
        {
            m_itemUser.OnEquippedItemChanged -= RefreshHeldModel;
        }
    }

    // 앵커가 인스펙터에서 지정되지 않았으면 카메라 하위에 기본 위치로 만든다.
    // 손 위치를 아트에 맞춰 조정할 때는 인스펙터에서 앵커를 직접 지정하면 된다.
    private void EnsureHandAnchor()
    {
        if (m_handAnchor != null)
        {
            return;
        }

        Camera playerCamera = GetComponentInChildren<Camera>(true);
        Transform anchorParent = playerCamera != null ? playerCamera.transform : transform;

        GameObject anchor = new GameObject("HandAnchor");
        m_handAnchor = anchor.transform;
        m_handAnchor.SetParent(anchorParent, false);
        m_handAnchor.localPosition = s_defaultAnchorOffset;
    }

    // 장착 변경 수신 — 기존 든 모델을 제거하고 새 아이템의 모델을 손 앵커에 표시한다.
    private void RefreshHeldModel(ItemBase item)
    {
        if (m_heldModelInstance != null)
        {
            Destroy(m_heldModelInstance);
            m_heldModelInstance = null;
        }

        if (item == null || item.HeldModelPrefab == null)
        {
            return; // 빈손이거나 표시 모델이 없는 아이템 — 손만 보인다
        }

        m_heldModelInstance = Instantiate(item.HeldModelPrefab, m_handAnchor, false);

        // 표시 전용 인스턴스 — 콜라이더가 플레이어·월드와 간섭하지 않게 전부 끈다
        foreach (Collider heldCollider in m_heldModelInstance.GetComponentsInChildren<Collider>(true))
        {
            heldCollider.enabled = false;
        }
    }
}
