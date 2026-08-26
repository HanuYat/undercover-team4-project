using System.Collections.Generic;
using UnityEngine;

// 밧줄 표현 — 손과 묶인 NPC들을 잇는 선 + 바닥 먼지. 순수 로컬 연출이라 동기화 없이 각 피어가 동기화된
// 양 끝점(PlayerEscorter.GetTetheredNpc)을 보고 스스로 그린다(#269). 시작점은 3인칭 손 앵커
// (PlayerHeldItemView.HandAnchor)가 기본이고, 오너 1인칭 화면만 예외로 FP 팔의 손을 쓴다
// (PlayerHandView.TryGetHandWorldPoint) — 안 그러면 오너 화면에서 컬링된 3인칭 손에서 줄이 나온다(#828 증상).
// 이유·계산 근거는 docs/828-rope-first-person.md. 밧줄 1개당 NPC 1명, 표시는 슬롯 단위로 풀링한다(#390).
// 기능 정지(Die) 동료 운반(#365)도 같은 밧줄이라 NPC 줄 뒤에 슬롯 하나를 더 쓴다.
[RequireComponent(typeof(PlayerEscorter))]
public class RopeDragView : MonoBehaviour
{
    [Header("밧줄 선")]
    [Tooltip("밧줄 선 머티리얼 — 비우면 선을 그리지 않는다")]
    [SerializeField] private Material m_ropeMaterial;

    [Tooltip("밧줄 굵기(m)")]
    [SerializeField] private float m_ropeWidth = 0.035f;

    [Tooltip("선 분할 수 — 늘어짐 곡선의 부드러움. 2면 직선이다")]
    [Range(2, 32)]
    [SerializeField] private int m_segments = 12;

    [Tooltip("완전히 늘어졌을 때 가운데가 처지는 최대 깊이(m). 팽팽해질수록 0에 가까워진다")]
    [SerializeField] private float m_maxSag = 0.3f;

    [Tooltip("손 앵커가 없을 때 쓰는 대체 시작 높이(m) — 플레이어 발밑 기준")]
    [SerializeField] private float m_fallbackHandHeight = 1.1f;

    [Tooltip("몸통 뼈를 못 찾는 NPC의 대체 매듭 높이(m) — 루트(발밑) 기준")]
    [SerializeField] private float m_npcKnotHeight = 0.25f;

    [Header("1인칭 보정 (#828)")]
    [Tooltip("오너 1인칭에서 시작점을 놓을 카메라 앞 거리(m) — 1인칭 팔 카메라와 월드 카메라의 FOV가 달라 " +
        "화면 위치를 맞추려면 깊이를 새로 정해야 한다. 0 이하면 손 자체 깊이를 쓴다")]
    [SerializeField] private float m_fpStartDepth = 0.6f;

    [Tooltip("오너 1인칭 시작점의 굵기(m) — 손이 카메라에서 ~0.5m라 정상 굵기(m_ropeWidth)를 그대로 두면 " +
        "화면에서 두꺼운 띠로 잡힌다. m_ropeWidth 지점까지 짧게 테이퍼링한다")]
    [SerializeField] private float m_fpStartWidth = 0.012f;

    [Tooltip("오너 1인칭 시작점의 화면 가로 보정(뷰포트 비율, 화면 폭 기준) — 음수면 왼쪽, 양수면 " +
        "오른쪽으로 밀린다. 화면 위치 자체를 옮기는 값이라 깊이(m_fpStartDepth)와 달리 손에서 " +
        "벗어난다 — 밧줄이 손을 가리거나 시야 가장자리에서 어색할 때만 미세하게 쓸 것")]
    [SerializeField] private float m_fpStartOffsetX = 0f;

    [Header("먼지")]
    [Tooltip("끌리는 몸 아래에 따라다니는 먼지 파티클 프리팹 — 비우면 먼지 없이 선만 그린다")]
    [SerializeField] private GameObject m_dustPrefab;

    // 밧줄 하나분의 표시 — 선·먼지와 매듭 뼈 캐시. 슬롯 단위로 재사용한다(끌 때마다 생성/파괴하지 않는다).
    private class RopeVisual
    {
        public LineRenderer Line;
        public GameObject Dust;

