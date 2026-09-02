using Unity.Netcode;
using UnityEngine;

// 1인칭 손·장착 아이템 표시(#45). 오너 카메라 하위 손 앵커에 손 모델을 표시하고, PlayerItemUser의
// 장착 변경 이벤트를 구독해 HeldModelPrefab을 손에 갈아끼운다. 순수 시각 표현이라 아이템 사용 로직에는
// 관여하지 않는다. 1인칭(오너 로컬) 전용 — 3인칭 장착 표시·네트워크 동기화는 PlayerHeldItemView가 맡는다.
[RequireComponent(typeof(PlayerItemUser))]
public class PlayerHandView : NetworkBehaviour
{
    [Header("손 앵커 (비우면 카메라 하위에 자동 생성)")]
    [SerializeField]
    private Transform m_handAnchor;

    [Header("1인칭 손 모델 (선택)")]
    [Tooltip("카메라 하위에 배치할 것. PlayerLook의 OwnBody 루트 바깥에 둬야 내 카메라에 보인다")]
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
    private const float k_bobSpeedDamp = 12f; // 흔들림 세기가 목표를 좇는 속도(1/초) (#964)

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

    // 피격 킥(#476) — 맞은 순간 팔이 한 번 튕겼다 감쇠 진동으로 돌아온다. 스윙과 달리 3인칭 짝이 없는
    // 순수 1인칭 연출이라 공용 상수에 묶이지 않는다(맞은 몸의 3인칭 표현은 Knockdown 모션이 맡는다).
    // 스윙(0.64초)보다 짧게 잡는다 — 길면 연타로 맞을 때 팔이 계속 흔들려 조준이 불가능해진다.
    private const float k_hitShakeDuration = 0.22f;
    private const float k_hitShakeDegrees = 7f; // 진폭(도). 어깨를 축으로 도는 회전이라 작아도 화면에서는 크다
    private const float k_hitShakeOscillations = 1.5f; // 감쇠하는 동안의 진동 횟수
    private const float k_hitShakeOffset = 0.02f; // 회전과 함께 손이 밀리는 거리(m)

    // 감전 경련(#477) — 피격 킥과 달리 지속형이다. 주파수와 파형은 ShockShake가 카메라와 공유하고
    // 여기서는 진폭만 정한다. 카메라보다 크게 잡는 이유는 손이 화면 안 물체라 같은 각도로는
    // 덜 움직여 보이기 때문이다.
    private const float k_convulsionDegrees = 3.2f;
    private const float k_convulsionOffset = 0.006f;

    // ---- 스윙 튜닝 (인스펙터) ----
    // 회전은 팔 로컬이 아니라 카메라(부모) 축 기준(Update가 base 회전 앞에 곱함). X=위아래, Y=좌우, Z=롤.
    // 회전축이 어깨라 각도가 작아도 화면에서는 크게 움직인다. 기본 자세가 이미 화면 오른쪽 아래
    // (뷰포트 약 0.80, 0.21)라 키우면 손이 프레임 밖으로 나가기 쉽다 — 조정 시 손이 화면에 남는지부터
    // 확인할 것(실측 여유: 손 최저 y=0.044, 최대 x=0.942).
    // 휘두르는 '맛'은 대부분 롤(Z)에서 나온다. X·Y는 손 위치만 옮기고, 회전해 보이게 만드는 건 롤이다 —
    // 롤 없이 X·Y만 키우면 흔드는 그림이 된다. 롤은 손 위치를 거의 안 건드리므로(실측: 26→40으로
    // 14도 더 줘도 손은 0.06→0.04, 봉 기울기는 -41°→-49°) 역동성은 롤에서 벌고 X·Y는 아낀다.
    // 부호: 롤 음수=봉이 오른쪽으로, 양수=좌하로.
    // 기본값 화면 기울기: -9°(준비) → -53°(임팩트) → -66°(팔로스루).

    [Header("스윙 포즈 — 준비 (#217)")]
    [Tooltip("오른쪽 위로 세워 젖히는 자세. 카메라 축 기준 회전(도)")]
    [SerializeField]
    private Vector3 m_swingWindupEuler = new Vector3(-32f, 3f, -24f);

