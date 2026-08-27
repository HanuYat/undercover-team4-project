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
// TODO: 장착이 서버 권위로 넘어가면 쓰기 권한을 Owner → Server로 옮긴다.
[RequireComponent(typeof(PlayerItemUser))]
public class PlayerHeldItemView : NetworkBehaviour
{
    [Header("손 앵커 (캐릭터 리그의 손 본)")]
    [Tooltip("아이템을 들릴 손 본. Synty 리그의 Hand_R 하위에 배치할 것")]
    [SerializeField]
    private Transform m_handAnchor;

    /// <summary>손 본 앵커 — 손에서 뻗어 나가는 표현(밧줄 선 #269 등)이 시작점으로 쓴다. 미지정이면 null.</summary>
    public Transform HandAnchor => m_handAnchor;

    /// <summary>
    /// 시체를 묶을 밧줄의 <b>물리 앵커</b> — <b>운반자의 루트</b>다.
    /// (#365/#506 → #571에서 NPC 시체도 같은 지점을 쓴다)
    ///
    /// <b>한때 손이었다가 되돌렸다.</b> 손을 고른 이유는 흐느적임이었다 — 손은 걷기 애니메이션으로
    /// 흔들리므로 매 걸음 장력이 변하고, 그 <b>가속 차이</b>가 팔다리를 흔든다(등속으로 끌면 전 뼈가
    /// 같은 속도가 되어 관절이 느낄 것이 없고 몸이 한 덩어리로 미끄러진다).
    ///
    /// 문제는 그 흔들림이 <b>애니메이터가 만든다</b>는 것이다. 애니메이터는 피어마다 따로 평가되고,
    /// 운반자가 원격이면 그 위에 NetworkTransform 보간값까지 얹힌다 — 즉 <b>앵커 위치가 피어마다
    /// 다르다.</b> 전 피어가 각자 밧줄을 묶던 구조에서는 그것이 곧 <b>같은 관절에 다른 입력</b>이
    /// 되어 견인 발산의 원인이 됐다.
    ///
    /// 루트는 스트리밍되는 값이라 전 피어가 같다. <b>흔들림은 따로 되찾을 문제로 미뤄 둔다</b> —
    /// 되찾을 때는 애니메이터가 아니라 <b>스트리밍된 이동거리에서 위상을 뽑아</b> 결정론적으로
    /// 합성해야 한다(<c>PlayerTowedMotion.m_dragTravel</c>이 이미 그 값을 누적한다).
    ///
    /// <b>보이는 줄은 그대로 손에서 나간다</b> — <see cref="RopeDragView"/>가 <see cref="HandAnchor"/>를
    /// 직접 읽으므로 이 함수와 무관하다. 당기는 지점만 갈렸다.
    ///
    /// <b>여기 있는 이유:</b> 부르는 쪽이 둘로 갈렸다 — 동료 운반(<see cref="PlayerTowedMotion"/>)과
    /// NPC 시체 끌기(<see cref="NpcRopeDrag"/>). 앵커의 주인이 이 컴포넌트이므로 판정도 여기 둔다.
    /// </summary>
    public static Transform ResolveRopeAnchor(Transform carrier) => carrier;

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

    // ---- 사망 중 숨김 (#571) ----
    //
    // <b>왜 필요한가.</b> <see cref="m_handAnchor"/>는 <b>살아있는 리그</b>의 손이고
    // (<c>Player/Root/.../Hand_R/HeldItemAnchor</c>), 사망 시 꺼지는 것은 살아있는 <b>스킨</b>뿐이다 —
    // 뼈는 계속 켜져 있다(Animator의 아바타 바인딩이 경로 기반이라 끄면 애니메이션이 끊긴다).
    // 그래서 몸은 사라지고 시체는 굴러가는데 <b>손에 든 아이템만 죽은 자리에 떠 있는다.</b>
    //
    // 표현 컴포넌트가 <c>IsRagdollActive</c>를 보고 스스로 물러나는 것이 이 기능의 관례다
    // (<see cref="PlayerAnimationDriver"/>·<see cref="PlayerMovement"/>·<see cref="PlayerHeadLook"/>).
    //
    // <b>폴링인 이유</b>는 모델이 <b>비동기로</b> 만들어지기 때문이다(<see cref="RefreshHeldModelAsync"/>).
    // 사망 시점에 밀어서 숨기면 그 뒤에 해석이 끝난 모델이 다시 나타난다 — 그래서 상태를 매 프레임 보고,
    // 만들어지는 자리에서도 한 번 맞춘다.
    private PlayerRagdoll m_ragdoll;
    private bool m_hiddenByRagdoll;

