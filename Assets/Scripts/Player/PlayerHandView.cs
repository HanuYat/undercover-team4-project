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

    // 뷰모델 스윙(#217) — 3인칭은 상체 레이어 클립(UpperBodyAttack)이 처리하지만, 1인칭 팔은
    // 애니메이터도 아바타도 없는 정적 스킨드 메시라(FPArmGenerator가 그렇게 뽑는다) 그 클립이 오지 않는다.
    // 그래서 같은 타격 이벤트로 팔 루트를 직접 흔들어 내 화면 몫의 스윙을 만든다.
    // 팔 루트 원점이 곧 어깨다(FPArmGenerator가 skeletonTop을 shoulder 위치만큼 당겨 놓는다) —
    // 덕분에 이 회전은 어깨를 축으로 팔 전체가 도는 그림이 되고, 손 본에 붙은 아이템도 함께 휘둘린다.
    // 길이·임팩트 시점은 직접 정하지 않고 PlayerAnimationDriver의 공용 상수에서 뽑는다 —
    // 3인칭 클립도 같은 값에 맞춰 배속되므로, 이래야 내 화면과 남의 화면의 타격 순간이 일치한다.
    // (여기 숫자를 상수로 되돌리지 말 것. 그 순간부터 양쪽이 조용히 갈라진다)
    private const float k_swingDuration = PlayerAnimationDriver.k_swingSeconds;
    private const float k_swingStrikeEnd =
        PlayerAnimationDriver.k_swingImpactSeconds / PlayerAnimationDriver.k_swingSeconds; // 임팩트

    // ---- 스윙 튜닝 (인스펙터) ----
    //
    // 회전은 팔 로컬이 아니라 카메라(부모) 축 기준이다 — Update가 base 회전 <b>앞에</b> 곱한다.
    // X = 위아래(음수가 들어올림), Y = 좌우, Z = 롤.
    //
    // 각도가 작아 보여도 회전축이 어깨라 화면에서는 크게 움직인다. 팔이 기준 자세부터 이미
    // 화면 오른쪽 아래(뷰포트 약 0.80, 0.21)에 있어서, 키우면 손이 곧장 프레임 밖으로 나간다.
    // <b>조정할 때는 '손이 화면에 남아 있는지'를 가장 먼저 볼 것</b> — 손만 빠지고 봉은 남으면
    // 봉이 허공에 떠 보인다. 기본값의 실측 여유는 손 최저 y=0.044 / 최대 x=0.942다.
    //
    // 휘두르는 '맛'은 대부분 롤(Z)에서 나온다. X·Y는 손을 화면에서 옮길 뿐이고, 든 물건이 실제로
    // 회전해 보이게 만드는 건 롤이다 — 롤 없이 X·Y만 키우면 봉이 각도를 유지한 채 미끄러져
    // 휘두르는 게 아니라 흔드는 그림이 된다. 게다가 롤은 손 위치를 거의 안 건드린다(계측: 롤을
    // 26→40으로 14도 더 줘도 손은 0.06→0.04, 봉 기울기는 -41°→-49°). 그래서 <b>역동성은 롤에서 벌고
    // X·Y는 아낀다.</b> 부호 주의 — 롤 음수가 봉을 오른쪽으로 세우고, 양수가 좌하로 넘긴다.
    //
    // 기본값에서 봉의 화면상 기울기가 -9°(준비) → -53°(임팩트) → -66°(팔로스루)로 돈다.

    [Header("스윙 포즈 — 준비 (#217)")]
    [Tooltip("오른쪽 위로 세워 젖히는 자세. 카메라 축 기준 회전(도)")]
    [SerializeField]
    private Vector3 m_swingWindupEuler = new Vector3(-12f, 8f, -28f);

    [Tooltip("몸쪽으로 당기는 위치 오프셋(m)")]
    [SerializeField]
    private Vector3 m_swingWindupOffset = new Vector3(0.015f, 0.005f, -0.06f);

    [Header("스윙 포즈 — 임팩트")]
    [Tooltip("우상 → 좌하 대각 내려치기. 맞는 순간의 자세다")]
    [SerializeField]
    private Vector3 m_swingStrikeEuler = new Vector3(8f, -18f, 46f);

    [Tooltip("앞으로 뻗으며 치는 위치 오프셋(m)")]
    [SerializeField]
    private Vector3 m_swingStrikeOffset = new Vector3(-0.035f, -0.025f, 0.085f);

    [Header("스윙 포즈 — 팔로스루")]
    // 팔로스루는 아래(X)가 아니라 옆(Y)과 롤로 흘린다 — 임팩트 자세가 이미 화면 아래쪽(손 y≈0.04)이라
    // 여기서 더 숙이면 주먹만 프레임 밖으로 빠진다(계측: X를 9로 두면 손 y=-0.03). X를 낮추고
    // 그만큼 Y·롤로 옮기면 손은 y≈0.08로 남으면서 봉은 오히려 더 돈다(-60° → -66°).
    [Tooltip("임팩트 뒤 흘러나가는 자세. X(아래)보다 Y·롤로 흘릴 것 — 아래로 파면 손이 화면에서 빠진다")]
    [SerializeField]
    private Vector3 m_swingFollowEuler = new Vector3(5f, -24f, 64f);

    [Tooltip("힘이 빠지며 옆으로 흘리는 위치 오프셋(m)")]
    [SerializeField]
    private Vector3 m_swingFollowOffset = new Vector3(-0.05f, 0.01f, 0.04f);

    [Header("스윙 구간 비율")]
    // 임팩트 시점(k_swingStrikeEnd)과 전체 길이는 3인칭·데미지와 공유하는 값이라 여기서 못 바꾼다 —
    // PlayerAnimationDriver의 상수다. 아래 둘은 그 안에서 준비/팔로스루가 차지하는 몫일 뿐이라
    // 마음대로 만져도 싱크가 깨지지 않는다.
    [Tooltip("임팩트까지의 시간 중 준비 동작이 차지하는 비율. 작을수록 늦게 젖혔다 급히 친다")]
    [SerializeField]
    [Range(0.15f, 0.85f)]
    private float m_swingWindupFraction = 0.49f;

    [Tooltip("임팩트 이후 남은 시간 중 팔로스루가 차지하는 비율. 나머지는 기본 자세로 회수")]
    [SerializeField]
    [Range(0.05f, 0.9f)]
    private float m_swingFollowFraction = 0.3f;

    [Header("스윙 가속 곡선")]
    // 구간마다 곡선이 다른 것이 역동성의 핵심이다. 전부 S자(EaseInOut)로 깔면 내려치기가
    // 임팩트 직전에 감속해서 — 가장 빨라야 할 순간에 브레이크를 밟는 셈이라 — 휘두르는 게 아니라
    // 훑는 것처럼 보인다. 기본값은 코드로 계산하던 것과 정확히 같은 모양이다(준비 1-(1-u)²,
    // 내려침 u², 팔로스루 1-(1-u)², 회수 smoothstep).
    //
    // 경계에서 속도가 이어지도록 접선을 맞출 것. 준비 끝과 내려침 시작은 둘 다 0,
    // <b>임팩트(내려침 끝 ↔ 팔로스루 시작)는 둘 다 최고 속도</b>여야 한다 — 여기서 0이 되면
    // 봉이 맞는 순간 허공에 멈춰 선다.
    [Tooltip("준비 — 빠르게 젖혔다 멎는다(감속으로 끝날 것)")]
    [SerializeField]
    private AnimationCurve m_swingWindupCurve = new AnimationCurve(
        new Keyframe(0f, 0f, 2f, 2f),
        new Keyframe(1f, 1f, 0f, 0f)
    );

    [Tooltip("내려침 — 임팩트가 최고 속도가 되도록 가속으로 끝낼 것. 여기를 S자로 바꾸면 타격감이 죽는다")]
    [SerializeField]
    private AnimationCurve m_swingStrikeCurve = new AnimationCurve(
        new Keyframe(0f, 0f, 0f, 0f),
        new Keyframe(1f, 1f, 2f, 2f)
    );

    [Tooltip("팔로스루 — 최고 속도로 이어받아 힘이 풀리며 멎는다")]
    [SerializeField]
    private AnimationCurve m_swingFollowCurve = new AnimationCurve(
        new Keyframe(0f, 0f, 2f, 2f),
        new Keyframe(1f, 1f, 0f, 0f)
    );

    [Tooltip("회수 — 기본 자세로 조용히 복귀. 여기서 튀면 다음 스윙이 지저분해진다")]
    [SerializeField]
    private AnimationCurve m_swingRecoverCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

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
    private float m_swingTime = -1f; // 스윙 경과 시간(초). 음수 = 진행 중 아님

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

    /// <summary>
    /// 1인칭 팔에 타격 스윙 1회를 재생한다 — 3인칭 상체 클립과 짝을 이루는 내 화면 몫이다. (#217)
    /// 타격 이벤트 하나로 둘이 함께 돌도록 <see cref="PlayerAnimationDriver.TriggerAttack"/>이 불러 준다.
    /// 비오너 인스턴스는 OnNetworkSpawn에서 enabled=false라 여기서 곧장 빠진다 — 남의 1인칭 팔은
    /// 애초에 표시되지 않으므로 흔들 것도 없다(남이 보는 스윙은 3인칭 클립이 담당).
    /// </summary>
    public void PlaySwing()
    {
        if (!enabled || m_handsModel == null)
        {
            return;
        }

        // 진행 중이어도 처음부터 다시 시작한다 — 서버 쿨다운(0.9초)이 스윙 길이(0.55초)보다 길어
        // 정상 경로에서는 겹치지 않지만, 겹칠 땐 새 타격을 보여주는 쪽이 맞다.
        m_swingTime = 0f;
    }

    // 오너 전용(비오너는 OnNetworkSpawn에서 enabled=false). 손 뷰모델에 절차적 흔들림(#265)과
    // 타격 스윙(#217)을 얹는다.
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

        // 스윙은 bob 위에 얹는다 — 걷거나 점프하면서 휘둘러도 두 움직임이 함께 살아 있어야 한다.
        // 회전을 base 앞에 곱해 팔 로컬이 아닌 카메라 축으로 돌린다(= 어깨를 축으로 한 스윙).
        AdvanceSwingTime();
        EvaluateSwingPose(out Vector3 swingOffset, out Quaternion swingRotation);

        m_handsModel.transform.localPosition = m_handBasePos + new Vector3(x, y, 0f) + swingOffset;
        m_handsModel.transform.localRotation =
            swingRotation
            * m_handBaseRot
            * Quaternion.Euler(y * k_swayTiltDegrees, x * k_swayTiltDegrees, 0f);
    }

    // 스윙 타이머를 한 프레임 진행시킨다 — Update에서 <b>프레임당 정확히 한 번만</b> 부를 것.
    // 두 번 부르면 스윙이 2배 속도로 간다. 진행 중이 아니면 아무 일도 하지 않는다.
    private void AdvanceSwingTime()
    {
        if (m_swingTime < 0f)
        {
            return;
        }

        m_swingTime += Time.deltaTime;
        if (m_swingTime >= k_swingDuration)
        {
            m_swingTime = -1f; // 끝 — 기준 포즈로 복귀
        }
    }

    /// <summary>
    /// 지금 스윙 진행도에 해당하는 포즈를 낸다 — 상태를 바꾸지 않는다(타이머는 AdvanceSwingTime 담당).
    /// 스윙 중이 아니면 무변화(오프셋 0, 항등 회전).
    /// </summary>
    private void EvaluateSwingPose(out Vector3 offset, out Quaternion rotation)
    {
        offset = Vector3.zero;
        rotation = Quaternion.identity;

        if (m_swingTime < 0f)
        {
            return;
        }

        float t = m_swingTime / k_swingDuration;

        // 준비 → 임팩트 → 팔로스루 → 회수 4구간. 구간별 곡선은 인스펙터에서 튜닝한다
        // (기본값과 각 구간이 왜 그 모양이어야 하는지는 필드 선언부 주석 참고).
        // 임팩트 시점(k_swingStrikeEnd)만 3인칭·데미지와 공유하는 고정값이고, 나머지 경계는
        // 인스펙터 비율에서 나온다.
        float windupEnd = k_swingStrikeEnd * m_swingWindupFraction;
        float followEnd = k_swingStrikeEnd + (1f - k_swingStrikeEnd) * m_swingFollowFraction;

        Vector3 euler;
        if (t < windupEnd)
        {
            float u = Ease(m_swingWindupCurve, t / windupEnd);
            euler = Vector3.Lerp(Vector3.zero, m_swingWindupEuler, u);
            offset = Vector3.Lerp(Vector3.zero, m_swingWindupOffset, u);
        }
        else if (t < k_swingStrikeEnd)
        {
            float u = Ease(m_swingStrikeCurve, (t - windupEnd) / (k_swingStrikeEnd - windupEnd));
            euler = Vector3.Lerp(m_swingWindupEuler, m_swingStrikeEuler, u);
            offset = Vector3.Lerp(m_swingWindupOffset, m_swingStrikeOffset, u);
        }
        else if (t < followEnd)
        {
            float u = Ease(m_swingFollowCurve, (t - k_swingStrikeEnd) / (followEnd - k_swingStrikeEnd));
            euler = Vector3.Lerp(m_swingStrikeEuler, m_swingFollowEuler, u);
            offset = Vector3.Lerp(m_swingStrikeOffset, m_swingFollowOffset, u);
        }
        else
        {
            float u = Ease(m_swingRecoverCurve, (t - followEnd) / (1f - followEnd));
            euler = Vector3.Lerp(m_swingFollowEuler, Vector3.zero, u);
            offset = Vector3.Lerp(m_swingFollowOffset, Vector3.zero, u);
        }

        rotation = Quaternion.Euler(euler);
    }

    // 커브를 평가하되 키를 다 지운 커브는 선형으로 처리한다 — 인스펙터에서 실수로 비웠을 때
    // 팔이 한 자세에 굳는 대신 어색하게나마 움직이게 두는 편이 원인을 찾기 쉽다.
    private static float Ease(AnimationCurve curve, float u)
    {
        return curve != null && curve.length >= 2 ? curve.Evaluate(u) : u;
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
            case HandGrip.Handle: // 자루형(진압봉) — 검지까지 다섯 손가락을 같은 깊이로 말아 자루를 감싼다.
                // Trigger보다 깊게 쥐는 이유: 걸칠 방아쇠가 없고, 스윙 중(#217) 얕게 쥐면 자루가 손에서
                // 겉도는 게 눈에 띈다. 엄지는 Z를 음수로 줘 바깥으로 벌리지 않고 자루 위를 덮게 한다.
                return kind == FingerKind.Thumb ? new Vector3(45f, 0f, -12f) : new Vector3(72f, 0f, 0f);
            default: // Relaxed — 자연스럽게 살짝 쥠
                return kind == FingerKind.Thumb ? new Vector3(15f, 0f, 0f) : new Vector3(20f, 0f, 0f);
        }
    }
}
