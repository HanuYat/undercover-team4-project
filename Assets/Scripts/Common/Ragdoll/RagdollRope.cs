using UnityEngine;

/// <summary>
/// 시체에 <b>밧줄을 묶어</b> 물리로 끌려오게 한다. (#365 운반 / #398 드래그, #506에서 물리로 이관)
///
/// <see cref="RagdollRig"/>와 짝이고, 리그처럼 <b>네트워크·권위를 모른다</b> — 각 피어가 자기 로컬
/// 시체에 자기 밧줄을 묶는다. 끌기 시작/종료를 누가 결정하는지는 소유자의 사정이다.
///
/// <b>거리 제한이지 스프링이 아니다.</b> <c>linearLimit</c>만 걸고 힘은 <b>한계 바깥에서만</b> 생긴다 —
/// 밧줄 길이 안에서는 시체가 완전히 자유롭고(중력대로 눕고 구른다), 길이를 넘는 순간에만 관절이
/// 붙잡는다. 실제 밧줄이 그렇게 동작한다.
///
/// 이 구분이 예산 문제를 없앤다. 상시 작용하는 스프링으로 끌려다니게 만들려던 앞의 시도는
/// 마찰(412N)·골반 무게(107N)·질량(70kg) 사이에서 답이 없었다 — 세면 뜨고 약하면 안 끌린다.
/// 거리 제한은 수평으로는 마찰을 이기면서 수직으로는 늘어져 있는 한 아무 힘도 주지 않는다.
///
/// 회전은 <b>구속</b>하지 않는다 — 시체는 끌리면서 자유롭게 굴러야 한다. 각 감쇠는 구속이 아니라
/// 마찰이므로 별개다(팽이처럼 도는 것만 잡는다).
///
/// ⚠ <b>NPC의 기존 밧줄(<c>NpcRopeDrag</c>)과 다른 물건이다.</b> 그쪽은 서버가
/// <c>transform.position</c>을 직접 대입하고 NetworkTransform이 복제하는 방식이다. 래그돌이 된 몸은
/// 동적 리지드바디라 그 방식이 통하지 않아(부모 트랜스폼을 따르지 않는다) 이 관절 방식이 필요했다.
/// NPC 래그돌을 붙일 때 두 방식을 하나로 합치려 들지 말 것 — 대상이 래그돌이냐 아니냐로 갈린다.
/// </summary>
[RequireComponent(typeof(RagdollRig))]
public class RagdollRope : MonoBehaviour
{
    [Tooltip("밧줄 길이(m) — 운반자의 손과 시체 골반 사이의 최대 거리. 이 안에서는 시체가 자유롭고, " +
             "넘어가면 아래 강성·감쇠가 잡는다. 길게 잡으면 장력이 덜 걸려 전체적으로 순해진다. " +
             "⚠ 앵커가 손(약 1.1m)이라 손 높이보다 짧으면 시체가 바닥에 닿지 못하고 매달린다. " +
             "바닥에 누운 채 끌리는 수평거리 = √(길이² − (손높이 − 골반높이)²) — " +
             "2.0이면 약 1.8m 뒤에서 끌린다")]
    [SerializeField] private float m_length = 2f;

    [Tooltip("밧줄이 한계를 넘었을 때 되당기는 강성 — <b>한계 바깥에서만</b> 작동한다(늘어져 있으면 " +
             "힘이 0이라 시체를 들어올리지 못한다). 0이면 하드 리밋이 되어 위반량을 한 스텝에 " +
             "해소하며 시체를 11m/s로 튕긴다. 시체 70kg을 마찰(약 412N)에 맞서 끌려면 1500에서 " +
             "약 27cm 늘어난다 — 밧줄이 하중을 받아 늘어나는 정도라 자연스럽다")]
    [SerializeField] private float m_limitSpring = 1500f;

    [Tooltip("같은 한계의 감쇠 — <b>과감쇠로 둔다.</b> 임계는 약 2√(강성×질량) = 2√(1500×70) ≈ 650이고 " +
             "1000이면 ζ≈1.5다. 부족감쇠(예전 3000/600, ζ≈0.65)면 팽팽해질 때마다 오버슛으로 속도를 " +
             "얹는데, 늘어진 반주기에는 이 감쇠가 0이라 뺄 방법이 없다 — 운반자가 제자리에서 돌면 " +
             "회전 주기마다 에너지가 쌓여 시체가 점점 빨라지고 놓는 순간 날아간다 (§9-17)")]
    [SerializeField] private float m_limitDamper = 1000f;

