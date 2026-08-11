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

    private RagdollRig m_rig; // 피벗으로 쓸 골반 뼈

    private bool m_active;
    private float m_blend; // 1인칭(0) ↔ 관전(1) 진행도
    private bool m_snap;   // 다음 Tick에서 보간을 끊고 현재 상태를 즉시 반영한다
    private float m_yaw;
    private float m_pitch;

    /// <summary>관전이 요청된 상태인가 — 블렌드가 끝났는지와는 별개다.</summary>
    public bool IsActive => m_active;

    private void Awake()
    {
        m_rig = GetComponentInChildren<RagdollRig>();
        m_rig?.EnsureCollected(); // Awake 순서는 보장되지 않는다 — 멱등이라 중복 호출은 무해하다
    }

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
            return;

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

        Transform hips = m_rig != null ? m_rig.Hips : null;
        if (hips == null)
            return false; // 리그가 없는 구성(테스트 씬 등) — 기존 바닥 시점으로 남는다

        Vector3 pivot = hips.position + Vector3.up * m_pivotHeight;
        rotation = Quaternion.Euler(m_pitch, m_yaw, 0f);

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
