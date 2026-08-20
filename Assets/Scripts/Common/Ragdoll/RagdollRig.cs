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
    /// <summary>래그돌 뼈 콜라이더 전용 레이어 — <c>RagdollSetup</c>이 만든다.</summary>
    public const string k_layerName = "Ragdoll";

    /// <summary>
    /// 리그 최상단의 기본 이름 — Synty 리그 관례. 에디터 셋업(<c>RagdollSetup</c>)이 프리팹을
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
    //
    // <b>이 값이 곧 튀어오르는 높이다.</b> 겹침을 밀어내는 것은 힘이 아니라 <b>바디에 얹히는
    // 속도</b>라, 겹침이 풀린 뒤에도 그 속도가 남아 탄도 비행이 된다 — v²/2g가 그대로 보인다:
    // 10m/s면 5.1m, 3m/s면 46cm, 1m/s면 5cm, 0.5m/s면 1.3cm.
    //
    // <b>0.5로 정한 근거</b> (#671 실측). 프리팹의 클립별로 다리 콜라이더 최저점을 재면 가장 깊은
    // 포즈(NPC attack02~05)가 −4.2cm다. 0.5m/s면 50Hz에서 프레임당 1cm이므로 그 4.2cm를
    // 빠져나오는 데 4~5스텝(약 0.1초) — 안 보이고, 튐은 1.3cm라 역시 안 보인다. 두 쪽이 다
    // 문턱 아래로 드는 구간이 여기다.
    //
    // ⚠ <b>올려야 할 신호는 "박힌 채 있다"다.</b> 스폰·재배치처럼 <b>우리가 몸을 놓는</b> 경로에서
    // 시체가 바닥에 잠긴 채 눈에 띄게 머물면 여기가 첫 용의자다 — 겹침이 4cm보다 훨씬 깊다는
    // 뜻이므로 위 표를 보고 올린다. 반대로 <b>낮추는 방향은 이미 바닥에 가깝다</b>(1.3cm).
    private const float k_maxDepenetrationVelocity = 0.5f;

    // 관절 projection을 껐기 때문에(RagdollSetup의 k_enableProjection 주석) 관절을 붙드는 일은
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
    private Collider[] m_boneColliders; // 위와 같은 순서 — 뼈마다 하나 (위저드가 그렇게 만든다)
    private float[] m_baseLinearDamping; // 감쇠를 풀 때 되돌릴 평시 값 — 프리팹이 진실이라 상수로 박지 않는다
    private float[] m_baseAngularDamping;

    private Transform m_hipsBone; // 관절이 없는 뼈 = 래그돌 루트
    private Rigidbody m_hipsBody;
    private Transform m_headBone; // 누운 방향(yaw) 계산용

    // 몸통 스킨드 메시 — 래그돌 동안 컬링 바운즈를 매 프레임 재계산시켜야 한다(SetSkinsAlwaysVisible)
    private SkinnedMeshRenderer[] m_skins;
    private bool[] m_skinUpdateWhenOffscreen;

    private Vector3[] m_capturedPositions; // 캡처한 월드 포즈 (재정렬 전후를 잇는다)
    private Quaternion[] m_capturedRotations;

    // ---- 바인드 포즈 (프리팹이 authoring한 자세) ----
    //
    // <b>왜 들고 있나.</b> 관절의 <c>connectedAnchor</c>가 자동 설정이라(프리팹 확인:
    // <c>m_AutoConfigureConnectedAnchor: 1</c>) 관절이 처음 깨어날 때의 <b>뼈 길이</b>를 기준으로
    // 구워진다. 그 뒤 뼈의 <c>localPosition</c>이 달라지면 관절은 첫 스텝부터 위반 상태로 출발한다.
    //
    // 그런데 그 길이는 <b>실제로 달라질 수 있다</b>: 물리가 관절을 늘린 채 정착하면
    // <see cref="RestoreCapturedPose"/>가 <b>월드</b> 포즈를 쓰므로 늘어난 길이가 로컬 값에 굳고,
    // 부활·재사망의 포즈 복사가 그것을 그대로 나른다(<c>RagdollPose.Copy</c>가 localPosition까지
    // 옮긴다). 회전만 보는 진단으로는 안 잡힌다.
    //
    // <b>리지드바디 뼈만이 아니라 리그 전체</b>를 담는다 — 포즈 복사가 훑는 범위와 같아야 드리프트를
    // 빠짐없이 재고 되돌릴 수 있다.
    private Transform[] m_bindBones;
    private Vector3[] m_bindPositions;
    private Quaternion[] m_bindRotations;
    private bool[] m_bindJointed; // 관절이 달려 있는가 — 드리프트 판정을 이 뼈들로 좁힌다

    // 이 뼈가 <see cref="m_poseBones"/>에 들어 있는가 — 즉 <b>자세가 복제되는 뼈인가.</b>
    // 거짓인 뼈(손·발·손가락 같은 말단)는 아무도 값을 보내 주지 않으므로 바인드로 못박는다.
    private bool[] m_bindStreamed;

    /// <summary>
    /// <b>자세 한 벌 — 복제되는 뼈 전부.</b> <see cref="m_bodies"/>에 <b>체인 뼈</b>를 더한 것이다.
    ///
    /// 표준 래그돌 위저드는 Rigidbody를 11개만 만드는데, 리그에는 그 사이를 잇는 뼈가 끼어 있다:
    ///
    /// <code>
    /// Hips(rb) → Spine_01 → Spine_02(rb) → Spine_03 → Neck → Head(rb)
    ///                                   └→ Clavicle_L → UpperArm_L(rb)
    /// </code>
    ///
    /// 그 사이 뼈를 빼고 보내면 <b>상체 전체가 Spine_01 하나에 매달린다</b> — 받는 쪽의 Spine_01은
    /// 자기 애니메이터가 마지막에 놓은 값이라, 보낸 회전 11개가 전부 같아도 몸이 다른 자세로 굳는다.
    ///
    /// <b>포함 규칙: 자기가 리지드바디 뼈이거나, 자손에 리지드바디 뼈가 있는 것.</b> 손·발·손가락 같은
    /// <b>말단</b>은 체인에 없어 몸 모양을 바꾸지 못하므로 뺀다 — 그쪽은
    /// <see cref="RestoreUnstreamedBonesToBind"/>가 바인드로 못박아 공짜로 맞춘다.
    ///
    /// ⚠ <b>순서가 피어마다 같아야 한다</b> — <c>GetComponentsInChildren</c>의 계층 순서를 그대로
    /// 쓰므로 같은 프리팹이면 같다. 부모가 자식보다 먼저 오는 것도 그 성질에 기댄다.
    /// </summary>
    private Transform[] m_poseBones;

    /// <summary>뼈를 제대로 찾았는가 — 거짓이면 소유자는 래그돌 기능 전체를 꺼야 한다.</summary>
    public bool IsValid => m_bodies != null && m_bodies.Length > 0 && m_hipsBone != null;

    /// <summary>골반 — 관절이 없는 뼈. 래그돌의 기준점이자 위치 대리값의 추종 대상.</summary>
    public Transform Hips => m_hipsBone;

    /// <summary>같은 뼈의 Rigidbody — 밧줄 관절이 여기 붙는다.</summary>
    public Rigidbody HipsBody => m_hipsBody;

    /// <summary>리그 최상단 — 스킨 판정·계층 질의용.</summary>
    public Transform BoneRoot => m_boneRoot;

    /// <summary>
    /// <b>자세 한 벌을 이루는 뼈 수</b> — 스트림 페이로드의 길이이자 피어 간 배선 검증값.
    /// <see cref="m_bodies"/>보다 많다(<see cref="m_poseBones"/> 주석).
    /// </summary>
    public int BoneCount => m_poseBones != null ? m_poseBones.Length : 0;

    /// <summary>
    /// 가장 낮은 뼈의 월드 y — 시체가 지면을 파고드는지 재는 값.
    ///
    /// ⚠ <b><see cref="Rigidbody.position"/>이 아니라 트랜스폼을 읽는다.</b> 이 프로젝트는
    /// <c>Physics.autoSyncTransforms = 0</c>이라 트랜스폼에 쓴 값이 다음 물리 스텝까지 물리 포즈에
    /// 반영되지 않는다. 이 값을 재는 자리는 전부 <b>뼈를 방금 대입한 직후</b>이므로
    /// (<c>RestoreCapturedPose</c>·<c>RestoreBindBoneLengths</c> 뒤) 물리 포즈를 읽으면
    /// <b>대입 전 골격을 재고 조용히 0을 돌려준다.</b>
    ///
    /// 물리가 굴러가는 동안에도 트랜스폼이 곧 화면에 보이는 것이라 이쪽이 맞다 — 파고들었는지는
    /// 보이는 몸으로 판정해야 한다.
    /// </summary>
    public float LowestBoneY
    {
        get
        {
            if (m_bodies == null || m_bodies.Length == 0)
                return 0f;

            float lowest = float.MaxValue;
            for (int i = 0; i < m_bodies.Length; i++)
            {
                float y = m_bodies[i].transform.position.y;
                if (y < lowest)
                    lowest = y;
            }
            return lowest;
        }
    }

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
                $"[래그돌] 레이어 '{k_layerName}'가 없다 — Tools > Player > Finish Ragdoll Setup을 먼저 실행할 것",
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

        // 뼈 콜라이더 — 위저드가 뼈마다 하나씩 만든다. 켜고 끄는 것과 충돌 무시가 같은 배열을 쓴다.
        m_boneColliders = new Collider[count];
        for (int i = 0; i < count; i++)
        {
            m_baseLinearDamping[i] = m_bodies[i].linearDamping;
            m_baseAngularDamping[i] = m_bodies[i].angularDamping;
            m_boneColliders[i] = m_bodies[i].GetComponent<Collider>();
        }

        // 골반(관절 없는 뼈)이 없으면 정착 재정렬·임펄스 기준이 없다 — 반쯤 도는 것보다 끄는 편이 낫다
        if (count == 0 || m_hipsBone == null)
        {
            Debug.LogError(
                $"[래그돌] {m_boneRoot.name} 아래에서 래그돌 뼈를 제대로 찾지 못했다"
                    + $" (뼈 {count}개, 골반 {(m_hipsBone == null ? "없음" : m_hipsBone.name)})"
                    + " — Tools > Player > Finish Ragdoll Setup을 실행할 것",
                this
            );
            m_bodies = new Rigidbody[0];
            return;
        }

        ApplyRuntimePhysics(); // 프리팹이 들고 있을 수 없는 값 — 위 상수 주석 참고

        CaptureBindPose(); // 아직 아무도 리그를 건드리지 않은 지금이 유일한 기회다

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

    // ---- 바인드 포즈 ----

    // 프리팹이 authoring한 자세를 담아 둔다 — <see cref="Collect"/>에서만 부른다(Awake 시점이라
    // 아직 아무도 리그를 건드리지 않았다). 나중에 부르면 그때의 오염된 자세가 "바인드"가 된다.
    private void CaptureBindPose()
    {
        m_bindBones = m_boneRoot.GetComponentsInChildren<Transform>(true);
        m_bindPositions = new Vector3[m_bindBones.Length];
        m_bindRotations = new Quaternion[m_bindBones.Length];
        m_bindJointed = new bool[m_bindBones.Length];
        m_bindStreamed = new bool[m_bindBones.Length];

        for (int i = 0; i < m_bindBones.Length; i++)
        {
            m_bindPositions[i] = m_bindBones[i].localPosition;
            m_bindRotations[i] = m_bindBones[i].localRotation;
            m_bindJointed[i] = m_bindBones[i].GetComponent<Joint>() != null;

            // ⚠ <b>Rigidbody 유무가 아니라 <see cref="m_bodies"/> 소속으로 판정한다</b> — 수집이
            // 레이어로도 거르므로(<see cref="Collect"/>), 둘이 갈리면 이 표가 조용히 틀린다.
            // 체인 뼈(자손에 리지드바디가 있는 뼈)도 복제 대상이다 — m_poseBones 주석.
            m_bindStreamed[i] = IsSelfOrAncestorOfBody(m_bindBones[i]);
        }

        CollectPoseBones();
    }

    // 자세 한 벌을 고른다 — 계층 순서 그대로라 피어마다 같고 부모가 자식보다 먼저 온다.
    private void CollectPoseBones()
    {
        int count = 0;
        for (int i = 0; i < m_bindBones.Length; i++)
        {
            if (m_bindStreamed[i])
                count++;
        }

        m_poseBones = new Transform[count];
        int next = 0;
        for (int i = 0; i < m_bindBones.Length; i++)
        {
            if (m_bindStreamed[i])
                m_poseBones[next++] = m_bindBones[i];
        }
    }

    // 이 뼈가 리지드바디 뼈이거나, 그 조상인가 — 즉 <b>몸 모양을 결정하는 체인 위에 있는가.</b>
    private bool IsSelfOrAncestorOfBody(Transform bone)
    {
        for (int i = 0; i < m_bodies.Length; i++)
        {
            Transform body = m_bodies[i].transform;
            if (body == bone || body.IsChildOf(bone))
                return true;
        }

        return false;
    }

    /// <summary>
    /// <b>관절이 달린 뼈</b>의 로컬 위치가 바인드 포즈에서 얼마나 벗어났는지(m) — 최댓값. 진단용.
    ///
    /// <b>이것이 관절이 보는 "뼈 길이"다.</b> 0이 아니면 관절의 <c>connectedAnchor</c>(바인드 포즈
    /// 기준으로 구워짐)와 실제 골격이 어긋나 있다는 뜻이고, 그 상태로 물리에 넘기면 <b>첫 스텝부터
    /// 관절이 위반된 채 출발한다</b> — 사지가 고무처럼 늘어나는 그림이 된다.
    ///
    /// ⚠ <b>관절 없는 뼈를 세면 안 된다.</b> 처음엔 리그 전체를 쟀는데, 거기에는 <b>골반</b>이
    /// 들어 있다 — 골반은 래그돌 루트라 로컬 위치가 자세의 일부이고 애니메이션에 따라 정당하게
    /// 변한다. 그래서 아무 문제가 없는데도 2cm대 값이 상시로 찍혀 경고가 무의미해졌다.
    /// 관절이 구속하는 뼈만이 "길이가 틀어졌다"는 판정의 대상이다.
    ///
    /// 회전은 보지 않는다. 자세는 매번 새로 복사되므로 문제가 되는 것은 <b>길이</b>뿐이다.
    /// </summary>
    public float MaxBindPositionDrift
    {
        get
        {
            if (m_bindBones == null)
                return 0f;

            float worst = 0f;
            for (int i = 0; i < m_bindBones.Length; i++)
            {
                if (m_bindBones[i] == null || !m_bindJointed[i])
                    continue;

                float drift = Vector3.Distance(m_bindBones[i].localPosition, m_bindPositions[i]);
                if (drift > worst)
                    worst = drift;
            }
            return worst;
        }
    }

    // ---- 진단 보조 (임시 — NpcRagdoll의 진단 ⑨가 쓴다. 그 블록과 함께 지운다) ----

    /// <summary>
    /// 스트림에 실리는 뼈 — <b>계층 순서</b>(부모가 자식보다 먼저)라 그대로 훑으면 체인이 풀린다.
    /// 원격의 재구성을 흉내 내는 진단에만 쓴다.
    /// </summary>
    public Transform[] PoseBones => m_poseBones;

    /// <summary>
    /// <b>뼈마다</b>의 바인드 로컬 위치 드리프트(m) — <see cref="MaxBindPositionDrift"/>가 최댓값만
    /// 주는 자리를 <b>어느 뼈인지</b>까지 벌려 놓은 진단용이다. 대상은 같다(관절이 달린 뼈만).
    /// </summary>
    /// <returns>담은 개수.</returns>
    public int CollectBindPositionDrift(System.Collections.Generic.List<(string Bone, float Drift)> into)
    {
        if (into == null)
            return 0;

        into.Clear();
        if (m_bindBones == null)
            return 0;

        for (int i = 0; i < m_bindBones.Length; i++)
        {
            if (m_bindBones[i] == null || !m_bindJointed[i])
                continue;

            into.Add(
                (m_bindBones[i].name, Vector3.Distance(m_bindBones[i].localPosition, m_bindPositions[i]))
            );
        }

        return into.Count;
    }

    /// <summary>
    /// 이 뼈의 <b>바인드 로컬 위치</b> — 원격이 자세를 입힐 때 실제로 쓰는 뼈 길이다(스트림은 회전만
    /// 싣는다). 리그의 뼈가 아니면 거짓.
    /// </summary>
    public bool TryGetBindLocalPosition(Transform bone, out Vector3 bindLocal)
    {
        bindLocal = Vector3.zero;
        if (m_bindBones == null || bone == null)
            return false;

        for (int i = 0; i < m_bindBones.Length; i++)
        {
            if (m_bindBones[i] != bone)
                continue;

            bindLocal = m_bindPositions[i];
            return true;
        }

        return false;
    }

    /// <summary>
    /// 리그를 프리팹의 바인드 포즈로 되돌린다 — <b>뼈 길이를 복원하는 것이 목적이다.</b>
    ///
    /// 부활처럼 "시체가 다음 사망까지 쉬는" 시점에 부르면, 물리가 늘려 놓은 <c>localPosition</c>이
    /// 지워져 다음 사망이 <b>1차 사망과 같은 조건</b>에서 출발한다.
    ///
    /// ⚠ <b>키네마틱일 때만 의미가 있다.</b> 동적인 뼈는 트랜스폼이 진실이 아니라서 다음 물리 스텝에
    /// 덮인다. 부르는 쪽이 순서를 맞출 것.
    /// </summary>
    public void RestoreBindPose()
    {
        if (m_bindBones == null)
            return;

        for (int i = 0; i < m_bindBones.Length; i++)
        {
            if (m_bindBones[i] == null)
                continue;

            m_bindBones[i].localPosition = m_bindPositions[i];
            m_bindBones[i].localRotation = m_bindRotations[i];
        }
    }

    /// <summary>
    /// <b>뼈 길이만</b> 바인드로 되돌린다 — 자세(회전)는 손대지 않는다.
    ///
    /// <see cref="RestoreBindPose"/>를 쓸 수 없는 자리를 위한 것이다. 저쪽은 회전까지 되돌리므로
    /// <b>물리를 안 받는 뼈</b>(목·손가락·발)가 T자 방향으로 튀는데, 원격이 받는 자세
    /// (<see cref="ApplyLocalPoseAroundHips"/>)에는 그 뼈들이 들어 있지 않아 되돌릴 짝이 없다.
    ///
    /// <b>왜 원격에 필요한가.</b> 시체는 <c>ExitRagdoll</c>을 영영 타지 않아 <see cref="RestoreBindPose"/>가
    /// 한 번도 돌지 않는다 — 물리가 관절을 늘려 놓으면 그 길이가 <b>영구히 남는다.</b> 그런데 받는
    /// 자세는 로컬 <b>회전</b>뿐이라(길이는 관절이 유지한다는 전제) 늘어난 리그에 입히면 보낸 쪽과
    /// 다른 몸이 나온다. 갈아끼우기 직전에 길이를 되돌려 그 전제를 실제로 참으로 만든다.
    ///
    /// 대상은 <b>관절이 달린 뼈</b>뿐이다 — 골반의 로컬 위치는 자세의 일부라 되돌리면 안 된다
    /// (<see cref="MaxBindPositionDrift"/>가 골반을 빼는 것과 같은 이유).
    ///
    /// ⚠ <see cref="RestoreBindPose"/>와 같이 <b>키네마틱일 때만</b> 의미가 있다.
    /// </summary>
    public void RestoreBindBoneLengths()
    {
        if (m_bindBones == null)
            return;

        for (int i = 0; i < m_bindBones.Length; i++)
        {
            if (m_bindBones[i] == null || !m_bindJointed[i])
                continue;

            m_bindBones[i].localPosition = m_bindPositions[i];
        }
    }

    /// <summary>
    /// <b>말단 뼈</b>(손·발·손가락)의 회전을 바인드로 못박는다 — 전 피어가 같은 값을 쓰게 만드는 것이
    /// 목적이다. 래그돌에 <b>진입할 때 모든 피어가</b> 부른다.
    ///
    /// 대상은 <see cref="m_poseBones"/>에 <b>들지 않는</b> 뼈다. 그것들은 아무도 값을 보내 주지 않아
    /// 각 피어의 애니메이터가 마지막에 놓은 자세에 멈추는데, 기상 클립처럼 피어마다 클립 시간이
    /// 어긋나는 구간에서 죽으면 손발 모양이 갈린다. 바인드는 전 피어가 프리팹에서 읽는 값이라
    /// <b>공짜로 결정적</b>이다.
    ///
    /// ⚠ <b>체인 뼈는 여기서 손대지 않는다 — 그쪽은 스트림이 싣는다</b>(<see cref="m_poseBones"/>).
    /// 한때 체인까지 여기서 못박았다가 <b>시체가 바닥에 파묻혔다</b>: 바인드 척추는 <b>골반 로컬
    /// 기준으로 똑바로 선</b> 자세라, 골반이 엎어진 기상 자세에서 그걸 강제하면 상체가 지면 쪽으로
    /// 꺾인다. 그 자세로 물리를 시작하면 겹침 탈출 상한에 걸려 다 빠져나오지 못하고, 지형에 낀 몸은
    /// 속도가 낮아 <b>그대로 정착·얼림</b>된다. 표기만 맞추려던 것이 <b>몸을 실제로 옮긴</b> 사고다.
    ///
    /// 말단은 콜라이더도 리지드바디도 없어 같은 사고가 원리적으로 안 난다 — 순수 표시다.
    ///
    /// ⚠ <b>위치는 건드리지 않는다</b> — 그쪽은 뼈 길이이고, 애니메이터도 물리도 이 뼈들의
    /// localPosition은 쓰지 않는다. 되돌릴 짝은 <see cref="RestoreBindBoneLengths"/>다.
    /// </summary>
    public void RestoreUnstreamedBonesToBind()
    {
        if (m_bindBones == null)
            return;

        for (int i = 0; i < m_bindBones.Length; i++)
        {
            if (m_bindBones[i] == null || m_bindStreamed[i])
                continue;

            m_bindBones[i].localRotation = m_bindRotations[i];
        }
    }

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

    /// <summary>
    /// 전 뼈를 키네마틱(애니메이터가 포즈를 쥠) ↔ 물리 사이에서 전환한다.
    ///
    /// <b>물리로 넘기기 전에 트랜스폼을 PhysX에 밀어 넣는다</b> — 이 프로젝트는
    /// <c>m_AutoSyncTransforms = 0</c>이라(ProjectSettings/DynamicsManager.asset) 트랜스폼에 쓴 값이
    /// 액터로 <b>즉시 넘어가지 않기</b> 때문이다. 키네마틱인 동안은 트랜스폼이 진실이지만 동적으로
    /// 바뀌는 순간 <b>액터가 진실</b>이 되므로, 그 사이에 동기화하지 않으면 물리가 <b>액터가 들고
    /// 있던 옛 포즈</b>에서 출발한다.
    ///
    /// 실측: 사망 순간 살아있는 리그의 포즈를 시체로 복사한 직후 <c>복사잔차 0.0°</c>(트랜스폼은
    /// 정확)인데 <c>물리반영차 81.7°</c>였다 — 세 피어 모두. 그래서 시체가 <b>바인드 포즈(T자)에서
    /// 무너지기 시작했다.</b>
    ///
    /// <b>여기가 맞는 자리인 이유:</b> 같은 전이가 여러 군데다(사망 시 시체 켜기, 정착 후 물리 복귀,
    /// NPC 래그돌 진입). 호출부마다 동기화를 끼우면 하나 빠뜨리는 순간 같은 증상이 조용히 돌아온다.
    ///
    /// 반대 방향(동적 → 키네마틱)에는 필요 없다 — 그때는 물리가 트랜스폼을 쓰고 있었으므로 이미 맞다.
    /// </summary>
    public void SetKinematic(bool kinematic)
    {
        if (m_bodies == null)
            return;

        if (!kinematic)
            Physics.SyncTransforms();

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
        if (other == null || m_boneColliders == null)
            return;

        for (int i = 0; i < m_boneColliders.Length; i++)
        {
            if (m_boneColliders[i] != null)
                Physics.IgnoreCollision(m_boneColliders[i], other, ignore);
        }
    }

    /// <summary>
    /// 뼈 콜라이더를 켜고 끈다 — <b>시체를 보이지 않게 하는 방식이 GameObject 비활성이 아닐 때</b> 필요하다.
    ///
    /// <b>왜 오브젝트를 끄지 않는가.</b> NGO는 <b>비활성 GameObject의 NetworkBehaviour를 스폰에서
    /// 제외하고</b>(<c>NetworkObject.InvokeBehaviourNetworkSpawn</c>), 나중에 활성화돼도 만회하지
    /// 않는다 — 소스 주석이 "not supported"라고 못박는다. 그래서 골반에 NetworkTransform을 얹는
    /// 구성에서는 시체 오브젝트가 <b>항상 활성</b>이어야 하고, 숨기는 일은 렌더러와 콜라이더가 맡는다.
    ///
    /// ⚠ <b>콜라이더를 껐다 켜면 <see cref="IgnoreCollisionWith"/> 상태가 초기화된다</b>(Unity 사양) —
    /// 다시 거는 책임은 소유자에게 있다.
    /// </summary>
    public void SetBoneCollidersEnabled(bool value)
    {
        if (m_boneColliders == null)
            return;

        for (int i = 0; i < m_boneColliders.Length; i++)
        {
            if (m_boneColliders[i] != null)
                m_boneColliders[i].enabled = value;
        }
    }

    /// <summary>이 리그가 구동하는 스킨드 메시를 켜고 끈다 — 시체를 보이거나 숨긴다.</summary>
    public void SetSkinsEnabled(bool value)
    {
        if (m_skins == null)
            return;

        for (int i = 0; i < m_skins.Length; i++)
        {
            if (m_skins[i] != null)
                m_skins[i].enabled = value;
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
            // 키네마틱 바디는 속도 대입이 무시되고 경고만 난다 — 형제 루프(ClampSpeed 등)와 같은 가드다.
            // 정착한 시체가 이 상태다: 뼈 12개가 전부 키네마틱이라 임펄스가 경고로만 남았다 (#768).
            if (m_bodies[i] == null || m_bodies[i].isKinematic)
                continue;

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
    /// <b>전 뼈가 잠들었는가</b> — 물리가 스스로 낸 "다 끝났다" 신호다. 코드로 정착을 판정하던
    /// 자리를 이것이 대신한다.
    ///
    /// <b>하나라도 깨어 있으면 거짓이다.</b> 사지가 아직 움직이는데 골반만 잤다고 끝난 것으로 보면
    /// 마지막 자세가 팔다리가 뜬 채로 굳는다.
    ///
    /// 키네마틱 뼈는 <b>세지 않는다</b> — PhysX가 재우지 않으므로 항상 깨어 있는 것으로 보고되고,
    /// 원격은 전 뼈가 키네마틱이라 이 값이 영영 참이 되지 않는다. 어차피 묻는 쪽은 권위 피어다.
    /// </summary>
    public bool AllAsleep
    {
        get
        {
            if (m_bodies == null || m_bodies.Length == 0)
                return false;

            for (int i = 0; i < m_bodies.Length; i++)
            {
                if (m_bodies[i] == null || m_bodies[i].isKinematic)
                    continue;

                if (!m_bodies[i].IsSleeping())
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// 전 뼈를 강제로 재운다 — <b>지형에 껴서 영영 안 자는 몸</b>의 안전망이다(타임아웃 경로).
    ///
    /// <see cref="SetKinematic"/>과 다르다: 얼리는 것이 아니라 <b>물리 수면</b>이라, 밟히거나
    /// 밧줄이 걸리면(<see cref="WakeAll"/>) 그 자리에서 그대로 이어진다. 자세도 상태도 안 바꾼다.
    /// </summary>
    public void SleepAll()
    {
        if (m_bodies == null)
            return;

        for (int i = 0; i < m_bodies.Length; i++)
        {
            if (m_bodies[i] != null && !m_bodies[i].isKinematic)
                m_bodies[i].Sleep();
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

    // ---- 로컬 포즈 스냅샷 (#571 시체 얼림) ----
    //
    // <b>월드 캡처(<see cref="CapturePose"/>)와 용도가 다르다.</b> 저쪽은 "루트를 옮기는 동안 화면을
    // 그대로 두기"라 <b>같은 피어 안에서</b> 잠깐 들고 있는 값이다. 이쪽은 <b>다른 피어로 보내는</b>
    // 값이라 반드시 로컬(부모 기준)이어야 한다 — 원격의 루트는 다른 자리에 있으므로 월드 값을 보내면
    // 자세가 통째로 어긋난다.
    //
    // 로컬 회전 + 골반 로컬 위치만으로 자세가 복원되는 것은 애니메이션과 같은 이유다: 뼈 길이(자식의
    // 로컬 위치)는 관절이 유지하므로 바뀌지 않고, 나머지는 계층 수학이 만든다.
    //
    // ⚠ <b>순서가 피어마다 같아야 한다.</b> <see cref="m_bodies"/>는 같은 프리팹을 같은 방식으로
    // 훑어(GetComponentsInChildren + 레이어 필터) 모으므로 모든 피어에서 같은 순서다. 그 전제가 깨지면
    // 자세가 뒤섞이므로, 수집 방식을 바꿀 때 이 주석을 함께 볼 것.

    /// <summary>물리를 받는 뼈의 로컬 회전을 담아 간다 — 배열 길이는 <see cref="BoneCount"/>. (#571)</summary>
    /// <returns>담을 수 있으면 참 — 길이가 안 맞으면 거짓(아무것도 쓰지 않는다).</returns>
    public bool CaptureLocalPose(Quaternion[] rotations, out Vector3 hipsLocalPosition)
    {
        hipsLocalPosition = Vector3.zero;
        if (m_poseBones == null || rotations == null || rotations.Length != m_poseBones.Length)
            return false;

        for (int i = 0; i < m_poseBones.Length; i++)
            rotations[i] = m_poseBones[i].localRotation;

        hipsLocalPosition = m_hipsBone.localPosition;
        return true;
    }

    /// <summary>담아 온 로컬 회전을 그대로 입힌다 — 원격 피어가 얼린 자세를 재현할 때 쓴다. (#571)</summary>
    /// <returns>입혔으면 참 — 길이가 안 맞으면 거짓.</returns>
    public bool ApplyLocalPose(Quaternion[] rotations, Vector3 hipsLocalPosition)
    {
        if (m_poseBones == null || rotations == null || rotations.Length != m_poseBones.Length)
            return false;

        // 골반이 먼저다 — 자식들의 월드 위치가 골반의 로컬 위치 위에 얹히기 때문.
        m_hipsBone.localPosition = hipsLocalPosition;

        for (int i = 0; i < m_poseBones.Length; i++)
            m_poseBones[i].localRotation = rotations[i];

        return true;
    }

    // ---- 뼈 길이 (#728 후속 5) ----
    //
    // <b>"뼈 길이는 관절이 유지하므로 상수다"가 시체에서는 거짓이다.</b> 그 전제로 회전만 보내다가
    // 실측(2026-08-19)에서 갈렸다: 래그돌 <b>진입 시점 0.000m</b>이던 드리프트가 무너져 정착하고 나면
    // <b>0.036~0.206m</b>다(최악은 항상 <c>Spine_02</c>). 겹침 탈출과 솔버가 무너지는 동안 뼈를 늘려
    // 놓는데, 시체는 <see cref="RestoreBindPose"/>를 도는 <c>ExitRagdoll</c>을 영영 지나지 않아
    // <b>그 길이가 영구히 남는다.</b>
    //
    // 그래서 원격은 <b>바인드 길이 골격에 권위 쪽 회전</b>을 입히게 되고, 체인을 따라 오차가 쌓여
    // 팔·머리에서 최대 0.17m 다른 몸이 나온다 — 호스트에서는 등이 바닥에 붙어 있는데 클라에서는 떠
    // 보이던 증상이 이것이다.
    //
    // <b>실어 보내는 것은 신뢰 1회 패킷뿐이다</b>(정착·순간이동 — <c>RagdollPoseStreamer</c>).
    // 길이는 무너지는 동안 변하고 정착하면 상수인데, 화면에 오래 남는 것은 정착 자세다. 25Hz 스트림에
    // 매번 실으면 대역폭이 2.4배가 되고 얻는 것은 <b>이미 빠르게 움직이는 동안의</b> 정확도뿐이다.

    /// <summary>전 자세 뼈의 <b>로컬 위치</b>(= 뼈 길이)를 담아 간다 — 배열 길이는 <see cref="BoneCount"/>.</summary>
    /// <returns>담았으면 참 — 길이가 안 맞으면 거짓(아무것도 쓰지 않는다).</returns>
    public bool CaptureBoneLengths(Vector3[] lengths)
    {
        if (m_poseBones == null || lengths == null || lengths.Length != m_poseBones.Length)
            return false;

        for (int i = 0; i < m_poseBones.Length; i++)
            lengths[i] = m_poseBones[i].localPosition;

        return true;
    }

    /// <summary>
    /// 담아 온 뼈 길이를 입힌다 — 원격 전용(키네마틱일 때만 의미가 있다).
    ///
    /// ⚠ <b>골반은 건너뛴다.</b> 골반의 로컬 위치는 길이가 아니라 <b>자세</b>이고
    /// (<see cref="MaxBindPositionDrift"/>가 골반을 빼는 것과 같은 이유), 원격에서는 스트리머가
    /// 월드로 못박으므로 여기서 손대면 그 값과 싸운다.
    /// </summary>
    /// <returns>입혔으면 참 — 길이가 안 맞으면 거짓.</returns>
    public bool ApplyBoneLengths(Vector3[] lengths)
    {
        if (m_poseBones == null || lengths == null || lengths.Length != m_poseBones.Length)
            return false;

        for (int i = 0; i < m_poseBones.Length; i++)
        {
            if (m_poseBones[i] == m_hipsBone)
                continue;

            m_poseBones[i].localPosition = lengths[i];
        }

        return true;
    }

    // 부활 블렌드는 <see cref="RagdollPoseBlend"/>로 나갔다 (#571). 이 리그는 시체에 붙어 있는데
    // 블렌드는 살아있는 리그에서 일어나므로, 여기 두면 쓸 수 없는 자리에 코드가 남는다.

    // ---- 질의 ----

    // 몸이 "누웠다"고 보는 최소 기울기 — <b>수평 성분 / 전체 길이</b>로 잰다. 그 비율이 곧 수직에서
    // 기운 각도의 sin이므로 0.7은 약 45°다.
    //
    // ⚠ <b>절대 길이로 재면 안 된다.</b> 예전 가드는 수평 성분이 2cm보다 짧을 때만 거절했는데,
    // 서 있는 몸도 척추 곡선·이동 관성으로 머리가 골반보다 <b>5~6cm</b> 나가 있어서 그냥 통과했다.
    // 통과한 그 값은 몸이 향한 쪽이 아니라 <b>미세한 기울어짐의 방향</b>이라 프레임마다 춤춘다
    // (실측: 사망 직후 6프레임에 353.2° → 20.9° → 22.4° → 21.3° → 24.3° → 30.1°, 수평은 5.6cm).
    // 그 노이즈가 루트 yaw로 대입되면서 죽는 순간 몸이 <b>147° 뒤집혔다</b>.
    private const float k_lyingHorizontalRatio = 0.7f;

    /// <summary>
    /// 몸이 누운 방향의 yaw — 골반→머리를 지면에 투영한 값. 비행 중 추종과 정착 정렬이 <b>같은</b>
    /// 계산을 써야 정착 순간에 회전이 안 튄다.
    ///
    /// <b>누워 있지 않으면 거짓을 낸다</b> — 투영이 방향 정보를 잃기 때문이다. 부르는 쪽은 그때
    /// <b>기존 yaw를 유지</b>할 것 (<see cref="k_lyingHorizontalRatio"/>의 실측 기록 참고).
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
        float length = lengthwise.magnitude;
        if (length < 0.02f)
            return false; // 두 뼈가 겹쳐 있다 — 뺄 방향이 없다

        Vector3 horizontal = new Vector3(lengthwise.x, 0f, lengthwise.z);
        if (horizontal.magnitude < length * k_lyingHorizontalRatio)
            return false; // 아직 서 있다 — 방향을 못 정하니 기존 yaw를 유지한다

        yaw = Quaternion.LookRotation(horizontal.normalized).eulerAngles.y;
        return true;
    }
}