    [Tooltip("밧줄에 묶인 동안 뼈에 거는 선형 감쇠(1/s) — <b>늘어진 구간의 유일한 에너지 배출구다.</b> " +
             "한계 감쇠는 밧줄이 팽팽할 때만 작동하므로 이것이 없으면 넣기만 하고 빼지 않는 펌프가 된다. " +
             "0.6이면 시정수 약 1.7초. 끌리는 저항이 늘어 밧줄이 조금 더 늘어난다(2m/s에서 약 84N)")]
    [SerializeField] private float m_dragLinearDamping = 0.6f;

    [Tooltip("같은 구간의 각 감쇠 — 팽이처럼 계속 도는 것을 잡는다. 평시 뼈 값은 0.05로 사실상 없다. " +
             "너무 올리면 끌릴 때 몸이 뻣뻣해져 흐느적임이 죽으므로 선형 감쇠부터 올려 볼 것")]
    [SerializeField] private float m_dragAngularDamping = 0.6f;

    [Tooltip("밧줄에 묶인 동안 뼈 속도의 <b>하드 상한</b>(m/s) — 슬링 차단용이다. 0이면 끈다. " +
             "감쇠로는 못 막는다: 운반자가 달리며 원을 그리면 장력이 하는 일이 ω²로 커지는데 " +
             "감쇠 배출은 v에 비례해, 빨리 돌수록 입력이 이긴다(§9-18). 기본 8은 스프린트 속도와 " +
             "같다 — 끌려가는 시체가 끄는 사람보다 빠를 이유는 없고, 넘는 만큼은 전부 슬링이다")]
    [SerializeField] private float m_maxSpeed = 8f;

    // ⚠ <b>감쇠·속도캡은 밧줄에 묶인 동안에만 건다.</b> 상시로 걸면 사망 직후의 비행이 같이 죽는다 —
    // 그쪽은 탄도로 남아야 하고(임펄스가 유일한 입력), 오히려 더 날려야 하는 방향이다.
    // 걸고 푸는 자리는 밧줄의 수명과 정확히 같다: ApplyTuning ↔ Detach.

    private RagdollRig m_rig;
    private Rigidbody m_anchor; // 운반자 손을 따라가는 키네마틱 앵커 — 밧줄의 끝
    private GameObject m_anchorObject; // 파괴용 — 앵커는 부모가 없어 씬에 남는다
    private ConfigurableJoint m_joint; // 골반 ↔ 앵커, 거리 제한(= 밧줄)
    private Transform m_carrier; // 밧줄을 쥔 쪽

    // 마지막으로 적용한 튜닝 값 — 인스펙터에서 바뀐 프레임에만 다시 쓰기 위한 비교용.
    // (관절 프로퍼티 대입과 뼈 순회를 매 물리 스텝 돌리지 않는다)
    private Vector4 m_appliedTuning;
    private float m_appliedAngularDamping;

    /// <summary>지금 밧줄이 묶여 있는가.</summary>
    public bool IsAttached => m_joint != null;

    private bool TuningChanged =>
        m_appliedTuning != new Vector4(m_length, m_limitSpring, m_limitDamper, m_dragLinearDamping)
        || m_appliedAngularDamping != m_dragAngularDamping;

    private void Awake()
    {
        m_rig = GetComponent<RagdollRig>();
        m_rig.EnsureCollected(); // Awake 순서는 보장되지 않는다 — 아래 Attach가 뼈를 요구한다

        // ⚠ <b>앵커를 여기서 만들지 않는다</b> — 실제로 묶는 순간(<see cref="Attach"/>)에 만든다.
        // 앵커는 부모가 없어 씬에 그대로 떠 있는 오브젝트인데, NPC까지 밧줄 대상이 되면서(#571)
        // 라운드당 100구가 이 컴포넌트를 들고 있다. 미리 만들면 <b>한 번도 안 쓸 앵커가 100개</b>
        // 뜬다. Attach가 어차피 쓰기 직전에 다시 보장하므로(EnsureAnchor 주석의 씬 전환 사정)
        // 지연시켜도 잃는 것이 없다.
    }

    private void OnDestroy()
    {
        if (m_anchorObject != null)
            Destroy(m_anchorObject);
    }

    private void FixedUpdate() => Tick();

