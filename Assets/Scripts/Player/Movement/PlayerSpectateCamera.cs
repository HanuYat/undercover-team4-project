using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 사망 관전 시점 — 기능 정지(<see cref="IncapacitationCause.Die"/>) 동안 내 시체를 중심으로 도는
/// 3인칭 오빗 카메라. (#576)
///
/// <b>피벗이 루트가 아니라 시체(골반)다.</b> 래그돌 비행 중(#506) 루트는 제자리에 남고 yaw만 몸을
/// 따라가므로, 루트에 붙은 카메라는 폭발로 날아가는 자기 몸을 화면에서 놓친다. 정착한 뒤에는
/// 루트와 사실상 같은 자리이고, 동료가 시체를 옮기는 동안(#365)에도 골반을 보면 그대로 따라간다.
///
/// <b>포즈를 스스로 대입하지 않는다</b> — 월드 포즈를 내주기만 하고 카메라에 넣는 것은
/// <see cref="PlayerLook.UpdateCameraPose"/>다. 카메라 transform을 밖에서 만지면 그쪽이 매 프레임
/// 통째로 덮어써 그 프레임에 지워진다(#477).
///
/// 순수 로컬 표현이다 — 동기화할 상태가 없고 서버 권위와 무관하다(<see cref="PlayerRagdoll"/>과 같은 성격).
/// </summary>
public class PlayerSpectateCamera : MonoBehaviour
{
    [Tooltip("시체(골반)에서 카메라가 도는 중심까지의 높이(m)")]
    [SerializeField] private float m_pivotHeight = 0.6f;

    [Tooltip("중심에서 카메라까지의 거리(m)")]
    [SerializeField] private float m_distance = 3.5f;

    [Tooltip("오빗 피치 하한(음수=카메라가 중심보다 낮아진다). 너무 낮추면 바닥을 파고든다")]
    [SerializeField] private float m_minPitch = -10f;

    [Tooltip("오빗 피치 상한(양수=위에서 내려다본다). 시체를 내려다보는 쪽을 넉넉히 연다")]
    [SerializeField] private float m_maxPitch = 70f;

    [Tooltip("관전 진입 시 시작 피치 — 살짝 내려다보는 각에서 출발한다")]
    [SerializeField] private float m_enterPitch = 20f;

    [Tooltip("1인칭↔관전 전환 보간 속도. 4면 약 1초에 걸쳐 뒤로 빠진다")]
    [SerializeField] private float m_blendSpeed = 4f;

    [Tooltip("카메라가 벽을 파고들지 않게 띄울 반경(m)")]
    [SerializeField] private float m_probeRadius = 0.25f;

    [Tooltip("카메라 충돌 판정 레이어 — 플레이어·트리거는 빼 둘 것 (감정표현 3인칭과 같은 값)")]
    [SerializeField] private LayerMask m_collisionMask = ~0;

    private RagdollRig m_ownRig;             // 내 골반 — 대상이 없을 때의 피벗
    private PlayerIncapacitation m_self;     // 나 자신 — 순환 목록에서 걸러낼 기준
    private PlayerInputHandler m_input;      // 휠 순환 입력 (#590)

    // 지금 보고 있는 동료 — null이면 내 시체다. 순환 고리의 원점이라 "없음"을 별도 플래그로 두지 않는다.
    private PlayerIncapacitation m_target;
    private RagdollRig m_targetRig;

    // 순환 고리 재사용 버퍼 — 휠을 굴릴 때마다 새로 만들지 않는다. 0번은 항상 내 시체(null)다.
    private readonly List<PlayerIncapacitation> m_ring = new();

    private bool m_active;
    private float m_blend; // 1인칭(0) ↔ 관전(1) 진행도
    private bool m_snap;   // 다음 Tick에서 보간을 끊고 현재 상태를 즉시 반영한다
    private float m_pitch;

    // 좌우 각. <b>기준이 대상에 따라 다르다</b> — 내 시체를 볼 때는 월드 절대각이고, 동료를 볼 때는
    // 그 동료의 yaw에 얹는 <b>상대각</b>이다(0이면 정확히 뒤통수). 동료는 계속 움직이므로 절대각으로
    // 잡으면 조금만 걸어가도 옆구리·정면이 보인다 — "뒤를 따라간다"가 성립하지 않는다.
    private float m_yaw;

    /// <summary>관전이 요청된 상태인가 — 블렌드가 끝났는지와는 별개다.</summary>
    public bool IsActive => m_active;

    /// <summary>내 시체를 보고 있는가 — <see cref="PlayerLook"/>이 내 몸 렌더 여부를 이걸로 가른다. (#590)</summary>
    public bool IsWatchingSelf => m_target == null;

