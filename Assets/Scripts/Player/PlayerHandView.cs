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

    // 뷰모델 절차적 흔들림(bob/sway) — 애니 없이 손을 살짝 움직여 생동감을 준다 (#265).
    // 멈춰 있을 땐 미세한 숨쉬기, 걸을 땐 속도에 비례해 커진다.
    private const float k_bobRefSpeed = 5f; // 이 속도에서 걷기 흔들림이 최대 (PlayerMovement 기본 이동속도)
    private const float k_idleFreq = 1.6f;
    private const float k_walkFreq = 9f;
    private const float k_idleAmp = 0.004f;
    private const float k_walkAmp = 0.010f;
    private const float k_swayTiltDegrees = 90f; // bob 오프셋(m)당 손을 기울이는 각도 — 움직임에 무게감을 준다

    private PlayerItemUser m_itemUser;
    private GameObject m_heldModelInstance;

    // FP 손 손가락 프리셋 적용용 — 장착 아이템의 HandGrip에 맞춰 손가락을 굽힌다 (#265).
    // 손 본이 통짜 스킨드 메시라 애니메이터 없이 본을 직접 회전한다. 굽힘은 본 로컬 X축 기준.
    private enum FingerKind { Finger, Index, Thumb }

    private struct FingerJoint
    {
        public Transform Bone;
        public Quaternion BaseRotation; // 바인드 로컬 회전 — 프리셋 굽힘을 이 위에 얹는다
        public FingerKind Kind;
    }

    private FingerJoint[] m_fingerJoints;

    // 뷰모델 흔들림 상태 — 손 모델의 기준 로컬 포즈에 매 프레임 오프셋을 얹는다
    private CharacterController m_controller;
    private Vector3 m_handBasePos;
    private Quaternion m_handBaseRot;
    private bool m_hasHandBase; // 스폰에서 base 포즈를 캡처했는지 — Update가 스폰 전에 먼저 돌아 손을 원점으로 옮기는 것을 막는다
    private float m_bobTime;

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

        m_controller = GetComponent<CharacterController>();

        SetupHandViewmodel(); // 손 모델 활성화·레이어·앵커·기준 포즈·손가락 캐시 (FP 팔이 있을 때)
        EnsureHandAnchor(); // 위에서 앵커를 못 잡았으면(FP 팔 없음 등) 카메라 하위에 폴백 앵커 생성

        m_itemUser.OnEquippedItemChanged += RefreshHeldModel;
        RefreshHeldModel(m_itemUser.EquippedItem);
    }

    // FP 팔(m_handsModel)을 1인칭 뷰모델로 셋업한다. 흔들림의 기준 포즈를 캡처하고, Viewmodel 레이어로
    // 올리고, 아이템 앵커·손가락 본을 손에서 런타임에 찾는다 — 에디터 툴로 팔을 재생성해 프리팹 내부
    // fileID가 바뀌어도 이 런타임 재해결 덕분에 배선이 깨지지 않는다. (#265)
    private void SetupHandViewmodel()
    {
        if (m_handsModel == null)
        {
            return;
        }

        m_handsModel.SetActive(true);
        m_handBasePos = m_handsModel.transform.localPosition;
        m_handBaseRot = m_handsModel.transform.localRotation;
        m_hasHandBase = true;

        int viewmodelLayer = LayerMask.NameToLayer("Viewmodel");
        if (viewmodelLayer >= 0)
        {
            PlayerMovement.SetLayerRecursively(m_handsModel.transform, viewmodelLayer);
        }

        if (m_handAnchor == null)
        {
            m_handAnchor = FindChildByName(m_handsModel.transform, "HeldItemAnchor");
        }

        CacheFingerJoints();
    }

    public override void OnNetworkDespawn()
    {
        if (m_itemUser != null)
        {
            m_itemUser.OnEquippedItemChanged -= RefreshHeldModel;
        }
    }

    // 오너 전용(비오너는 OnNetworkSpawn에서 enabled=false). 손 뷰모델에 절차적 흔들림을 준다. (#265)
    private void Update()
    {
        if (!m_hasHandBase || m_handsModel == null)
        {
            return;
        }

        Vector3 horizontalVelocity = m_controller != null ? m_controller.velocity : Vector3.zero;
        horizontalVelocity.y = 0f;
        float speedT = Mathf.Clamp01(horizontalVelocity.magnitude / k_bobRefSpeed);

        m_bobTime += Time.deltaTime * (k_idleFreq + speedT * k_walkFreq);
        float amp = k_idleAmp + speedT * k_walkAmp;

        // 좌우(cos)·상하(sin 2배 주기) = 걸음마다 8자를 그리는 전형적 뷰모델 bob
        float x = Mathf.Cos(m_bobTime) * amp;
        float y = Mathf.Sin(m_bobTime * 2f) * amp;

        m_handsModel.transform.localPosition = m_handBasePos + new Vector3(x, y, 0f);
        m_handsModel.transform.localRotation =
            m_handBaseRot * Quaternion.Euler(y * k_swayTiltDegrees, x * k_swayTiltDegrees, 0f);
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

        // 손가락 모양을 아이템 그립에 맞춘다 — 빈손은 Relaxed (#265)
        ApplyGrip(item != null ? item.HandGrip : HandGrip.Relaxed);

        if (item == null || item.HeldModelPrefab == null)
        {
            return; // 빈손이거나 표시 모델이 없는 아이템 — 손만 보인다
        }

        m_heldModelInstance = Instantiate(item.HeldModelPrefab, m_handAnchor, false);

        // 손 앵커가 FP 팔의 Hand_R 본이면 아이템마다 다른 그립을 맞춰야 한다 —
        // 3인칭 표시(#151)와 같은 그립 오프셋을 그대로 써 손에 동일하게 들리게 한다. (#265)
        m_heldModelInstance.transform.localPosition = item.HeldPositionOffset;
        m_heldModelInstance.transform.localRotation = Quaternion.Euler(item.HeldRotationOffset);

        // 표시 전용 인스턴스 — 콜라이더가 필요 없으니 아예 제거한다. 끄기만 하면 손 본(Synty 오른손은
        // 미러링돼 스케일이 음수)에 붙었을 때 BoxCollider가 음수 스케일 경고를 계속 뱉는다. (#265)
        foreach (Collider heldCollider in m_heldModelInstance.GetComponentsInChildren<Collider>(true))
        {
            Destroy(heldCollider);
        }

        // 든 아이템도 FP 팔과 같은 Viewmodel 레이어로 — 뷰모델 오버레이 카메라가 월드 위에 덧그려 벽을 뚫지 않게 한다 (#265)
        int viewmodelLayer = LayerMask.NameToLayer("Viewmodel");
        if (viewmodelLayer >= 0)
        {
            PlayerMovement.SetLayerRecursively(m_heldModelInstance.transform, viewmodelLayer);
        }
    }

    // m_handsModel 하위에서 이름으로 트랜스폼을 찾는다 (손 본·아이템 앵커 런타임 해결용).
    private static Transform FindChildByName(Transform root, string childName)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == childName)
            {
                return t;
            }
        }
        return null;
    }

    // FP 손(m_handsModel)의 손가락 본을 수집하고 바인드 회전을 기억해 둔다.
    // Synty 로우폴리 손은 손가락 그룹 3개(Finger=중지열·Index·Thumb), 각 최대 3관절.
    private void CacheFingerJoints()
    {
        var joints = new System.Collections.Generic.List<FingerJoint>();
        if (m_handsModel != null)
        {
            Transform hand = FindChildByName(m_handsModel.transform, "Hand_R");

            if (hand != null)
            {
                foreach (Transform chainRoot in hand)
                {
                    FingerKind kind;
                    if (chainRoot.name.StartsWith("Thumb")) kind = FingerKind.Thumb;
                    else if (chainRoot.name.StartsWith("Index")) kind = FingerKind.Index;
                    else if (chainRoot.name.StartsWith("Finger")) kind = FingerKind.Finger;
                    else continue; // HeldItemAnchor 등 손가락 아닌 자식은 건너뛴다

                    Transform seg = chainRoot;
                    for (int depth = 0; seg != null && depth < 3; depth++)
                    {
                        joints.Add(new FingerJoint { Bone = seg, BaseRotation = seg.localRotation, Kind = kind });
                        seg = seg.childCount > 0 ? seg.GetChild(0) : null;
                    }
                }
            }
        }
        m_fingerJoints = joints.ToArray();
    }

    // 장착 아이템의 그립 프리셋에 맞춰 손가락을 굽힌다. 바인드 회전 위에 로컬 X 굽힘을 얹는다.
    private void ApplyGrip(HandGrip grip)
    {
        if (m_fingerJoints == null) return;
        foreach (FingerJoint j in m_fingerJoints)
        {
            if (j.Bone == null) continue;
            j.Bone.localRotation = j.BaseRotation * Quaternion.Euler(CurlEuler(grip, j.Kind));
        }
    }

    // 프리셋별 손가락 굽힘 오일러 각(도). 눈대중 초기값 — 아트가 보고 조정한다. (#265)
    // X = 굽힘(손바닥·물건 안쪽으로 말림), Y = 비틀기, Z = 좌우 벌림(엄지를 총 밖으로 빼는 축).
    private static Vector3 CurlEuler(HandGrip grip, FingerKind kind)
    {
        switch (grip)
        {
            case HandGrip.Trigger: // 총류 — 검지 걸치고 나머지 감쌈, 엄지는 굽히되 바깥으로 벌려 총을 안 뚫게
                if (kind == FingerKind.Index) return new Vector3(20f, 0f, 0f);
                if (kind == FingerKind.Thumb) return new Vector3(50f, 0f, 30f);
                return new Vector3(58f, 0f, 0f);
            case HandGrip.Wide: // 큰 물건 — 손 넓게
                return kind == FingerKind.Thumb ? new Vector3(6f, 0f, 0f) : new Vector3(8f, 0f, 0f);
            default: // Relaxed — 자연스럽게 살짝 쥠
                return kind == FingerKind.Thumb ? new Vector3(15f, 0f, 0f) : new Vector3(20f, 0f, 0f);
        }
    }
}