    [Tooltip("몸쪽으로 당기는 위치 오프셋(m)")]
    [SerializeField]
    private Vector3 m_swingWindupOffset = new Vector3(0.015f, 0.06f, -0.05f);

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
    private float m_swingWindupFraction = 0.62f;

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
    // 경계에서 속도가 이어지도록 접선을 맞출 것. 준비 끝과 내려침 시작은 둘 다 0, 임팩트(내려침 끝 ↔
    // 팔로스루 시작)는 둘 다 최고 속도여야 한다 — 여기서 0이 되면 봉이 맞는 순간 허공에 멈춰 선다.
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

    // 1인칭 손을 그리는 카메라와 월드를 그리는 카메라. 둘은 트랜스폼이 같아도 FOV가 달라
    // 같은 월드 점의 화면 위치가 어긋난다 — 근거·계산은 docs/828-rope-first-person.md. (#828)
    private Camera m_worldCamera;
    private Camera m_viewmodelCamera;

    // FP 손 손가락 프리셋 — 장착 아이템의 HandGrip에 맞춰 손가락을 굽힌다(#265). 손 본이 통짜 스킨드
    // 메시라 애니메이터 없이 본을 직접 회전한다.
    // 축(#428): 굽힘=본 로컬 Z(+가 손바닥 안쪽), 벌림=Y, X는 안 쓴다. 손가락 본이 로컬 +X로 뻗어 있어
    // X 회전은 굽힘이 아니라 길이축 롤이다 — 키워도 안 굽고 살만 꼬인다. 되돌리지 말 것.
    // 엄지만 -Y가 굽힘이다(감기는 게 아니라 손바닥을 가로질러 넘어오는 대립 운동이라). 근거(3관절에
    // ±40° 먹이고 잰 손끝↔엄지끝 거리, 기준 0.156): +Z 0.138 / -Z 0.189 / 엄지 -Y 0.090.
    private enum FingerKind { Finger, Index, Thumb }

    private struct FingerJoint
    {
        public Transform Bone;
        public Quaternion BaseRotation; // 바인드 로컬 회전 — 프리셋 굽힘을 이 위에 얹는다
        public FingerKind Kind;
        public int Depth; // 체인에서 몇 번째 마디인가(0 = 손에 붙은 뿌리). 깊이별 감쇠에 쓴다 (#428)
    }

    private FingerJoint[] m_fingerJoints;

    // 마디별 굽힘 몫 (#428). 프리셋 각도는 '뿌리 마디 기준'이고 깊은 마디는 이 비율만큼만 굽는다 —
    // 3마디에 같은 각을 그대로 얹으면 합이 3배가 돼 손끝이 손바닥을 뚫는다.
    private static readonly float[] s_fingerDepthWeights = { 1f, 0.75f, 0.5f };

    // 엄지 뿌리는 중수골(손목 관절)이라 크게 돌리면 엄지가 통째로 손목에서 스윙한다 — 몫을 확 줄이고
    // 쥐는 힘은 아래 두 마디에서 낸다.
    private static readonly float[] s_thumbDepthWeights = { 0.25f, 0.9f, 0.7f };

    // 검지를 방아쇠에 맞출 때 훑는 범위·간격 (#428). 60도를 넘기면 검지가 방아쇠를 지나 총 안으로 말린다.
    private const float k_indexAimMaxDegrees = 60f;
    private const float k_indexAimStepDegrees = 2.5f;

    // 뷰모델 흔들림 상태 — 손 모델의 기준 로컬 포즈에 매 프레임 오프셋을 얹는다
    private CharacterController m_controller;
    private Vector3 m_handBasePos;
    private Quaternion m_handBaseRot;
    private bool m_hasHandBase; // 스폰에서 base 포즈를 캡처했는지 — Update가 스폰 전에 먼저 돌아 손을 원점으로 옮기는 것을 막는다
    private float m_bobTime;
    private float m_bobSpeedT; // 적용 중인 흔들림 세기(0~1) (#964)
    private float m_swingTime = -1f; // 스윙 경과 시간(초). 음수 = 진행 중 아님
    private float m_hitShakeTime = -1f; // 피격 킥 경과 시간(초). 음수 = 진행 중 아님 (#476)
    private Vector3 m_hitShakeAxis; // 이번 피격 킥의 회전축 — 맞을 때마다 다르게 뽑아 같은 그림이 반복되지 않게 한다
    private float m_convulsionIntensity; // 감전 경련 강도(지속형). 0 = 떨지 않음 (#477)

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