        // 이 캐시가 가리키는 대상 — 슬롯에 다른 NPC가 들어오면 뼈를 다시 잡는다.
        // 뼈를 못 찾은 NPC는 Anchor를 null로 캐시해 재검색을 막는다.
        public Transform KnotSource;
        public Transform KnotAnchor;
    }

    private PlayerEscorter m_escorter;
    private PlayerCarrier m_carrier; // 기능 정지 동료 운반 — 같은 밧줄이라 같은 선을 그린다 (#365)
    private PlayerHeldItemView m_heldItemView;
    private PlayerHandView m_handView; // 오너 1인칭 손 — 있고 타진에 성공하면 3인칭 앵커보다 우선한다 (#828)

    private readonly List<RopeVisual> m_visuals = new List<RopeVisual>();

    // 1인칭 시작점 굵기 커브 — m_fpStartWidth/m_ropeWidth 비율이 인스펙터에서 바뀌면 다시 짓는다
    // (플레이 중 튜닝을 반영하려는 것; 그 외에는 프레임마다 새로 만들지 않는다).
    private static readonly AnimationCurve s_flatWidthCurve = AnimationCurve.Linear(0f, 1f, 1f, 1f);
    private const float k_fpTaperFraction = 0.25f; // 시작점에서 정상 굵기로 돌아오는 구간(선 길이 비율)
    private AnimationCurve m_fpWidthCurve;
    private float m_fpWidthCurveRatio = -1f;

    private void Awake()
    {
        m_escorter = GetComponent<PlayerEscorter>();
        m_carrier = GetComponent<PlayerCarrier>();
        m_heldItemView = GetComponent<PlayerHeldItemView>(); // 없는 구성(테스트 등)이면 null
        m_handView = GetComponent<PlayerHandView>(); // 없는 구성(테스트 등)이면 null — 3인칭 경로로만 그린다
    }

    // 끌기 위치는 서버가 Update에서 갱신하고 플레이어도 Update에서 움직인다 —
    // 선을 LateUpdate에서 그려야 이번 프레임의 최종 위치를 잇는다(한 프레임 늦게 따라붙지 않는다).
    private void LateUpdate()
    {
        // 끌고 있는 동안만이 아니라 '묶여 있는 동안' 내내 그린다 — 놓기(E)는 끌기를 멈출 뿐
        // 줄을 푸는 게 아니다. 실제로 풀리면(밧줄 좌클릭 풀기·인계 판정·방치 탈주) 연결이 끊긴다. (#369)
        int count = m_escorter.TetheredCount;
        bool isFirstPerson = TryGetHandPoint(out Vector3 handPoint);

        for (int i = 0; i < count; i++)
        {
            NpcController npc = m_escorter.GetTetheredNpc(i);

            // 아직 참조가 안 풀리는 대상(스폰 전·파괴 직후)은 이번 프레임만 건너뛴다
            if (npc == null)
            {
                HideVisual(i);
                continue;
            }

            RopeVisual visual = EnsureVisual(i);
            if (visual == null)
                return; // 머티리얼이 없어 그릴 수 없다 — Build가 컴포넌트를 스스로 껐다

            visual.Line.enabled = true;
            DrawRope(visual, handPoint, KnotPoint(visual, npc.transform), npc.Rope.RopeLength, isFirstPerson);

            // 먼지는 실제로 끌고 있을 때만 — 세워 둔 대상 발밑에서 먼지가 계속 일면 안 된다
            if (visual.Dust != null)
            {
                if (visual.Dust.activeSelf != npc.Rope.IsRoped)
                    visual.Dust.SetActive(npc.Rope.IsRoped);
                visual.Dust.transform.position = npc.transform.position;
            }
        }

        // 기능 정지 동료 운반(#365)도 같은 밧줄이라 같은 선을 그린다 — NPC 줄 뒤에 슬롯 한 칸을 더 쓴다.
        // 운반에는 '묶어만 둔' 상태가 없어(내려놓으면 줄이 풀린다) 끌고 있는 동안만 이어지고,
        // 그래서 먼지도 조건 없이 켠다(NPC 쪽 IsRoped 분기에 해당하는 상태가 없다).
        Transform carried = m_carrier != null ? m_carrier.CarriedTransform : null;
        if (carried != null)
        {
            RopeVisual visual = EnsureVisual(count);
            if (visual == null)
                return; // 머티리얼이 없어 그릴 수 없다 — Build가 컴포넌트를 스스로 껐다

            visual.Line.enabled = true;
            DrawRope(visual, handPoint, KnotPoint(visual, carried), CarriedRopeLength(carried), isFirstPerson);

            if (visual.Dust != null)
            {
                if (!visual.Dust.activeSelf)
                    visual.Dust.SetActive(true);
                visual.Dust.transform.position = carried.position;
            }

            count++; // 아래 정리 루프가 이 슬롯을 끄지 않게 한다
        }

        // 줄이 줄어들면 남는 슬롯은 꺼 둔다 — 파괴하지 않고 다음 끌기에 재사용한다
        for (int i = count; i < m_visuals.Count; i++)
            HideVisual(i);
    }