    public override void OnNetworkSpawn()
    {
        m_itemUser = GetComponent<PlayerItemUser>();
        m_ragdoll = GetComponentInParent<PlayerRagdoll>();

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

        // 대기 중 디스폰됐거나 장착이 또 바뀌었으면 이 갱신은 폐기한다.
        EResolveResult result = await NetworkRefResolver.WaitAsync(
            itemRef,
            () => this != null && IsSpawned && version == m_refreshVersion
        );

        if (result != EResolveResult.Resolved)
        {
            return; // 폐기됐거나 상한까지 못 풀었다 — 빈손처럼 둔다
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

    // 래그돌 상태를 따라간다 — 위 필드 주석의 사정으로 밀어 넣기가 아니라 폴링이다.
    private void Update()
    {
        bool hide = m_ragdoll != null && m_ragdoll.IsRagdollActive;
        if (hide == m_hiddenByRagdoll)
            return;

        m_hiddenByRagdoll = hide;
        ApplyRagdollVisibility();
    }

    // 사망 중이면 손에 든 모델을 감춘다. 오너 화면에서는 이미 OwnBody 레이어로 가려져 있으므로
    // 이 처리가 실제로 바꾸는 것은 <b>남들에게 보이는 3인칭 표시</b>다.
    private void ApplyRagdollVisibility()
    {
        if (m_heldModelInstance != null)
            m_heldModelInstance.SetActive(!m_hiddenByRagdoll);
    }

    private void ShowHeldModel(ItemBase item)
    {
        m_heldModelInstance = Instantiate(item.HeldModelPrefab, m_handAnchor, false);

        // 아이템마다 모델 피벗이 달라(대부분 손목에 걸린다) 앵커 하나로는 못 맞춘다 —
        // 실제로 쥔 각도는 아이템이 자기 그립 오프셋으로 들고 있다. (#151)
        m_heldModelInstance.transform.localPosition = item.ThirdPersonPositionOffset;
        m_heldModelInstance.transform.localRotation = Quaternion.Euler(
            item.ThirdPersonRotationOffset
        );

        // 표시 전용 인스턴스 — 콜라이더가 플레이어·월드와 간섭하지 않게 전부 끈다 (PlayerHandView와 동일)
        foreach (Collider heldCollider in m_heldModelInstance.GetComponentsInChildren<Collider>(true))
        {
            heldCollider.enabled = false;
        }

        // 오너 화면에서는 1인칭 손 표시(#45)만 보여야 하므로 캐릭터 몸과 같은 OwnBody 레이어로 가린다.
        // 손 본은 PlayerLook의 m_ownBodyRoot(스킨드 메시) 바깥이라 레이어가 자동 상속되지 않는다 —
        // 여기서 명시적으로 찍어야 내 화면에서 3인칭 모델과 1인칭 뷰모델이 이중으로 보이지 않는다.
        if (IsOwner)
        {
            PlayerLook.SetLayerRecursively(
                m_heldModelInstance.transform,
                LayerMask.NameToLayer("OwnBody")
            );
        }

        // 사망 중에 해석이 끝나 늦게 만들어진 모델도 곧바로 감춘다 — Update를 한 프레임 기다리면
        // 그동안 죽은 자리에 아이템이 번쩍인다.
        ApplyRagdollVisibility();
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
