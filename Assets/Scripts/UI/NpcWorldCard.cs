using UnityEngine;

/// <summary>
/// NPC 머리 위에 붙는 월드공간 표시물의 공통 뼈대. (#233 → #493)
/// NPC 프리팹의 자식으로 배치되어 부모-자식 Transform으로 위치를 따라간다 — 별도 위치 갱신 로직 불필요.
/// 기본은 비활성 — 로컬 플레이어가 조준할 때만 플레이어 쪽 프레젠터가 켠다.
///
/// 이 베이스가 맡는 것은 셋이다:
///   · <b>빌보드</b> — 카메라를 향해 회전. 프레젠터가 <see cref="SetCamera"/>로 로컬 플레이어 카메라를 준다.
///   · <b>누운 자세 보정</b> — 머리 위 고정 오프셋은 몸이 바닥에 깔리면 허공에 뜬다.
///   · <b>표시 토글</b> — 오브젝트째 껐다 켠다(배경까지 함께 사라져야 한다).
///
/// 파생은 "무엇을 보여줄지"만 맡는다 (<see cref="ScanInfoView"/> 정보 카드,
/// <see cref="NpcHealthBarView"/> 체력 바).
/// </summary>
public abstract class NpcWorldCard : MonoBehaviour
{
    [Header("빌보드")]
    [Tooltip("켜져 있는 동안 카메라를 향하도록 회전한다. 끄면 프리팹의 고정 방향을 유지")]
    [SerializeField]
    private bool m_billboard = true;

    [Header("누운 자세 위치")]
    [Tooltip(
        "NPC가 누워 있을 때(기절·밧줄 끌림) 위치(NPC 루트 로컬) — 머리가 -Z라 표시도 머리 쪽으로 밀린다. "
            + "서 있을 때 위치는 프리팹 값을 그대로 쓴다"
    )]
    [SerializeField]
    private Vector3 m_proneOffset = new Vector3(0.1f, 0.72f, -0.4f);

    private Transform m_cameraTransform;
    private RectTransform m_rect;

    // 누운 자세를 알려주는 표현 계층 — NPC 루트에 있다. 표시물을 NPC 밖에 단독으로 두면(테스트 씬) null이고,
    // 그때는 위치를 건드리지 않는다.
    private NpcAnimationDriver m_driver;

    // 서 있을 때 위치 — 프리팹 초기값에서 캡처해 그대로 되돌린다 (NpcProneCollider가 서기 캡슐 값을
    // 캡처하는 것과 같은 관례). 상수로 박으면 프리팹에서 높이를 다시 잡았을 때 복원값만 조용히 어긋난다.
    private Vector3 m_standOffset;

    protected virtual void Awake()
    {
        m_rect = transform as RectTransform;
        if (m_rect != null)
            m_standOffset = m_rect.anchoredPosition3D;

        // 표시물은 기본 비활성이라 활성화되기 전에는 이 Awake도 돌지 않는다 — 그래서 여기서 캡처하는 값은
        // 항상 아직 아무도 손대지 않은 프리팹 초기값이다(이 스크립트 외에 위치를 옮기는 코드는 없다).
        m_driver = GetComponentInParent<NpcAnimationDriver>(true);
    }

    protected virtual void OnEnable()
    {
        if (m_driver == null)
            return;

        m_driver.OnProneChanged += ApplyProne;

        // 꺼져 있는 동안은 이 컴포넌트가 돌지 않아 그 사이의 눕기/일어나기를 놓친다 —
        // 조준으로 다시 켜질 때마다 현재 자세로 맞춘다 (NpcProneCollider.OnEnable이 재적용하는 것과 같은 이유).
        ApplyProne(m_driver.IsProne);
    }

    protected virtual void OnDisable()
    {
        if (m_driver != null)
            m_driver.OnProneChanged -= ApplyProne;
    }

    /// <summary>
    /// 빌보드가 바라볼 카메라를 지정한다. 프레젠터가 로컬 플레이어 카메라(PlayerInteractor.AimCamera)를
    /// 넘겨준다 — Camera.main에 의존하면 플레이어 카메라에 MainCamera 태그가 없을 때 회전이 멈춘다.
    /// </summary>
    public void SetCamera(Transform cameraTransform)
    {
        if (cameraTransform != null)
            m_cameraTransform = cameraTransform;
    }

    /// <summary>표시물을 끈다.</summary>
    public void Hide() => SetCardActive(false);

    protected void SetCardActive(bool active)
    {
        if (gameObject.activeSelf != active)
            gameObject.SetActive(active);
    }

    // 누움 여부에 맞춰 표시물을 올리고 내린다. anchoredPosition3D는 인스펙터의 Pos X/Y/Z와 같은 값이다 —
    // 프리팹이 서기 위치를 anchoredPosition(0, 2.5) + z 0으로 들고 있어 localPosition을 쓰면 y를 놓친다.
    // 눕기/서기는 즉시 스냅한다: 모션도 스냅이고, 몸통 콜라이더(NpcProneCollider)도 같은 이벤트로 같은 순간에 눕는다.
    private void ApplyProne(bool prone)
    {
        if (m_rect == null)
            return;

        m_rect.anchoredPosition3D = prone ? m_proneOffset : m_standOffset;
    }

    // 켜져 있을 때만 돈다(비활성이면 호출되지 않음). 표시물의 회전을 카메라 회전에 맞춰(forward·up 동일)
    // 화면과 평행하게 만든다 — UI 캔버스는 이 방향에서 글자가 반전 없이 정면으로 읽힌다.
    protected virtual void LateUpdate()
    {
        if (!m_billboard)
            return;

        if (m_cameraTransform == null)
        {
            Camera camera = Camera.main;
            if (camera == null)
                return;

            m_cameraTransform = camera.transform;
        }

        transform.rotation = Quaternion.LookRotation(m_cameraTransform.forward, m_cameraTransform.up);
    }
}
