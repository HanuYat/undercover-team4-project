using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerInteractor : NetworkBehaviour
{
    [Header("레이캐스트")]
    [SerializeField]
    private Camera m_camera;

    [SerializeField]
    private float m_range = 3f;

    [SerializeField]
    private LayerMask m_interactMask = ~0;

    [Tooltip("시야를 가로막는 장애물 레이어 — 벽·건물(Default). 여기 걸리면 대상으로 잡지 않는다")]
    [SerializeField]
    private LayerMask m_losBlockMask = 1; // Default

    [Header("디버그")]
    [Tooltip(
        "조준·가시선 판정을 콘솔에 찍는다 — 히트 목록·거리·무엇이 차단했는지. Gizmos를 켜면 레이도 보인다"
    )]
    [SerializeField]
    private bool m_logLineOfSight;

    [Tooltip(
        "Gizmos로 그리는 레이가 화면에 남는 시간(초). 콘솔 로그는 상황이 바뀔 때 한 번만 찍힌다"
    )]
    [SerializeField]
    private float m_logInterval = 0.5f;

    // 가시선 검사를 끝점 직전에서 멈추는 여유(m) — 대상이 딛고 선 바닥이 가림으로 잡히는 것을 막는다.
    private const float k_losEndMargin = 0.05f;

    // 가시선 히트 버퍼 — UpdateTarget이 매 프레임 도는 경로라 RaycastAll(호출마다 배열 할당) 대신
    // NonAlloc + 고정 버퍼를 쓴다. 소유자 전용 컴포넌트라 static 공유로 충분하다.
    // 16칸은 래그돌 본까지 세면 군중 안에서 넘친다 — 넘치면 정렬 없이 잘려 가림이 조용히 빠진다 (#779)
    private static readonly RaycastHit[] s_losHits = new RaycastHit[64];

    public IInteractable CurrentInteractable { get; private set; }
    public GameObject CurrentTarget { get; private set; } // 아이템 타겟팅/UI용

    /// <summary>조준 대상이 바뀔 때 발행 — 조준 피드백(아웃라인·크로스헤어)용. null = 대상 없음. (#184)</summary>
    public event System.Action<GameObject> OnTargetChanged;

    /// <summary>상호작용 레이캐스트 사거리(m). 서버 줍기 거리 검증(#147)이 같은 값을 재사용한다.</summary>
    public float Range => m_range;

    /// <summary>레이캐스트 기준점(카메라 위치). 카메라 미배정 시 플레이어 루트로 대체.
    /// 서버 줍기 거리 검증(#147)이 클라이언트 조준과 동일한 기준점을 쓰기 위해 참조한다.</summary>
    public Transform AimOrigin => m_camera != null ? m_camera.transform : transform;

    /// <summary>조준 카메라. 조준 대상의 월드→스크린 좌표 변환(#233 스캔 정보 추종)에 쓴다. 미배정이면 null.</summary>
    public Camera AimCamera => m_camera;

    /// <summary>시야를 막는 장애물 레이어. 서버 드롭 위치 보정(#360)이 "벽"의 정의를 여기서 재사용한다 —
    /// 값을 따로 두면 조준은 막히는데 드롭은 통과하는 식으로 어긋난다.</summary>
    public LayerMask LosBlockMask => m_losBlockMask;

    private PlayerInputHandler m_inputHandler;
    private PlayerEscorter m_escorter; // "지금 이걸 끌고 있나" 조회 (연결 상태)
    private PlayerEscortCommands m_commands; // 놓기 요청 (명령 허브)
    private PlayerIncapacitation m_incapacitation;
    private PlayerCarrier m_carrier; // 운반 중 E의 "내려놓기" 선점 판정용 (#365)

    // 로그 중복 억제 상태 — 대상·차단자·결과가 그대로면 다시 찍지 않는다
    private int m_lastLogTargetId;
    private int m_lastLogBlockerId;
    private bool m_lastLogBlocked;
    private bool m_lastLogWasAimMiss;

    public override void OnNetworkSpawn()
    {
        m_inputHandler = GetComponent<PlayerInputHandler>();
        // 연행 중 E 입력의 "놓기" 선점 판정용 — 없는 구성(테스트 등)이면 null (#91)
        m_escorter = GetComponent<PlayerEscorter>();
        m_commands = GetComponent<PlayerEscortCommands>();
        // 행동불능 중 상호작용 차단용 — 이동/아이템은 각자 게이팅하지만 E 상호작용은 공백이었다 (#101)
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_carrier = GetComponent<PlayerCarrier>(); // 없는 구성(테스트 등)이면 null (#365)
        if (m_camera == null)
            m_camera = Camera.main;

        if (!IsOwner)
        {
            enabled = false;
            return;
        }

        m_inputHandler.OnInteractPerformed += HandleInteract;
        // E 뗌(OnInteractCanceled) 구독은 여기 없다 — 도주 제압 홀드 취소 전용이었고 그 홀드가
        // 사라졌다(#436). PlayerReviver의 구조도 이제 탭 방식이라 뗌을 보지 않는다(#725).
    }

    public override void OnNetworkDespawn()
    {
        if (!IsOwner)
            return;

        m_inputHandler.OnInteractPerformed -= HandleInteract;
    }

    private void Update()
    {
        UpdateTarget();
    }

    private void UpdateTarget()
    {
        if (m_camera == null)
            return;

        GameObject previousTarget = CurrentTarget;

        Ray ray = new Ray(m_camera.transform.position, m_camera.transform.forward);
        bool aimed = Physics.Raycast(ray, out RaycastHit hit, m_range, m_interactMask);
        if (aimed && HasLineOfSight(ray.origin, hit.point, hit.transform, "조준"))
        {
            CurrentTarget = hit.collider.gameObject;
            CurrentInteractable = hit.collider.GetComponentInParent<IInteractable>();
        }
        else
        {
            if (!aimed)
                LogAimMiss(ray);
            CurrentTarget = null;
            CurrentInteractable = null;
        }

        if (CurrentTarget != previousTarget)
            OnTargetChanged?.Invoke(CurrentTarget);
    }

    /// <summary>
    /// 서버 판정용 가시선 검사 — 조준 기준점(AimOrigin)에서 대상이 벽에 가리지 않았는가. (#360)
    /// 서버 사거리 검증(#147)만으로는 위조 RPC로 벽 너머 줍기·제압·구조·스캔이 뚫리므로,
    /// 각 서버 판정이 사거리와 함께 이걸 통과해야 한다. 클라 조준과 같은 기준점·마스크를 쓴다.
    /// </summary>
    public bool HasLineOfSightTo(Transform target)
    {
        // 끝점은 대상 콜라이더의 중심 — 루트 원점은 대개 발밑이라 바닥(Default)에 걸려 오차단된다.
        // 비재귀 조회라 판정 콜라이더가 루트에 있다는 전제 — 아이템·NPC·플레이어 모두 성립한다.
        Vector3 point = target.TryGetComponent(out Collider targetCollider)
            ? targetCollider.bounds.center
            : target.position;

        return HasLineOfSight(AimOrigin.position, point, target, "서버 판정");
    }

    /// <summary>
    /// 사거리 + 가시선을 함께 보는 서버 판정 — 거리만 보면 위조 RPC로 벽 너머 상호작용이 뚫린다 (#360).
    /// 스캔·구조·운반·검거가 이 하나를 통과하므로, 새 상호작용도 여기로 붙여야 규칙이 갈라지지 않는다.
    /// 기준점은 조준·윤곽선 게이트와 동일한 AimOrigin(카메라) — 루트(발밑) 기준이면 카메라 오프셋만큼
    /// 사거리 경계에서 판정이 어긋난다 (#147 관례, #184).
    /// interactor가 없는 구성(테스트 씬 등)은 fallbackOrigin을 기준점으로 쓰고 가시선은 생략한다.
    /// </summary>
    public static bool IsWithinReach(
        PlayerInteractor interactor,
        Transform target,
        float range,
        Vector3 fallbackOrigin
    )
    {
        Vector3 origin = interactor != null ? interactor.AimOrigin.position : fallbackOrigin;
        return (target.position - origin).sqrMagnitude <= range * range
            && (interactor == null || interactor.HasLineOfSightTo(target));
    }

    /// <summary>인터랙터의 사거리 — 없는 구성(테스트 등)이면 fallback. IsWithinReach와 짝으로 쓴다.</summary>
    public static float RangeOf(PlayerInteractor interactor, float fallback) =>
        interactor != null ? interactor.Range : fallback;

    // 조준 레이캐스트는 Interactable 레이어만 보므로 벽(Default)을 그냥 통과한다 — 대상 확정 후
    // 여기서 장애물만 따로 본다. (마스크에 벽을 넣으면 본부 트리거 존이 레이를 가로채고, 트리거를
    // 무시하자니 줍기 콜라이더가 트리거라(#263) 줍기가 통째로 죽는다.)
    // 대상 자신의 콜라이더는 가림으로 치지 않는다 — 폭탄은 몸통(Default)이 배선(Interactable)을 감싼다.
    //
    // 히트를 하나만 보지 않고 전부 받아 AimOcclusion에 판정을 맡긴다 — 테이저 사격·진압봉 스윙과
    // 같은 규칙이어야 "테이저는 맞는데 E는 안 되는" 어긋남이 생기지 않는다.
    // 기준(피봇 거리)과 그 한계는 AimOcclusion 문서 주석에 정리돼 있다.
    private bool HasLineOfSight(
        Vector3 origin,
        Vector3 point,
        Transform target,
        string context = null
    )
    {
        Vector3 toPoint = point - origin;
        float fullDistance = toPoint.magnitude;
        float distance = fullDistance - k_losEndMargin;
        if (distance <= 0f)
        {
            return true; // 코앞 — 가릴 것이 들어갈 틈이 없다
        }

        int count = Physics.RaycastNonAlloc(
            origin,
            toPoint / fullDistance,
            s_losHits,
            distance,
            m_losBlockMask,
            QueryTriggerInteraction.Ignore
        );

        // 대상 자신의 콜라이더는 가림이 아니다 — 폭탄은 몸통(Default)이 배선(Interactable)을 감싼다.
        int blockerIndex = AimOcclusion.FindNearestByPivot(origin, s_losHits, count, target.root);

        // 가장 가까운 피봇조차 대상보다 멀면 가림은 없다 (그보다 먼 것들은 볼 필요가 없다)
        if (
            blockerIndex >= 0
            && AimOcclusion.PivotDistance(origin, s_losHits[blockerIndex])
                >= Vector3.Distance(origin, target.position)
        )
        {
            blockerIndex = -1;
        }

        LogLineOfSight(context, origin, point, target, count, blockerIndex, distance);
        return blockerIndex < 0;
    }

    // ---- 디버그 로그 (m_logLineOfSight) ----

    private void LogLineOfSight(
        string context,
        Vector3 origin,
        Vector3 point,
        Transform target,
        int count,
        int blockerIndex,
        float distance
    )
    {
        if (!m_logLineOfSight)
            return;

        bool blocked = blockerIndex >= 0;

        // 레이 시각화 — Gizmos를 켜면 Game/Scene 뷰에 보인다. 로그 간격과 무관하게 매 호출 그린다.
        Debug.DrawLine(
            origin,
            blocked ? s_losHits[blockerIndex].point : point,
            blocked ? Color.red : Color.green,
            Mathf.Max(0.05f, m_logInterval)
        );

        // 상황이 바뀔 때만 한 줄 — 같은 대상·같은 결과가 이어지는 동안은 다시 찍지 않는다.
        // (매 프레임 도는 경로라 시간 스로틀만 두면 같은 줄이 계속 쌓여 읽을 수 없다)
        int targetId = target.GetInstanceID();
        int blockerId = blocked ? s_losHits[blockerIndex].collider.GetInstanceID() : 0;
        if (
            !m_lastLogWasAimMiss
            && targetId == m_lastLogTargetId
            && blockerId == m_lastLogBlockerId
            && blocked == m_lastLogBlocked
        )
        {
            return;
        }

        m_lastLogTargetId = targetId;
        m_lastLogBlockerId = blockerId;
        m_lastLogBlocked = blocked;
        m_lastLogWasAimMiss = false;

        var sb = new System.Text.StringBuilder();
        sb.Append($"[LOS/{context ?? "?"}] {(blocked ? "차단" : "통과")} — 대상 {target.name}");
        sb.Append($", 사거리 검사 {distance:F2}m, 히트 {count}개");
        sb.Append($" (server={IsServer} owner={IsOwner}, blockMask={m_losBlockMask.value})");
        sb.Append(
            $"\n  대상 Transform까지 {Vector3.Distance(origin, target.position):F2}m — 이보다 가까운 피봇만 가림으로 센다"
        );

        if (count == 0)
        {
            sb.Append("\n  히트 없음 — 이 경로는 가시선 때문에 실패한 것이 아니다");
        }

        for (int i = 0; i < count; i++)
        {
            Collider c = s_losHits[i].collider;
            string mark =
                i == blockerIndex ? "  ← 여기서 차단"
                : c != null && c.transform.root == target.root ? "  (대상 자신)"
                : "";
            sb.Append($"\n  [{i}] {s_losHits[i].distance:F2}m  {(c == null ? "(null)" : c.name)}");
            if (c != null)
            {
                sb.Append(
                    $"  (Transform까지 {Vector3.Distance(origin, c.transform.position):F2}m)"
                );
                sb.Append(
                    $"  root={c.transform.root.name}, layer={LayerMask.LayerToName(c.gameObject.layer)}"
                );
            }

            sb.Append(mark);
        }

        if (blocked)
            Debug.LogWarning(sb.ToString(), s_losHits[blockerIndex].collider);
        else
            Debug.Log(sb.ToString(), target);
    }

    // 조준 레이가 아무것도 못 맞춘 경우 — 가시선 이전 단계에서 실패한 것이라 위 로그가 아예 안 찍힌다.
    // 마스크를 무시하고 한 번 더 쏴서, 대상이 m_interactMask 밖 레이어에 있는 것인지 구분한다.
    private void LogAimMiss(Ray ray)
    {
        if (!m_logLineOfSight)
            return;

        Debug.DrawRay(
            ray.origin,
            ray.direction * m_range,
            Color.yellow,
            Mathf.Max(0.05f, m_logInterval)
        );

        // 대상 없음도 상태가 바뀔 때 한 번만 — 아무것도 안 보고 있는 동안 계속 찍히면 의미가 없다
        if (m_lastLogWasAimMiss)
            return;
        m_lastLogWasAimMiss = true;
        m_lastLogTargetId = 0;
        m_lastLogBlockerId = 0;

        string extra = Physics.Raycast(
            ray,
            out RaycastHit any,
            m_range,
            ~0,
            QueryTriggerInteraction.Collide
        )
            ? $"마스크를 무시하면 {any.collider.name} (layer={LayerMask.LayerToName(any.collider.gameObject.layer)}, "
                + $"{any.distance:F2}m, trigger={any.collider.isTrigger})를 맞는다 — 대상 레이어가 interactMask 밖일 수 있다"
            : $"마스크를 무시해도 아무것도 없다 — 사거리({m_range}m) 밖이거나 콜라이더가 없다";

        Debug.Log(
            $"[LOS/조준] 대상 없음 — interactMask={m_interactMask.value}, range={m_range}m. {extra}"
        );
    }

    private void HandleInteract()
    {
        // 커서가 풀려 있으면(인벤토리 편집 등 UI 조작 중) 월드 상호작용은 막는다 — 아이템 사용과 동일 (#352)
        if (CursorLock.IsUnlocked)
            return;

        // 행동불능(HP 다운·오검거 매달기) 중엔 상호작용 불가 — 이동·아이템과 동일하게 게이트한다 (#101/#105)
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
            return;

        // 겨냥한 상호작용이 지금 실제로 동작하는가 — 아래 '손 떼기'보다 앞서는 조건이다 (#638).
        // 윤곽선 판정(CanInteract)을 그대로 쓴다: 배회하는 시민처럼 E가 아무 일도 하지 않는 대상을
        // 겨눴다는 이유로 끌던 줄이 안 풀리면, 무엇을 보고 있느냐에 따라 E가 죽는 구간이 생긴다.
        bool aimedInteraction =
            CurrentInteractable != null && CurrentInteractable.CanInteract(gameObject);

        // 끌고 있는 대상을 겨눈 E는 **그 한 명만** 푼다 (#390/#513) — 여러 명을 끌 때 하나만 놓는 수단이다.
        // 겨냥이 비면 아래에서 전원 풀기로 간다 (#638): 끌리는 몸은 늘 등 뒤에 있어서, 겨냥을 요구하면
        // 놓을 때마다 뒤를 돌아봐야 했다.
        // (연행 쪽이 따로 입력을 구독하면 풀기와 다른 E 동작이 한 입력에 동시 발동하는 이중 소비가 생긴다)
        NpcController aimed =
            CurrentTarget != null ? CurrentTarget.GetComponentInParent<NpcController>() : null;
        if (m_escorter != null && m_escorter.IsDraggingNpc(aimed))
        {
            // '끌고 온 상태에서만 의미 있는' 대상에 선점을 열어 주던 예외(TakesPriorityOverRelease, #414)는
            // 유일한 사용처인 인계 단말과 함께 제거됐다 (#492) — 필요해지면 아래 운반 쪽
            // ICarriedBodyReceiver가 같은 취지의 선례다.
            // ReleaseDrag 직접 호출은 서버 가드에 막힌다 — 요청 API로 서버에 넘긴다 (#118)
            Debug.Log($"E 입력 — 밧줄 풀기 요청(겨냥): {aimed.name}");
            m_commands?.RequestUnrope(aimed);
            return;
        }

        if (m_carrier != null && m_carrier.IsCarrying)
        {
            // 몸을 받는 대상(부활 장치)은 내려놓기보다 앞선다 — 아니면 장치 앞에서 E를 눌러도 그 자리에
            // 툭 내려놓게 된다. 선점을 여는 대상은 ICarriedBodyReceiver로 한정한다(문·콘솔은 종전대로).
            // CanInteract를 함께 보므로 조준 윤곽선이 켜진 조건과 실제로 E가 먹히는 조건이 일치한다.
            if (
                CurrentInteractable is ICarriedBodyReceiver receiver
                && receiver.CanInteract(gameObject)
            )
            {
                receiver.Interact(gameObject);
                return;
            }

            // 업은 동료를 겨눈 E는 그 몸만 내려놓는다 — 겨냥으로 하나를 고르는 위 밧줄 갈래와 같은 취지.
            Transform carried = m_carrier.CarriedTransform;
            if (
                carried != null
                && CurrentTarget != null
                && CurrentTarget.transform.IsChildOf(carried)
            )
            {
                Debug.Log("E 입력 — 내려놓기 요청 (운반, 겨냥)");
                m_carrier.RequestDrop();
                return;
            }
        }

        // ---- 손 떼기 — 겨냥 없는 E (#638) ----
        // 지금 손에 쥔 것을 전부 놓는다: 끌던 대상의 밧줄과 업은 동료. 둘은 밧줄 칸을 나눠 쓰는
        // 같은 조작이라(#365/#390) 한 입력으로 함께 놓는다.
        // 겨냥한 상호작용이 있으면 그쪽이 이긴다 — 끌고 가며 문·콘솔·유치장을 다루는 동선을 지킨다.
        if (!aimedInteraction)
        {
            bool released = false;

            if (m_escorter != null && m_escorter.IsDraggingAny)
            {
                Debug.Log("E 입력 — 끌던 대상 전원 밧줄 풀기 요청");
                m_commands?.RequestUnropeAll();
                released = true;
            }

            if (m_carrier != null && m_carrier.IsCarrying)
            {
                Debug.Log("E 입력 — 내려놓기 요청 (운반)");
                m_carrier.RequestDrop();
                released = true;
            }

            // 놓을 것이 없으면 평소 상호작용으로 흘려보낸다 — CanInteract가 false여도 Interact를
            // 불러 주던 종전 동작(잠긴 문의 거부 피드백 등)이 그대로 살아 있어야 한다.
            if (released)
                return;
        }

        CurrentInteractable?.Interact(gameObject);
    }
}