    /// <summary>
    /// 밧줄 앵커를 보장한다 — <b>부모 없는 키네마틱 Rigidbody.</b> <b>멱등이다.</b>
    ///
    /// 시체의 루트에 매달지 않는다 — 앵커가 따라가야 하는 것은 <b>운반자</b>이지 시체 자신이 아니다.
    /// 자기 루트의 자식으로 두면 "시체가 자기를 끄는" 꼴이 된다.
    /// 콜라이더는 붙이지 않는다 — 세계와 부딪히지 않는 순수한 손잡이다.
    ///
    /// ⚠ <b>Awake에서 한 번 만드는 것으로는 부족하다 — <see cref="Attach"/>에서 다시 보장한다.</b>
    /// 부모가 없다는 것은 곧 <b>활성 씬에 놓인다</b>는 뜻이고, 씬 전환은
    /// <c>LoadSceneMode.Single</c>이라(<c>AppHelper.LoadSceneAsync</c>) 기존 씬을 통째로 버린다.
    /// Player는 NetworkObject라 NGO가 넘겨 주지만 <b>이 앵커는 아니라서 씬과 함께 죽는다</b> —
    /// 그러면 <c>m_anchor</c>가 파괴된 참조로 남아 Attach가 조용히 중단되고, 시체는 끄는 힘을
    /// 하나도 못 받는다(실제로 밟았다: 맵을 새로 만들어 붙인 뒤 그 씬에서만 안 끌렸다).
    ///
    /// <c>DontDestroyOnLoad</c>로 올리지 않는 이유는 앵커가 <b>시체마다 하나씩</b>이기 때문이다 —
    /// 상주로 만들면 씬을 넘나드는 쓰레기가 쌓이고, 물리 씬이 갈리면 관절이 아예 안 걸린다.
    /// 대신 쓰기 직전에 다시 만든다(<see cref="RagdollRig.EnsureCollected"/>와 같은 멱등 보장).
    /// </summary>
    private void EnsureAnchor()
    {
        if (m_anchor != null) // 파괴됐으면 Unity의 가짜 null이라 여기서 걸러진다
            return;
        if (m_rig == null || !m_rig.IsValid)
            return;

        GameObject anchor = new GameObject($"RopeAnchor ({name})");
        m_anchor = anchor.AddComponent<Rigidbody>();
        m_anchor.isKinematic = true;
        m_anchor.useGravity = false;
        m_anchorObject = anchor;
    }

    /// <summary>밧줄을 시체에 <b>묶는다</b> — 운반자가 움직이면 물리가 시체를 끌어온다.</summary>
    /// <param name="carrier">운반자(밧줄을 쥔 쪽). 매 물리 스텝 이 위치를 따라 앵커가 움직인다.</param>
    public void Attach(Transform carrier)
    {
        // ⚠ <b>Detach가 먼저다.</b> 멱등을 위해 기존 관절을 끊고 감쇠를 되돌리는데, 그 과정에서
        // m_carrier를 지우므로 순서를 뒤집으면 방금 받은 운반자가 곧바로 날아간다(밧줄이 안 따라간다).
        Detach();

        m_carrier = carrier;

        // 앵커가 씬 전환에 쓸려 갔을 수 있다 — 쓰기 직전에 다시 보장한다 (EnsureAnchor 주석).
        EnsureAnchor();

        if (carrier == null || m_rig == null || m_rig.HipsBody == null || m_anchor == null)
            return;

        // 앵커를 운반자 자리에 먼저 옮긴다. 관절은 만들어진 순간의 상대 포즈를 기준으로 삼으므로
        // 순서가 뒤바뀌면 엉뚱한 기준이 굳는다 (그렇게 만들었다가 시체가 0.88m 떠올랐다).
        m_anchor.position = carrier.position;

        m_joint = m_rig.HipsBody.gameObject.AddComponent<ConfigurableJoint>();
        m_joint.autoConfigureConnectedAnchor = false;
        m_joint.anchor = Vector3.zero; // 골반 피벗
        m_joint.connectedAnchor = Vector3.zero; // 앵커 원점
        m_joint.connectedBody = m_anchor;

        // 전 축 Limited + 반경 = 밧줄 길이. 구면 안에서는 자유, 표면에서 잡힌다.
        m_joint.xMotion = ConfigurableJointMotion.Limited;
        m_joint.yMotion = ConfigurableJointMotion.Limited;
        m_joint.zMotion = ConfigurableJointMotion.Limited;

        m_joint.angularXMotion = ConfigurableJointMotion.Free;
        m_joint.angularYMotion = ConfigurableJointMotion.Free;
        m_joint.angularZMotion = ConfigurableJointMotion.Free;

        m_joint.projectionMode = JointProjectionMode.None; // §9-2 — projection은 충돌을 무시한다
        m_joint.enableCollision = false;

        ApplyTuning(); // 길이·강성·감쇠 — 관절 생성과 분리해 Play 중에도 다시 적용할 수 있게
        m_rig.WakeAll(); // 잠든 시체는 관절 힘만으로는 안 깨어날 수 있다
    }