    private void OnDisable()
    {
        for (int i = 0; i < m_visuals.Count; i++)
            HideVisual(i);
    }

    // 운반 대상의 밧줄 길이 — 늘어짐(sag) 계산의 기준. NPC는 NpcRopeDrag.RopeLength가, 플레이어는
    // PlayerTowedMotion.RopeLength가 답한다 — 래그돌이냐에 따라 줄이 갈리는 사정은 그쪽이 안다.
    // 값이 아니라 컴포넌트를 캐시한다 — 운반 도중 래그돌로 갈릴 수 있어 길이는 매 프레임 물어야 하고,
    // 피하려는 것은 GetComponent다 (KnotSource 캐시와 같은 관례).
    private Transform m_carriedLengthSource;
    private PlayerTowedMotion m_carriedTowed;

    private const float k_fallbackCarriedRopeLength = 1.6f; // PlayerTowedMotion이 없는 구성(테스트 등)

    private float CarriedRopeLength(Transform carried)
    {
        if (carried != m_carriedLengthSource)
        {
            m_carriedLengthSource = carried;
            m_carriedTowed = carried.GetComponent<PlayerTowedMotion>();
        }

        return m_carriedTowed != null ? m_carriedTowed.RopeLength : k_fallbackCarriedRopeLength;
    }

    // NPC 쪽 매듭점 — 몸통 뼈가 있으면 그 위치(눕든 서든 몸을 따라간다), 없으면 루트+대체 높이. (#369)
    private Vector3 KnotPoint(RopeVisual visual, Transform tethered)
    {
        if (tethered != visual.KnotSource)
        {
            visual.KnotSource = tethered;
            Animator animator = tethered.GetComponentInChildren<Animator>();
            visual.KnotAnchor = animator != null && animator.isHuman
                ? animator.GetBoneTransform(HumanBodyBones.Chest)
                : null;
        }

        return visual.KnotAnchor != null
            ? visual.KnotAnchor.position
            : tethered.position + Vector3.up * m_npcKnotHeight;
    }

    // 시작점을 낸다 — 오너 1인칭 손을 먼저 타진하고, 실패하면(비오너·FP 팔 없음·감정표현·관전 등
    // 3인칭 상황) 3인칭 손 앵커로, 그마저 없으면 몸통 높이로 대체한다. 반환값은 어느 경로를
    // 탔는지 — DrawRope가 1인칭 시작점만 가늘게 테이퍼링하는 데 쓴다. (#828)
    private bool TryGetHandPoint(out Vector3 point)
    {
        if (m_handView != null && m_handView.TryGetHandWorldPoint(m_fpStartDepth, out point, m_fpStartOffsetX))
            return true;

        Transform anchor = m_heldItemView != null ? m_heldItemView.HandAnchor : null;
        point = anchor != null ? anchor.position : transform.position + Vector3.up * m_fallbackHandHeight;
        return false;
    }

    // m_fpStartWidth/m_ropeWidth 비율로 시작점 테이퍼 커브를 짓는다 — 인스펙터에서 값을 바꾸면
    // (플레이 중 튜닝 포함) 다시 짓고, 그 외에는 캐시를 그대로 쓴다.
    private AnimationCurve GetFpWidthCurve()
    {
        float ratio = m_ropeWidth > 0.0001f ? Mathf.Clamp01(m_fpStartWidth / m_ropeWidth) : 0f;
        if (m_fpWidthCurve == null || !Mathf.Approximately(m_fpWidthCurveRatio, ratio))
        {
            m_fpWidthCurveRatio = ratio;
            m_fpWidthCurve = new AnimationCurve(
                new Keyframe(0f, ratio),
                new Keyframe(k_fpTaperFraction, 1f)
            );
        }
        return m_fpWidthCurve;
    }