    private void Awake()
    {
        m_ownRig = GetComponentInChildren<RagdollRig>();
        m_ownRig?.EnsureCollected(); // Awake 순서는 보장되지 않는다 — 멱등이라 중복 호출은 무해하다
        m_self = GetComponent<PlayerIncapacitation>();
        m_input = GetComponent<PlayerInputHandler>();
    }

    // 좌클릭(아이템 사용)을 그대로 빌린다 (#590) — 무력화 중에는 아이템 사용이 막히므로
    // (PlayerItemUser) 죽어 있는 동안 이 입력은 놀고 있다. 새 액션을 만들면 입력 에셋·
    // PlayerInputHandler·프리팹 세 곳을 건드려야 하는데, 우클릭은 아예 바인딩이 없어 그 비용이 든다.
    // 비오너 인스턴스는 PlayerInputHandler가 스스로 비활성화돼 이벤트를 발행하지 않는다.
    private void OnEnable()
    {
        if (m_input != null)
            m_input.OnUseItemStarted += CycleNext;
    }

    private void OnDisable()
    {
        if (m_input != null)
            m_input.OnUseItemStarted -= CycleNext;
    }

    // 한 방향으로만 돈다 — 고리가 작아(보통 3~5칸) 뒤로 갈 일이 거의 없고, 역방향을 주려면
    // 우클릭 액션을 새로 만들어야 한다. 한 바퀴 돌면 내 시체로 돌아온다.
    private void CycleNext() => CycleTarget(1);

    /// <summary>
    /// 관전 대상을 한 칸 옮긴다 — 고리는 [내 시체, 살아 있는 동료들…]이다. (#590)
    /// 죽은 동료는 넣지 않는다: 볼 것이 시체뿐이라 칸만 늘리고, 내 시체와 구분도 안 된다.
    /// </summary>
    public void CycleTarget(int direction)
    {
        if (!m_active || direction == 0)
            return;

        RebuildRing();

        int current = m_ring.IndexOf(m_target);
        if (current < 0)
            current = 0; // 보던 대상이 고리에서 빠졌다 — 내 시체부터 다시 센다

        int count = m_ring.Count;
        int next = ((current + direction) % count + count) % count;
        SetTarget(m_ring[next]);
    }

    // 고리를 다시 만든다. PlayerIncapacitation.All은 전원 순회용 무할당 목록이다 (#365에서 도입).
    private void RebuildRing()
    {
        m_ring.Clear();
        m_ring.Add(null); // 0번 = 내 시체 — 언제든 돌아올 수 있어야 한다

        IReadOnlyList<PlayerIncapacitation> all = PlayerIncapacitation.All;
        for (int i = 0; i < all.Count; i++)
        {
            PlayerIncapacitation candidate = all[i];
            if (candidate == null || candidate == m_self || candidate.IsDead)
                continue;

            m_ring.Add(candidate);
        }
    }

    // 대상 전환. 좌우 각의 기준이 대상에 따라 달라지므로(m_yaw 주석) 갈아탈 때 환산해 준다 —
    // 안 하면 전환 순간 화면이 대상 yaw만큼 홱 돈다.
    private void SetTarget(PlayerIncapacitation target)
    {
        if (m_target == target)
            return;

        float previousBase = TargetBaseYaw();

        m_target = target;
        m_targetRig = target != null ? target.GetComponentInChildren<RagdollRig>() : null;
        m_targetRig?.EnsureCollected();

        // 동료로 갈아타면 뒤통수(상대각 0)에서 시작한다 — 갈아탄 직후 옆구리가 보이면 누구를 보는지
        // 알기 어렵다. 내 시체로 돌아올 때는 직전 절대각을 그대로 이어받아 화면이 튀지 않게 한다.
        m_yaw = target != null ? 0f : previousBase + m_yaw;
    }

    // 대상이 사라지거나 죽었으면 고리에서 다음 칸으로 넘긴다 — 아무도 없으면 내 시체로 돌아온다.
    private void EnsureTargetValid()
    {
        if (m_target == null)
            return;

        // Unity의 파괴된 오브젝트는 == null이 참이 되므로 퇴장·디스폰도 여기서 걸린다.
        if (m_target.isActiveAndEnabled && !m_target.IsDead)
            return;

        CycleTarget(1);

        // 한 바퀴 돌아도 살아 있는 동료가 없으면 CycleTarget이 같은 자리에 남길 수 있다.
        if (m_target != null && (!m_target.isActiveAndEnabled || m_target.IsDead))
            SetTarget(null);
    }

    // 좌우 각의 기준값 — 동료를 볼 때는 그 동료의 yaw(뒤통수 기준), 내 시체는 월드 절대각(0).
    private float TargetBaseYaw() =>
        m_target != null ? m_target.transform.eulerAngles.y : 0f;