    /// <summary>밧줄을 푼다 — 내려놓기·부활·운반자 소실.</summary>
    public void Detach()
    {
        m_carrier = null;

        if (m_joint == null)
            return;

        Destroy(m_joint);
        m_joint = null;
        m_rig?.RestoreDamping(); // 감쇠는 밧줄의 수명과 같다 — 풀면 다시 탄도로 돌아간다
    }

    // 앵커를 운반자 손 위치로 옮긴다 — 물리 스텝마다. 앵커는 키네마틱이라 이 이동이 곧 밧줄의
    // 장력이 되고, 시체는 그 장력에 끌린다. 우리가 시체 위치를 계산하지 않는 것이 핵심이다.
    private void Tick()
    {
        if (m_anchor == null || m_carrier == null)
            return;

        // Play 중 인스펙터에서 값을 바꾸면 밧줄을 다시 잡지 않아도 바로 먹는다 (튜닝용).
        if (TuningChanged)
            ApplyTuning();

        m_anchor.MovePosition(m_carrier.position);

        // 밧줄이 팽팽해지는 순간 시체가 자고 있으면 장력을 못 받는다.
        if (m_rig.HipsBody != null && m_rig.HipsBody.IsSleeping())
            m_rig.WakeAll();

        m_rig.ClampSpeed(m_maxSpeed); // 슬링 차단 — 장력을 적용한 뒤에 자른다
    }

    /// <summary>
    /// 밧줄의 튜닝 값(길이·한계 스프링·뼈 감쇠)을 지금 값으로 적용한다.
    ///
    /// 관절 생성과 분리해 둔 이유는 <b>Play 중 인스펙터 조정</b>이다 — 예전에는 이 값들이
    /// <see cref="Attach"/> 안에 인라인이라 밧줄을 다시 잡아야 새 값이 먹었다.
    ///
    /// <b>한계 스프링은 한계 "바깥"에서만 작동한다</b> — 앞서 폐기한 xDrive 스프링과 범주가 다르다.
    /// 그건 상시 작용해서 시체를 들어올리거나 지면과 싸웠지만, 이건 밧줄이 늘어져 있는 동안
    /// (한계 안)에는 힘이 정확히 0이다. 그래서 세게 잡아도 시체가 뜨지 않는다.
    /// spring=0(하드 리밋)으로 두면 위반량을 솔버가 한 스텝에 해소하며 큰 속도를 실어준다 —
    /// 실측: 시체가 <b>11.6m/s</b>로 튀어 운반자를 지나쳐 손↔골반 2.12m → 0.33m까지 오버슛했고,
    /// 그 야크가 캡슐(=루트)에 실려 원격으로 나가 격차가 14.65m까지 벌어졌다.
    ///
    /// <b>뼈 감쇠가 여기 같이 있는 이유</b>(§9-17): 한계 감쇠는 팽팽할 때만 일하므로, 늘어진
    /// 반주기에는 에너지를 뺄 수단이 하나도 없다. 운반자가 제자리에서 도는 동안 밧줄은 팽팽↔늘어짐을
    /// 반복하는데, 팽팽 구간에서 부족감쇠 오버슛이 속도를 얹고 늘어짐 구간에서 그대로 유지되면
    /// <b>회전 주기마다 에너지가 쌓이는 펌프</b>가 된다 — 돌릴수록 빨라지다 놓는 순간 날아간다.
    /// 한계를 과감쇠로 만들어 주입을 없애고(위 툴팁), 뼈 감쇠로 배출구를 연다. 둘은 짝이다.
    ///
    /// 흐느적임은 이 스프링의 오버슛이 아니라 <b>손의 걸음 흔들림</b>이 만든다
    /// (<c>PlayerTowedMotion.ResolveRopeAnchor</c>) — 과감쇠로 바꿔도 그쪽은 그대로 남는다.
    /// </summary>
    private void ApplyTuning()
    {
        if (m_joint == null)
            return;

        m_joint.linearLimit = new SoftJointLimit { limit = Mathf.Max(0.1f, m_length) };
        m_joint.linearLimitSpring = new SoftJointLimitSpring
        {
            spring = m_limitSpring,
            damper = m_limitDamper,
        };

        m_rig.SetDamping(m_dragLinearDamping, m_dragAngularDamping);

        m_appliedTuning = new Vector4(m_length, m_limitSpring, m_limitDamper, m_dragLinearDamping);
        m_appliedAngularDamping = m_dragAngularDamping;
    }
}