    // 두 끝점을 잇되 가운데를 아래로 늘어뜨린다. 늘어짐은 밧줄이 팽팽할수록(길이에 가까울수록) 얕아진다 —
    // 멈춰 있으면 축 처지고, 끌기 시작하면 팽팽해지는 변화가 "당기고 있다"를 보여준다.
    private void DrawRope(RopeVisual visual, Vector3 handPoint, Vector3 knotPoint, float ropeLength, bool taperStart)
    {
        LineRenderer line = visual.Line;
        float distance = Vector3.Distance(handPoint, knotPoint);
        float slack = 1f - Mathf.Clamp01(distance / Mathf.Max(0.01f, ropeLength));
        float sag = m_maxSag * slack;

        if (line.positionCount != m_segments + 1)
            line.positionCount = m_segments + 1;

        // 1인칭 시작점은 카메라에서 ~0.5m라 정상 굵기를 그대로 두면 화면에서 두꺼운 띠로 잡힌다 —
        // 시작 쪽만 가늘게 테이퍼링한다. 3인칭 시작점(다른 피어 화면)은 항상 평평한 굵기다. (#828)
        line.widthCurve = taperStart ? GetFpWidthCurve() : s_flatWidthCurve;

        for (int i = 0; i <= m_segments; i++)
        {
            float t = (float)i / m_segments;
            Vector3 point = Vector3.Lerp(handPoint, knotPoint, t);
            point.y -= sag * Mathf.Sin(t * Mathf.PI); // 양 끝 0, 가운데 최대로 처진다
            line.SetPosition(i, point);
        }
    }

    private void HideVisual(int index)
    {
        if (index >= m_visuals.Count)
            return;

        RopeVisual visual = m_visuals[index];
        if (visual.Line != null)
            visual.Line.enabled = false;
        if (visual.Dust != null && visual.Dust.activeSelf)
            visual.Dust.SetActive(false);
    }

    // 슬롯의 표시 인스턴스를 필요할 때 한 번만 만든다 — 끌지 않는 플레이어는 비용이 0이다.
    private RopeVisual EnsureVisual(int index)
    {
        while (m_visuals.Count <= index)
        {
            RopeVisual built = Build();
            if (built == null)
                return null;
            m_visuals.Add(built);
        }

        return m_visuals[index];
    }

    private RopeVisual Build()
    {
        if (m_ropeMaterial == null)
        {
            enabled = false; // 머티리얼 없이는 그릴 수 없다 — 매 프레임 헛돌지 않게 스스로 꺼진다
            Debug.LogWarning($"[RopeDragView] 밧줄 선 머티리얼이 없어 표시를 끈다. {name} 프리팹에 지정할 것", this);
            return null;
        }

        GameObject ropeObject = new GameObject("RopeLine");
        ropeObject.transform.SetParent(transform, false);

        var visual = new RopeVisual();
        visual.Line = ropeObject.AddComponent<LineRenderer>();
        visual.Line.useWorldSpace = true; // 양 끝이 서로 다른 오브젝트라 월드 좌표로 그린다
        visual.Line.sharedMaterial = m_ropeMaterial;
        visual.Line.widthMultiplier = m_ropeWidth;
        visual.Line.numCapVertices = 2;
        visual.Line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; // 밧줄 자체가 그림자를 드리우진 않는다 — 여러 명이 동시에 끌면 그림자만 지저분해진다
        visual.Line.receiveShadows = true; // 대신 주변 빛은 받는다 — 밝은 곳/그늘의 명암 차이가 드러난다 (#828)
        visual.Line.generateLightingData = true; // 리본 지오메트리에 법선을 만들어 Lit 셰이더가 실제로 음영을 계산하게 한다. 없으면 Unlit처럼 평평하게 보인다
        visual.Line.enabled = false;

        if (m_dustPrefab != null)
        {
            visual.Dust = Instantiate(m_dustPrefab); // 월드에 독립 — 부모를 따라 회전하면 먼지가 같이 돌아버린다
            visual.Dust.SetActive(false);
        }

        return visual;
    }

    private void OnDestroy()
    {
        foreach (RopeVisual visual in m_visuals)
            if (visual.Dust != null)
                Destroy(visual.Dust);
    }
}