    /// <summary>
    /// 관전 진입/이탈. <paramref name="entryYaw"/>는 지금 보고 있는 월드 yaw다 —
    /// 각을 새로 잡으면 전환 첫 프레임에 화면이 홱 돈다.
    /// </summary>
    public void SetSpectating(bool spectating, float entryYaw)
    {
        if (m_active == spectating)
            return;

        m_active = spectating;

        if (!spectating)
        {
            // 관전 대상이 남지 않게 한다 (#590) — 남기면 다음 사망이 엉뚱한 동료를 보며 시작하고,
            // 라운드가 바뀌어 그 동료가 없어졌으면 첫 프레임이 무효 대상을 잡는다.
            SetTarget(null);
            return;
        }

        m_yaw = entryYaw;
        m_pitch = m_enterPitch;
    }

    /// <summary>
    /// 마우스 입력을 오빗 각에 누적한다 — 좌우는 자유, 상하는 제한.
    /// 부호는 <see cref="PlayerLook.HandleLook"/>과 같다(양수 피치=아래).
    /// </summary>
    public void AddLook(Vector2 delta)
    {
        m_yaw += delta.x;
        m_pitch = Mathf.Clamp(m_pitch - delta.y, m_minPitch, m_maxPitch);
    }

    /// <summary>
    /// 블렌드를 한 프레임 진행시키고 그 값(1인칭 0 ↔ 관전 1)을 낸다.
    /// 관전 중이 아니어도 매 프레임 불러야 이탈 보간이 진행된다.
    /// </summary>
    public float Tick()
    {
        if (m_snap)
        {
            m_snap = false;
            m_blend = m_active ? 1f : 0f;
            return m_blend;
        }

        m_blend = Mathf.Lerp(m_blend, m_active ? 1f : 0f, m_blendSpeed * Time.deltaTime);

        if (m_blend < 0.001f)
            m_blend = 0f;

        return m_blend;
    }

    /// <summary>
    /// 다음 <see cref="Tick"/>에서 보간을 끊고 현재 상태를 즉시 반영한다 — <b>몸이 순간이동했을 때</b>
    /// 부른다(<see cref="PlayerMovement"/>의 포즈 대입). 옮겨간 자리에서 옛 화면으로 1초 쓸려 들어올
    /// 이유가 없다.
    ///
    /// <b>0으로 지우는 게 아니라 목표로 튀는 것</b>이 핵심이다 — 죽은 채로 옮겨지는 경로가 실제로
    /// 있고(본부 부활 장치 안치, #365), 거기서 0으로 지우면 아직 시체인데 화면만 1인칭으로 돌아간다.
    ///
    /// 즉시 대입하지 않고 한 프레임 미루는 이유는 <b>호출 순서</b>다. 세션 유지 씬 전환에서 재배치
    /// (PlayerSpawnManager)와 부활 해제(ShopManager)가 둘 다 씬 로드에 물려 있는데 Start 순서가
    /// 정해져 있지 않다. Unity는 그 프레임의 Start를 전부 돌린 뒤 Update를 돌리므로, Tick 시점에는
    /// 어느 쪽이 먼저였든 상태가 확정돼 있다.
    /// </summary>
    public void SnapNextTick() => m_snap = true;

    /// <summary>
    /// 관전 카메라의 <b>월드</b> 포즈. 골반이 없으면 false — 호출자는 1인칭 포즈를 그대로 쓴다.
    /// </summary>
    public bool TryGetPose(out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = default;

        EnsureTargetValid(); // 보던 동료가 죽거나 나갔으면 여기서 넘긴다

        RagdollRig rig = m_target != null ? m_targetRig : m_ownRig;
        Transform hips = rig != null ? rig.Hips : null;
        if (hips == null)
            return false; // 리그가 없는 구성(테스트 씬 등) — 기존 바닥 시점으로 남는다

        Vector3 pivot = hips.position + Vector3.up * m_pivotHeight;

        // 동료를 볼 때는 그 동료의 yaw에 얹는다 — 걸어가는 동안 뒤통수를 유지하려면 기준이 함께 돌아야 한다.
        rotation = Quaternion.Euler(m_pitch, TargetBaseYaw() + m_yaw, 0f);

        // 벽을 파고들지 않게 당긴다. 감정표현 3인칭(#219)과 같은 SphereCast 1회 — 맵 교체가
        // 예정돼 있어 여기서 완벽한 충돌 대응을 만들 이유가 없다.
        Vector3 back = rotation * Vector3.back;
        float distance = m_distance;
        if (Physics.SphereCast(pivot, m_probeRadius, back, out RaycastHit hit,
                distance, m_collisionMask, QueryTriggerInteraction.Ignore))
        {
            distance = Mathf.Max(hit.distance - m_probeRadius, 0f);
        }

        position = pivot + back * distance;
        return true;
    }
}
