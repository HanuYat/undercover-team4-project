using UnityEngine;

/// <summary>
/// 래그돌 뼈 한 벌 — <b>물리에 넘기고 되돌리는 것만</b> 한다. (#506)
///
/// <b>이 컴포넌트는 네트워크·권위·이동 프록시를 모른다.</b> 그게 분리의 기준이다: 뼈를 키네마틱과
/// 물리 사이에서 오가게 하고, 임펄스·감쇠·속도 상한을 걸고, 포즈를 캡처했다 되돌리는 일은
/// 플레이어든 NPC든 완전히 같다. 반면 <b>"누가 위치를 쥐나"</b>는 전혀 다르다 —
/// 플레이어는 CharacterController + 오너 권한 NetworkTransform이고, NPC는 NavMeshAgent + 서버 권한이다.
/// 그 부분은 이 리그를 소유하는 쪽(<see cref="PlayerRagdoll"/> 등)이 각자 쥔다.
///
/// 그래서 여기에는 <c>IsOwner</c>·<c>NetworkObject</c>·<c>CharacterController</c>가 <b>한 번도
/// 나오지 않는다.</b> 나오기 시작하면 분리가 무너진 것이다.
///
/// <b>상속이 아니라 컴포지션이다.</b> 추상 베이스로 만들면 프록시와 권위가 둘 다 갈려 거의 전부
/// abstract가 되고 공유되는 실체가 남지 않는다.
///
/// 붙이는 곳: 리그 최상단(<see cref="m_boneRootName"/>)을 <b>직속 자식으로</b> 가진 오브젝트.
/// </summary>
public class RagdollRig : MonoBehaviour
{
    /// <summary>래그돌 뼈 콜라이더 전용 레이어 — <c>PlayerRagdollSetup</c>이 만든다.</summary>
    public const string k_layerName = "Ragdoll";

    /// <summary>
    /// 리그 최상단의 기본 이름 — Synty 리그 관례. 에디터 셋업(<c>PlayerRagdollSetup</c>)이 프리팹을
    /// 검사할 때 같은 값을 써야 하므로 상수로 노출한다. 개체별로 다르면 <see cref="m_boneRootName"/>로 덮는다.
    /// </summary>
    public const string k_defaultBoneRootName = "Root";

    // ---- 프리팹에 저장할 수 없는 Rigidbody 물리값 ----
    //
    // ⚠ 이 셋은 <b>Rigidbody의 직렬화 필드가 아니다.</b> 프리팹의 Rigidbody 블록을 열어 보면
    // m_CollisionDetection에서 끝나고 solver·depenetration 항목이 아예 없다 — 에디터 스크립트에서
    // 아무리 써 넣어도 저장되지 않고, 인스턴스가 만들어질 때마다 Physics 프로젝트 기본값
    // (DynamicsManager.asset: 6 / 1 / 10)으로 되돌아온다. 그래서 <b>런타임에</b> 건다.
    // (레이어·isKinematic·보간·CCD·관절 preprocessing/projection은 직렬화되므로 에디터 셋업이 맡는다)

    // 겹침 탈출 속도 상한 — 안 걸면 기본값 10m/s로 튕겨나간다. 착지 순간 지형에 깊게 파고든 뼈가
    // 하나만 있어도 그 한 번의 탈출이 관절을 타고 몸 전체로 퍼져 시체가 발작하듯 튄다.
    private const float k_maxDepenetrationVelocity = 3f;

    // 관절 projection을 껐기 때문에(PlayerRagdollSetup의 k_enableProjection 주석) 관절을 붙드는 일은
    // 전적으로 solver 반복이 맡는다 — 기본값 6/1로는 강한 임펄스에서 관절이 눈에 띄게 늘어난다.
    private const int k_solverIterations = 12;
    private const int k_solverVelocityIterations = 4;

    [Tooltip("몸통 리그 최상단의 이름 — 이 오브젝트의 <b>직속</b> 자식이어야 한다.\n\n" +
             "⚠ 이름으로 뼈를 찾을 때 범위를 여기로 못박는 것이 핵심이다. 플레이어 프리팹에는 뼈 이름이 " +
             "완전히 같은 리그가 두 벌 있다 — 1인칭 팔(Camera/FPArm_Right/Root/...)이 FPArmGenerator가 " +
             "뽑은 리그 복사본이라 Hips·Spine_02·Shoulder_R가 그쪽에도 그대로 있고, Camera가 자식 순서상 " +
             "Root보다 앞이라 프리팹 전체를 훑어 첫 매치를 집으면 전부 FP 팔 쪽이 걸린다. " +
             "그러면 사망 시 1인칭 팔이 물리로 풀려 바닥으로 떨어진다(실제로 밟았다). " +
             "NPC 리그는 한 벌뿐이라 이 함정이 없지만, 범위를 좁히는 것은 어느 쪽이든 옳다")]
    [SerializeField] private string m_boneRootName = k_defaultBoneRootName;