        CacheCameras(); // 손을 그리는 카메라·월드를 그리는 카메라 (#828)

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
            PlayerLook.SetLayerRecursively(m_handsModel.transform, viewmodelLayer);
        }

        if (m_handAnchor == null)
        {
            m_handAnchor = FindChildByName(m_handsModel.transform, "HeldItemAnchor");
        }

        CacheFingerJoints();
    }

    // 손을 그리는 두 카메라를 각자의 컬링 마스크로 구분해 캐시한다 — Viewmodel 레이어를
    // 그리는 쪽이 1인칭 팔 카메라, 아닌 쪽이 월드(=밧줄 LineRenderer)를 그리는 카메라다.
    // 이름이 아니라 마스크로 가르는 이유는 씬 계층 순서에 기대지 않기 위해서다. (#828)
    private void CacheCameras()
    {
        int viewmodelLayer = LayerMask.NameToLayer("Viewmodel");
        if (viewmodelLayer < 0)
        {
            return; // 레이어가 없는 구성(테스트 씬 등) — 이후 TryGetHandWorldPoint가 폴백을 낸다
        }

        int viewmodelBit = 1 << viewmodelLayer;
        foreach (Camera cam in GetComponentsInChildren<Camera>(true))
        {
            if ((cam.cullingMask & viewmodelBit) != 0)
                m_viewmodelCamera = cam;
            else
                m_worldCamera = cam;
        }
    }

    // 1인칭 손을 메인 카메라 월드 좌표로 환산해 낸다 — 밧줄(#269) 시작점용. 실패하면 3인칭 손 앵커로
    // 폴백. 두 카메라 FOV가 달라 좌표를 그대로 못 쓰는 이유는 docs/828-rope-first-person.md 참고.
    // depth: 시작점을 놓을 카메라 앞 거리(m), 0 이하면 손 자체 깊이. viewportOffsetX: 화면 가로 보정(뷰포트 비율, 음수=왼쪽).
    public bool TryGetHandWorldPoint(float depth, out Vector3 point, float viewportOffsetX = 0f)
    {
        point = default;

        if (!enabled || m_handAnchor == null || m_worldCamera == null)
        {
            return false; // 비오너·앵커 미해결 — 3인칭으로 폴백
        }

        // 감정표현(#219)·사망 관전(#576)으로 3인칭에 빠지면 SetViewmodelVisible(false)가 이 팔을
        // 끈다 — 그 상태를 그대로 "지금은 3인칭 시점"의 신호로 재사용한다.
        if (m_handsModel != null && !m_handsModel.activeInHierarchy)
        {
            return false;
        }

        Camera viewCamera = m_viewmodelCamera != null ? m_viewmodelCamera : m_worldCamera;
        Vector3 viewport = viewCamera.WorldToViewportPoint(m_handAnchor.position);
        viewport.x += viewportOffsetX;
        viewport.z = depth > 0f ? depth : viewport.z;
        point = m_worldCamera.ViewportToWorldPoint(viewport);
        return true;
    }

    public override void OnNetworkDespawn()
    {
        if (m_itemUser != null)
        {
            m_itemUser.OnEquippedItemChanged -= RefreshHeldModel;
        }
    }

    // 1인칭 뷰모델(팔·든 아이템) 표시 전환 — 감정표현 3인칭 전환(#219)이 쓴다. 팔이 카메라 자식이라
    // 안 끄면 3인칭 화면에 허공에 뜬 채로 남는다. 레이어가 아니라 오브젝트를 끄는 건 SetupHandViewmodel이
    // 이미 Viewmodel 레이어로 옮겨 뒀으니 복원할 게 없어야 해서. 비오너는 애초에 뷰모델이 없어 무해하다.
    public void SetViewmodelVisible(bool visible)
    {
        if (!enabled || m_handsModel == null)
        {
            return;
        }

        m_handsModel.SetActive(visible);
    }

    // 1인칭 팔 타격 스윙 1회 재생(#217) — 3인칭 상체 클립과 짝을 이루는 내 화면 몫.
    // PlayerAnimationDriver.TriggerAttack이 같은 타격 이벤트로 호출한다. 비오너는 enabled=false라 무해.
    public void PlaySwing()
    {
        if (!enabled || m_handsModel == null)
        {
            return;
        }

        // 진행 중이어도 처음부터 다시 시작한다 — 서버 쿨다운(0.9초)이 스윙 길이(0.64초)보다 길어
        // 정상 경로에서는 겹치지 않지만, 겹칠 땐 새 타격을 보여주는 쪽이 맞다.
        m_swingTime = 0f;
    }

    // 맞은 순간 1인칭 팔을 튕긴다(#476) — PlayerHitView가 오너 화면에서만 호출.
    // 스윙과 독립이라 휘두르는 도중에 맞아도 둘 다 재생된다(Update가 두 포즈를 곱해 얹는다).
    public void PlayHitShake()
    {
        if (!enabled || m_handsModel == null)
        {
            return;
        }

        // 축을 매번 다시 뽑는다 — 고정하면 연타로 맞을 때 같은 방향으로만 튕겨 기계적으로 보인다.
        // 카메라 축 기준이므로 X=위아래, Y=좌우, Z=롤. 롤을 크게 줘 '휘청'이 잘 읽히게 한다.
        m_hitShakeAxis = new Vector3(
            Random.Range(-1f, 1f),
            Random.Range(-1f, 1f),
            Random.Range(-1f, 1f) * 1.5f
        ).normalized;

        m_hitShakeTime = 0f; // 진행 중이어도 처음부터 — 새로 맞은 것이 우선이다 (스윙과 같은 방침)
    }

    // 감전 경련 강도 설정(#477, 0=안 떪) — 매 프레임 갱신하는 지속형 값.
    // 단발 감쇠 진동인 PlayHitShake와 별개로 동시에 걸릴 수 있다(맞고 쓰러지는 순간이 그렇다).
    public void SetConvulsion(float intensity) =>
        m_convulsionIntensity = Mathf.Clamp01(intensity);

    // 오너 전용(비오너는 OnNetworkSpawn에서 enabled=false). 손 뷰모델에 절차적 흔들림(#265)과
    // 타격 스윙(#217), 피격 킥(#476)을 얹는다.
    private void Update()
    {
        if (!m_hasHandBase || m_handsModel == null)
        {
            return;
        }

        // CC가 꺼지면 velocity는 직전 값에 얼어붙는다 — 끌려가는 중은 걷는 중이 아니라 0으로 본다 (#964)
        float targetSpeedT = 0f;
        if (m_controller != null && m_controller.enabled)
        {
            Vector3 horizontalVelocity = m_controller.velocity;
            horizontalVelocity.y = 0f;
            targetSpeedT = Mathf.Clamp01(horizontalVelocity.magnitude / k_bobRefSpeed);
        }

        // 한 프레임에 끊으면 팔이 튄다 — 잦아들게 좇는다
        m_bobSpeedT = Mathf.Lerp(
            m_bobSpeedT, targetSpeedT, 1f - Mathf.Exp(-k_bobSpeedDamp * Time.deltaTime));

        m_bobTime += Time.deltaTime * (k_idleFreq + m_bobSpeedT * k_walkFreq);
        float amp = k_idleAmp + m_bobSpeedT * k_walkAmp;

        // 좌우(cos)·상하(sin 2배 주기) = 걸음마다 8자를 그리는 전형적 뷰모델 bob
        float x = Mathf.Cos(m_bobTime) * amp;
        float y = Mathf.Sin(m_bobTime * 2f) * amp;

        // 스윙은 bob 위에 얹는다 — 걷거나 점프하면서 휘둘러도 두 움직임이 함께 살아 있어야 한다.
        // 회전을 base 앞에 곱해 팔 로컬이 아닌 카메라 축으로 돌린다(= 어깨를 축으로 한 스윙).
        AdvanceSwingTime();
        EvaluateSwingPose(out Vector3 swingOffset, out Quaternion swingRotation);

        // 피격 킥도 같은 방식으로 얹는다(#476) — 스윙과 독립이라 휘두르는 도중에 맞으면 둘이 겹친다.
        // 킥을 스윙 바깥쪽에 곱해 스윙 포즈 전체를 통째로 흔든다(안쪽에 넣으면 궤적이 어긋나 보인다).
        AdvanceHitShakeTime();
        EvaluateHitShakePose(out Vector3 hitOffset, out Quaternion hitRotation);

        // 감전 경련(#477)도 같은 자리에 얹는다 — 단발 킥과 동시에 걸릴 수 있다(맞고 쓰러지는 순간).
        EvaluateConvulsionPose(out Vector3 shockOffset, out Quaternion shockRotation);

        m_handsModel.transform.localPosition =
            m_handBasePos + new Vector3(x, y, 0f) + swingOffset + hitOffset + shockOffset;
        m_handsModel.transform.localRotation =
            shockRotation
            * hitRotation
            * swingRotation
            * m_handBaseRot
            * Quaternion.Euler(y * k_swayTiltDegrees, x * k_swayTiltDegrees, 0f);
    }

    // 현재 감전 경련 강도의 포즈를 낸다(#477) — 지속형이라 타이머 없음(강도가 곧 상태).
    // 파형은 ShockShake가 카메라 떨림과 공유 — 주파수가 어긋나면 경련이 아니라 고장난 화면처럼 보인다.
    private void EvaluateConvulsionPose(out Vector3 offset, out Quaternion rotation)
    {
        ShockShake.Evaluate(
            m_convulsionIntensity,
            k_convulsionDegrees,
            k_convulsionOffset,
            out Vector3 euler,
            out offset
        );
        rotation = Quaternion.Euler(euler);
    }

    // 피격 킥 타이머를 한 프레임 진행시킨다 — AdvanceSwingTime과 같은 규약(프레임당 한 번만). (#476)
    private void AdvanceHitShakeTime()
    {
        if (m_hitShakeTime < 0f)
        {
            return;
        }

        m_hitShakeTime += Time.deltaTime;
        if (m_hitShakeTime >= k_hitShakeDuration)
        {
            m_hitShakeTime = -1f; // 끝 — 기준 포즈로 복귀
        }
    }

    // 현재 피격 킥 진행도의 포즈를 낸다(#476, 상태는 안 바꿈 — 타이머는 AdvanceHitShakeTime 담당).
    // 감쇠 진동: 맞은 순간 최대로 튀었다가 선형으로 줄며 수렴한다. 진행 중 아니면 무변화.
    private void EvaluateHitShakePose(out Vector3 offset, out Quaternion rotation)
    {
        offset = Vector3.zero;
        rotation = Quaternion.identity;

        if (m_hitShakeTime < 0f)
        {
            return;
        }

        float t = m_hitShakeTime / k_hitShakeDuration; // 0 → 1
        // sin으로 시작해야 t=0에서 진폭이 0이다 — cos으로 두면 첫 프레임에 팔이 순간이동한 것처럼 튄다.
        float wave = Mathf.Sin(t * Mathf.PI * 2f * k_hitShakeOscillations) * (1f - t);

        rotation = Quaternion.Euler(m_hitShakeAxis * (wave * k_hitShakeDegrees));
        offset = m_hitShakeAxis * (wave * k_hitShakeOffset);
    }

    // 스윙 타이머를 한 프레임 진행시킨다 — Update에서 프레임당 정확히 한 번만 부를 것.
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

    // 현재 스윙 진행도의 포즈를 낸다(상태는 안 바꿈 — 타이머는 AdvanceSwingTime 담당). 스윙 중 아니면 무변화.
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

        // 총이면 검지를 그 총의 방아쇠에 맞춘다 — 모델이 제자리를 잡은 뒤라야 방아쇠 위치가 확정된다 (#428)
        if (item.HandGrip == HandGrip.Trigger)
        {
            AimIndexAtTrigger(m_heldModelInstance);
        }

        // 표시 전용 인스턴스 — 콜라이더가 필요 없으니 아예 제거한다. 끄기만 하면 뷰모델이 물리·레이캐스트에
        // 계속 걸린다. (#265)
        foreach (Collider heldCollider in m_heldModelInstance.GetComponentsInChildren<Collider>(true))
        {
            Destroy(heldCollider);
        }

        // 든 아이템도 FP 팔과 같은 Viewmodel 레이어로 — 뷰모델 오버레이 카메라가 월드 위에 덧그려 벽을 뚫지 않게 한다 (#265)
        int viewmodelLayer = LayerMask.NameToLayer("Viewmodel");
        if (viewmodelLayer >= 0)
        {
            PlayerLook.SetLayerRecursively(m_heldModelInstance.transform, viewmodelLayer);
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

    // FP 손(m_handsModel)의 손가락 본을 수집하고 바인드 회전·체인 깊이를 기억해 둔다.
    // Synty 로우폴리 손은 손가락 그룹 3개 — Finger(중지·약지·소지를 뭉친 열)·Index·Thumb.
    // Finger/Index는 본이 4개(01~04)지만 01이 이미 너클이라 굽는 건 앞 3개고 04는 손끝 캡이다.
    // Thumb은 본이 3개(01=중수골·02·03)뿐이라 뿌리까지 담긴다 — 그 몫은 s_thumbDepthWeights가 줄인다.
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
                        joints.Add(new FingerJoint
                        {
                            Bone = seg,
                            BaseRotation = seg.localRotation,
                            Kind = kind,
                            Depth = depth,
                        });
                        seg = seg.childCount > 0 ? seg.GetChild(0) : null;
                    }
                }
            }
        }
        m_fingerJoints = joints.ToArray();
    }

    // 장착 아이템의 그립 프리셋에 맞춰 손가락을 굽힌다. 바인드 회전 위에 마디별 굽힘을 얹는다.
    private void ApplyGrip(HandGrip grip)
    {
        if (m_fingerJoints == null) return;
        foreach (FingerJoint j in m_fingerJoints)
        {
            if (j.Bone == null) continue;
            j.Bone.localRotation = j.BaseRotation * Quaternion.Euler(CurlEuler(grip, j.Kind, j.Depth));
        }
    }

    // 든 총의 방아쇠에 검지를 맞춘다(#428). 방아쇠 위치가 총마다 달라 CurlEuler의 고정 각도로는 못
    // 맞춘다(테이저 실측 5.3cm 어긋남). 방아쇠 메시(이름에 "Trigger" 포함) 중심까지 거리를 검지 굽힘 각으로
    // 훑어 가장 가까운 값을 고른다. 거리는 손끝 점이 아니라 말단 마디 선분 기준 — 점으로 재면 과하게 말린다.
    private void AimIndexAtTrigger(GameObject heldModel)
    {
        if (m_fingerJoints == null || heldModel == null)
        {
            return;
        }

        Renderer trigger = null;
        foreach (Renderer r in heldModel.GetComponentsInChildren<Renderer>(true))
        {
            if (r.name.IndexOf("Trigger", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                trigger = r;
                break;
            }
        }
        if (trigger == null)
        {
            return; // 방아쇠 메시가 없는 총 — CurlEuler의 폴백 각도 그대로 둔다
        }

        // 검지 마디를 깊이 순으로 모은다 (CacheFingerJoints가 그 순서로 넣는다)
        Transform[] bones = new Transform[s_fingerDepthWeights.Length];
        Quaternion[] baseRotations = new Quaternion[bones.Length];
        int count = 0;
        foreach (FingerJoint j in m_fingerJoints)
        {
            if (j.Kind != FingerKind.Index || j.Bone == null || count >= bones.Length)
            {
                continue;
            }
            bones[count] = j.Bone;
            baseRotations[count] = j.BaseRotation;
            count++;
        }
        if (count < bones.Length)
        {
            return;
        }

        Transform lastJoint = bones[bones.Length - 1];
        Transform tip = lastJoint.childCount > 0 ? lastJoint.GetChild(0) : lastJoint;
        Vector3 target = trigger.bounds.center;

        // ponytail: 관절 하나짜리 1차원 문제라 완전탐색이면 충분하다 — 장착할 때 한 번만 돈다.
        // 각도가 더 필요해지면(손가락별로 다른 목표 등) 그때 이분 탐색이나 IK로 바꿀 것.
        float bestCurl = 0f;
        float bestDistance = float.MaxValue;
        for (float curl = 0f; curl <= k_indexAimMaxDegrees; curl += k_indexAimStepDegrees)
        {
            ApplyIndexCurl(bones, baseRotations, curl);
            float distance = DistanceToSegment(lastJoint.position, tip.position, target);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestCurl = curl;
            }
        }
        ApplyIndexCurl(bones, baseRotations, bestCurl);
    }

    // 검지 체인에 굽힘 각을 얹는다 — 마디별 몫은 다른 손가락과 같은 s_fingerDepthWeights를 쓴다.
    private static void ApplyIndexCurl(Transform[] bones, Quaternion[] baseRotations, float curl)
    {
        for (int i = 0; i < bones.Length; i++)
        {
            bones[i].localRotation =
                baseRotations[i] * Quaternion.Euler(0f, 0f, curl * s_fingerDepthWeights[i]);
        }
    }

    // 선분 ab와 점 p 사이의 최단 거리.
    private static float DistanceToSegment(Vector3 a, Vector3 b, Vector3 p)
    {
        Vector3 ab = b - a;
        float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-8f));
        return Vector3.Distance(a + ab * t, p);
    }

    // 그립·손가락 종류·마디 깊이에 해당하는 굽힘 오일러 각(#265, 축 정정 #428). 각도는 뿌리 마디 기준이고
    // 깊은 마디는 s_*DepthWeights 비율만큼만 굽는다. 축은 FingerKind 선언부 참고 — 되돌리면 손가락이 비틀린다.
    // curl 상한은 100 언저리(실측: curl 100에서 손끝이 손바닥에 닿고, 110부터 손바닥을 뚫는다).
    private static Vector3 CurlEuler(HandGrip grip, FingerKind kind, int depth)
    {
        bool isThumb = kind == FingerKind.Thumb;

        // curl = 물건을 감싸는 방향으로 마는 양, spread = 손바닥 평면에서 여는 양.
        float curl;
        float spread = 0f;

        switch (grip)
        {
            case HandGrip.Trigger: // 총류 — 검지는 방아쇠에 걸치느라 덜 굽는다
                // 엄지 spread가 총 본체와 겹치는지를 좌우한다. Taser 기준 실측(엄지 체인 7점 중 관통 수):
                // +20 → 5점, 0 → 5점, -20 → curl 15~60 전 구간 0점.
                // 그래도 +20을 쓴다 — 이 수치는 본 중심선 기준이라 실제 스킨과 다르고, 인게임에서 보면
                // -20은 엄지가 총 아래로 빠져 쥔 것처럼 안 보인다. 숫자만 보고 뒤집지 말 것.
                // 검지 20은 방아쇠 메시가 없는 총용 폴백 — 있으면 AimIndexAtTrigger가 덮어쓴다.
                curl = isThumb ? 35f : (kind == FingerKind.Index ? 20f : 65f);
                if (isThumb) spread = 20f;
                break;
            case HandGrip.Wide: // 스캐너·박스 등 큰 물건 — 거의 편 손
                curl = isThumb ? 6f : 10f;
                if (isThumb) spread = 5f;
                break;
            case HandGrip.Handle: // 자루형(진압봉) — 다섯 손가락을 같은 깊이로 말아 자루를 감싼다
                // Trigger보다 깊게 쥔다: 걸칠 방아쇠가 없고, 스윙 중(#217) 얕게 쥐면 자루가 겉돈다.
                // 92가 손끝이 자루를 덮는 상한이지만 거기까지 주면 마디가 파묻혀 꽉 움켜쥔 그림이 된다 —
                // 70이 마디가 드러나면서도 헐거워 보이지 않는 지점. 엄지는 음수 spread로 자루 위를 덮는다.
                curl = isThumb ? 52f : 70f;
                if (isThumb) spread = -6f;
                break;
            default: // Relaxed — 빈손. 살짝 쥔 모양 (40을 주면 주먹에 가까워진다)
                curl = isThumb ? 18f : 26f;
                if (isThumb) spread = 3f;
                break;
        }

        float[] weights = isThumb ? s_thumbDepthWeights : s_fingerDepthWeights;
        float w = depth < weights.Length ? weights[depth] : 0f;
        curl *= w;
        spread *= w;

        // 엄지는 대립(-Y)이 굽힘이고 Z가 보조, 나머지는 Z가 굽힘·Y가 벌림. X는 길이축 롤이라 항상 0.
        return isThumb ? new Vector3(0f, -curl, spread) : new Vector3(0f, spread, curl);
    }
}