    [Tooltip("골반보다 높은 뼈에 얹는 추가 속도 비율(1/m) — 상체가 더 빨라 다리가 끌리는 텀블이 생긴다. " +
             "폭심 기준 AddExplosionForce 대신 이걸 쓰는 이유는 결정론이다(피어마다 같은 결과)")]
    [SerializeField] private float m_tumbleBias = 0.8f;

    private Transform m_boneRoot; // 리그 최상단 — 뼈·스킨 수집 범위를 여기로 못박는다

    private Rigidbody[] m_bodies; // 래그돌 레이어의 뼈 Rigidbody만 (손에 든 아이템의 rb가 섞이지 않게)
    private float[] m_baseLinearDamping; // 감쇠를 풀 때 되돌릴 평시 값 — 프리팹이 진실이라 상수로 박지 않는다
    private float[] m_baseAngularDamping;

    private Transform m_hipsBone; // 관절이 없는 뼈 = 래그돌 루트
    private Rigidbody m_hipsBody;
    private Transform m_headBone; // 누운 방향(yaw) 계산용
    private Transform[] m_allBones; // 리그 전체 — 블렌드는 물리를 안 받은 뼈까지 보간해야 한다

    // 몸통 스킨드 메시 — 래그돌 동안 컬링 바운즈를 매 프레임 재계산시켜야 한다(SetSkinsAlwaysVisible)
    private SkinnedMeshRenderer[] m_skins;
    private bool[] m_skinUpdateWhenOffscreen;

    private Vector3[] m_capturedPositions; // 캡처한 월드 포즈 (재정렬 전후를 잇는다)
    private Quaternion[] m_capturedRotations;

    private Quaternion[] m_blendFromRotations; // 블렌드 출발점(로컬)
    private Vector3 m_blendFromHipsLocalPosition;
    private float m_blendTimer;

    /// <summary>뼈를 제대로 찾았는가 — 거짓이면 소유자는 래그돌 기능 전체를 꺼야 한다.</summary>
    public bool IsValid => m_bodies != null && m_bodies.Length > 0 && m_hipsBone != null;

    /// <summary>골반 — 관절이 없는 뼈. 래그돌의 기준점이자 위치 대리값의 추종 대상.</summary>
    public Transform Hips => m_hipsBone;

    /// <summary>같은 뼈의 Rigidbody — 밧줄 관절이 여기 붙는다.</summary>
    public Rigidbody HipsBody => m_hipsBody;

    /// <summary>리그 최상단 — 스킨 판정·계층 질의용.</summary>
    public Transform BoneRoot => m_boneRoot;

    /// <summary>물리를 받는 뼈 수 — 진단·검증용.</summary>
    public int BoneCount => m_bodies != null ? m_bodies.Length : 0;

    /// <summary>뼈 평균 속도(m/s) — 정착 판정에 쓴다.</summary>
    public float AverageSpeed
    {
        get
        {
            if (m_bodies == null || m_bodies.Length == 0)
                return 0f;

            float total = 0f;
            for (int i = 0; i < m_bodies.Length; i++)
                total += m_bodies[i].linearVelocity.magnitude;
            return total / m_bodies.Length;
        }
    }

    private bool m_collected;

    private void Awake() => EnsureCollected();

    /// <summary>
    /// 뼈 수집을 보장한다 — <b>멱등</b>이고, 이 리그를 쓰는 컴포넌트는 자기 <c>Awake</c> 첫머리에서
    /// 불러야 한다.
    ///
    /// <b>같은 GameObject 위 컴포넌트들의 Awake 순서는 보장되지 않는다.</b> 그래서
    /// <see cref="RagdollRope"/>나 <see cref="PlayerRagdoll"/>의 Awake가 먼저 돌면 아직 뼈가 없는
    /// 리그를 보게 되고, 밧줄 앵커가 안 만들어지거나 캡슐 충돌 무시가 안 걸린 채 조용히 넘어간다.
    /// 실행 순서를 프로젝트 설정으로 강제하는 대신 여기서 멱등으로 푼다.
    /// </summary>
    public void EnsureCollected()
    {
        if (m_collected)
            return;

        m_collected = true;
        Collect();
    }

    // ---- 수집 ----

    private void Collect()
    {
        m_bodies = new Rigidbody[0];

        int layer = LayerMask.NameToLayer(k_layerName);
        if (layer < 0)
        {
            Debug.LogError(
                $"[래그돌] 레이어 '{k_layerName}'가 없다 — Tools > Player > Build Ragdoll을 먼저 실행할 것",
                this
            );
            return;
        }

        m_boneRoot = transform.Find(m_boneRootName);
        if (m_boneRoot == null)
        {
            Debug.LogError(
                $"[래그돌] 몸통 리그 '{m_boneRootName}'를 {name}의 직속 자식에서 찾지 못했다",
                this
            );
            return;
        }

        Rigidbody[] all = m_boneRoot.GetComponentsInChildren<Rigidbody>(true);
        int count = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].gameObject.layer == layer)
                count++;
        }

        m_bodies = new Rigidbody[count];
        int next = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].gameObject.layer != layer)
                continue;

            m_bodies[next++] = all[i];

            // 관절이 없는 뼈가 래그돌 루트(골반)다 — 빌더가 하나만 그렇게 만든다
            if (m_hipsBone == null && all[i].GetComponent<CharacterJoint>() == null)
            {
                m_hipsBone = all[i].transform;
                m_hipsBody = all[i];
            }
            if (all[i].name == "Head")
                m_headBone = all[i].transform;
        }

        m_capturedPositions = new Vector3[count];
        m_capturedRotations = new Quaternion[count];

        // 감쇠를 풀 때 되돌릴 자리 — 프리팹 값(0 / 0.05)을 상수로 박으면 프리팹이 바뀌었을 때
        // 조용히 덮어쓴다. 여기서 읽어 두면 항상 프리팹이 진실이다.
        m_baseLinearDamping = new float[count];
        m_baseAngularDamping = new float[count];
        for (int i = 0; i < count; i++)
        {
            m_baseLinearDamping[i] = m_bodies[i].linearDamping;
            m_baseAngularDamping[i] = m_bodies[i].angularDamping;
        }

        // 골반(관절 없는 뼈)이 없으면 정착 재정렬·임펄스 기준이 없다 — 반쯤 도는 것보다 끄는 편이 낫다
        if (count == 0 || m_hipsBone == null)
        {
            Debug.LogError(
                $"[래그돌] {m_boneRoot.name} 아래에서 래그돌 뼈를 제대로 찾지 못했다"
                    + $" (뼈 {count}개, 골반 {(m_hipsBone == null ? "없음" : m_hipsBone.name)})"
                    + " — Tools > Player > Build Ragdoll을 실행할 것",
                this
            );
            m_bodies = new Rigidbody[0];
            return;
        }

        ApplyRuntimePhysics(); // 프리팹이 들고 있을 수 없는 값 — 위 상수 주석 참고

        // 블렌드는 물리를 받지 않은 뼈(척추 사이·목·손가락·발)까지 보간해야 한다 — 그것들은
        // 애니메이터가 꺼진 순간의 포즈에 멈춰 있어, 안 섞으면 블렌드 시작 프레임에 목과 손이 튄다.
        m_allBones = m_boneRoot.GetComponentsInChildren<Transform>(true);
        m_blendFromRotations = new Quaternion[m_allBones.Length];

        CollectSkins();
        SetKinematic(true); // 평시는 애니메이터가 포즈를 쥔다
    }

    // 이 리그가 구동하는 스킨드 메시를 모은다 — rootBone이 몸통 리그 안에 있는 것만.
    // 메시는 리그의 자식이 아니라 <b>형제</b>라 계층으로는 못 찾고, 1인칭 팔도 같은 이름의 메시를
    // 들고 있어 이름으로도 못 가른다.
    private void CollectSkins()
    {
        SkinnedMeshRenderer[] all = GetComponentsInChildren<SkinnedMeshRenderer>(true);
        int count = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if (DrivenByBodyRig(all[i]))
                count++;
        }

        m_skins = new SkinnedMeshRenderer[count];
        m_skinUpdateWhenOffscreen = new bool[count];
        int next = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if (!DrivenByBodyRig(all[i]))
                continue;
            m_skins[next] = all[i];
            m_skinUpdateWhenOffscreen[next] = all[i].updateWhenOffscreen;
            next++;
        }
    }

    private bool DrivenByBodyRig(SkinnedMeshRenderer skin) =>
        skin.rootBone != null
        && (skin.rootBone == m_boneRoot || skin.rootBone.IsChildOf(m_boneRoot));

    // 직렬화되지 않는 Rigidbody 값을 인스턴스마다 다시 건다 — 상수 주석에 이유가 적혀 있다.
    private void ApplyRuntimePhysics()
    {
        for (int i = 0; i < m_bodies.Length; i++)
        {
            m_bodies[i].maxDepenetrationVelocity = k_maxDepenetrationVelocity;
            m_bodies[i].solverIterations = k_solverIterations;
            m_bodies[i].solverVelocityIterations = k_solverVelocityIterations;
        }
    }

    // ---- 물리 on/off ----

    /// <summary>전 뼈를 키네마틱(애니메이터가 포즈를 쥠) ↔ 물리 사이에서 전환한다.</summary>
    public void SetKinematic(bool kinematic)
    {
        if (m_bodies == null)
            return;

        for (int i = 0; i < m_bodies.Length; i++)
            SetBodyKinematic(m_bodies[i], kinematic);
    }

    // 보간을 키네마틱 여부와 함께 갈아탄다.
    //  · 물리 중 — Interpolate. 안 켜면 물리 틱(50Hz)이 그대로 보여 몸이 떨린다.
    //  · 키네마틱 — None. 켜 두면 물리가 스텝 사이 보간 포즈를 트랜스폼에 써서 애니메이터가 방금
    //    놓은 포즈를 한 스텝 늦은 값으로 덮는다 — 팔다리가 끌리고 스킨드 메시가 늘어난다.
    //
    // 속도는 <b>양쪽 전이에서 모두</b> 지운다 — 키네마틱으로 넘기기 <b>전에</b>, 물리로 돌려줄 때는
    // 돌려준 <b>뒤에</b>. (키네마틱 상태에서의 속도 대입은 적용이 보장되지 않아 순서가 갈린다)
    //
    //  · 넘기기 전 — 정착이 한 프레임 안에서 키네마틱을 왕복하므로 안 지우면 직전 속도가 되살아난다.
    //  · 돌려준 뒤 — <b>키네마틱인 동안에도 트랜스폼이 움직이면 PhysX는 그 이동에서 속도를
    //    유도하고, 그 속도는 isKinematic = false 시점에 살아난다.</b> 실측에서 접촉 없이 머리
    //    7.1m/s, 어깨·팔꿈치 6.0m/s가 나왔다 — 리그 루트에서 먼 뼈일수록 회전 반경이 커서 더
    //    빨랐다는 순서가 이 원인을 가리켰다. 운반·밧줄로 시체가 끌려다니는 동안에도 같은 조건이
    //    성립하므로 이 짝은 계속 필요하다.
    private static void SetBodyKinematic(Rigidbody body, bool kinematic)
    {
        if (kinematic && !body.isKinematic)
        {
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }

        body.isKinematic = kinematic;
        body.interpolation = kinematic
            ? RigidbodyInterpolation.None
            : RigidbodyInterpolation.Interpolate;

        if (kinematic)
            return;

        body.linearVelocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
    }

    /// <summary>
    /// 이 리그의 뼈 콜라이더와 <paramref name="other"/>의 충돌을 켜고 끈다.
    ///
    /// 소유자의 이동 대리값(플레이어 CharacterController 캡슐, NPC 콜라이더)과 부딪히지 않게 하는 데
    /// 쓴다. 죽는 순간 래그돌은 자기 대리값 <b>안에서</b> 출발하므로, 그대로 두면 깊게 겹친 상태로
    /// 시작하고 그걸 밀어내는 힘에 몸이 발작처럼 튄다(실제로 밟았다).
    ///
    /// ⚠ <b>이 상태는 콜라이더를 껐다 켜면 초기화된다</b>(Unity 사양) — 다시 거는 책임은 소유자에게 있다.
    /// </summary>
    public void IgnoreCollisionWith(Collider other, bool ignore)
    {
        if (other == null || m_bodies == null)
            return;

        for (int i = 0; i < m_bodies.Length; i++)
        {
            Collider bone = m_bodies[i].GetComponent<Collider>();
            if (bone != null)
                Physics.IgnoreCollision(bone, other, ignore);
        }
    }

    /// <summary>
    /// 래그돌 동안 컬링 바운즈를 매 프레임 재계산시킨다.
    ///
    /// <b>안 하면 시체가 화면에서 사라진다.</b> SkinnedMeshRenderer의 컬링 바운즈는 <c>rootBone</c>
    /// 기준으로 한 번 계산된 값인데, 래그돌은 골반 이하만 움직이고 rootBone(리그 최상단)은 사망 지점에
    /// 그대로 남는다 — 몸이 날아간 뒤에도 바운즈는 사망 지점에 있어서, 화면 안에 있는 몸이 컬링돼
    /// 통째로 안 보이게 된다. 래그돌의 고전적인 함정이다.
    ///
    /// 비용이 있으므로(매 프레임 스킨 바운즈 계산) 래그돌이 켜져 있는 동안만 올린다.
    /// </summary>
    public void SetSkinsAlwaysVisible(bool always)
    {
        if (m_skins == null)
            return;

        for (int i = 0; i < m_skins.Length; i++)
            m_skins[i].updateWhenOffscreen = always || m_skinUpdateWhenOffscreen[i];
    }

    // ---- 힘·속도 ----

    /// <summary>
    /// 전 뼈에 같은 속도를 주고, 골반보다 높은 뼈에만 조금 더 얹어 텀블을 만든다.
    ///
    /// 폭심 기준 <c>AddExplosionForce</c>를 쓰지 않는 이유는 결정론이다 — 임펄스 벡터 하나만 받으면
    /// 폭심·반경을 몰라도 되고, 난수 없이 전 피어가 같은 회전을 낸다. 뼈를 동기화하지 않는 설계에서는
    /// <b>입력이 같아야 결과가 같다</b>(§10-3).
    /// </summary>
    public void ApplyImpulse(Vector3 velocity)
    {
        if (velocity == Vector3.zero || m_bodies == null || m_hipsBone == null)
            return;

        float hipsHeight = m_hipsBone.position.y;
        for (int i = 0; i < m_bodies.Length; i++)
        {
            float lift = m_bodies[i].worldCenterOfMass.y - hipsHeight;
            m_bodies[i].linearVelocity += velocity * (1f + m_tumbleBias * lift);
        }
    }

    /// <summary>전 뼈에 감쇠를 건다 — 푸는 것은 <see cref="RestoreDamping"/>.</summary>
    public void SetDamping(float linear, float angular)
    {
        if (m_bodies == null)
            return;

        for (int i = 0; i < m_bodies.Length; i++)
        {
            if (m_bodies[i] == null)
                continue;
            m_bodies[i].linearDamping = linear;
            m_bodies[i].angularDamping = angular;
        }
    }

    /// <summary>감쇠를 프리팹에서 읽어 둔 평시 값으로 되돌린다.</summary>
    public void RestoreDamping()
    {
        if (m_bodies == null || m_baseLinearDamping == null)
            return;

        for (int i = 0; i < m_bodies.Length; i++)
        {
            if (m_bodies[i] == null)
                continue;
            m_bodies[i].linearDamping = m_baseLinearDamping[i];
            m_bodies[i].angularDamping = m_baseAngularDamping[i];
        }
    }

    /// <summary>뼈 속도의 하드 상한 — 방향은 두고 크기만 자른다. 0 이하면 무동작.</summary>
    public void ClampSpeed(float maxSpeed)
    {
        if (maxSpeed <= 0f || m_bodies == null)
            return;

        float maxSqr = maxSpeed * maxSpeed;
        for (int i = 0; i < m_bodies.Length; i++)
        {
            Rigidbody body = m_bodies[i];
            if (body == null || body.isKinematic)
                continue;

            Vector3 velocity = body.linearVelocity;
            float sqr = velocity.sqrMagnitude;
            if (sqr > maxSqr)
                body.linearVelocity = velocity * (maxSpeed / Mathf.Sqrt(sqr));
        }
    }

    /// <summary>
    /// 전 뼈를 깨운다. <b>골반만 깨우면 안 된다</b> — 골반만 깨어 있고 사지가 자고 있으면 관절이
    /// 당기는 힘을 사지가 받지 않아 몸이 한 덩어리로 끌려온다(흐느적임이 통째로 사라진다).
    /// </summary>
    public void WakeAll()
    {
        if (m_bodies == null)
            return;

        for (int i = 0; i < m_bodies.Length; i++)
        {
            if (m_bodies[i] != null && !m_bodies[i].isKinematic)
                m_bodies[i].WakeUp();
        }
    }

    /// <summary>
    /// 전 뼈를 같은 델타로 강체 평행이동한다 — 포즈·상대속도·관절이 보존된다.
    /// 동적 바디의 <c>position</c> 대입은 텔레포트라 속도가 유도되지 않는다
    /// (키네마틱과 반대 — <see cref="SetBodyKinematic"/> 주석).
    /// </summary>
    public void TranslateBy(Vector3 delta)
    {
        if (m_bodies == null)
            return;

        for (int i = 0; i < m_bodies.Length; i++)
            m_bodies[i].position += delta;
    }

    // ---- 포즈 캡처 / 블렌드 ----

    /// <summary>전 뼈의 월드 포즈를 저장한다 — 루트를 옮기기 <b>전에</b> 부른다.</summary>
    public void CapturePose()
    {
        if (m_bodies == null)
            return;

        for (int i = 0; i < m_bodies.Length; i++)
        {
            m_capturedPositions[i] = m_bodies[i].transform.position;
            m_capturedRotations[i] = m_bodies[i].transform.rotation;
        }
    }

    /// <summary>저장한 월드 포즈를 되돌린다 — 루트를 옮긴 <b>뒤에</b> 부른다. 화면은 그대로다.</summary>
    public void RestoreCapturedPose()
    {
        if (m_bodies == null)
            return;

        // 부모가 먼저 와야 자식의 월드 포즈 대입이 헛되지 않는다 —
        // GetComponentsInChildren이 계층 순서(부모 먼저)로 주므로 그 순서를 그대로 쓴다.
        for (int i = 0; i < m_bodies.Length; i++)
            m_bodies[i].transform.SetPositionAndRotation(
                m_capturedPositions[i],
                m_capturedRotations[i]
            );
    }

    /// <summary>지금 포즈를 블렌드 출발점으로 잡는다 — 애니메이터를 켜기 직전에 부른다.</summary>
    public void BeginBlend()
    {
        if (m_allBones == null)
            return;

        for (int i = 0; i < m_allBones.Length; i++)
            m_blendFromRotations[i] = m_allBones[i].localRotation;
        m_blendFromHipsLocalPosition = m_hipsBone.localPosition;
        m_blendTimer = 0f;
    }

    /// <summary>
    /// 블렌드 한 프레임 — <b>LateUpdate에서</b> 부른다. 그 시점의 뼈 로컬값이 곧 애니메이터가 평가한
    /// 포즈라, 저장해 둔 정착 포즈에서 그쪽으로 끌고 가면 "누운 자세에서 대기 자세로 스르륵"이 된다.
    /// </summary>
    /// <returns>블렌드가 끝났으면 참.</returns>
    public bool TickBlend(float blendSeconds)
    {
        m_blendTimer += Time.deltaTime;
        float t = blendSeconds <= 0f ? 1f : Mathf.Clamp01(m_blendTimer / blendSeconds);

        for (int i = 0; i < m_allBones.Length; i++)
            m_allBones[i].localRotation = Quaternion.Slerp(
                m_blendFromRotations[i],
                m_allBones[i].localRotation,
                t
            );

        m_hipsBone.localPosition = Vector3.Lerp(
            m_blendFromHipsLocalPosition,
            m_hipsBone.localPosition,
            t
        );

        return t >= 1f;
    }

    // ---- 질의 ----

    /// <summary>
    /// 몸이 누운 방향의 yaw — 골반→머리를 지면에 투영한 값. 비행 중 추종과 정착 정렬이 <b>같은</b>
    /// 계산을 써야 정착 순간에 회전이 안 튄다.
    ///
    /// <b>기상 모션 보정은 여기서 더하지 않는다.</b> "루트 전방을 향해 누워 있다"를 전제하는 클립이
    /// 어느 쪽을 머리로 보는지는 <b>그 리그의 클립 사정</b>이라 소유자가 자기 오프셋을 얹는다
    /// (<c>PlayerRagdoll.m_rootYawOffset</c>). 여기가 순수한 몸 방향을 내야 NPC가 다른 클립을 써도 맞는다.
    /// </summary>
    public bool TryGetBodyYaw(out float yaw)
    {
        yaw = 0f;
        if (m_headBone == null || m_hipsBone == null)
            return false;

        Vector3 lengthwise = m_headBone.position - m_hipsBone.position;
        lengthwise.y = 0f;
        if (lengthwise.sqrMagnitude < 0.0004f)
            return false; // 거의 수직으로 서 있다 — 방향을 못 정하니 기존 yaw를 유지한다

        yaw = Quaternion.LookRotation(lengthwise.normalized).eulerAngles.y;
        return true;
    }
}
