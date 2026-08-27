using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 납치 호송 중 캐리어 NPC의 손과 내 몸을 잇는 밧줄 표시 — 순수 로컬 연출. (#371/#901)
///
/// <see cref="RopeDragView"/>와 같은 방침(동기화 없이 이미 동기화된 양 끝점을 보고 각 피어가 스스로
/// 그린다)이지만 방향이 반대다 — 그쪽은 "내가 남을 끄는" 쪽이고, 이것은 "남이 나를 끄는" 쪽이다.
/// 그래서 <see cref="PlayerEscorter"/>가 아니라 <see cref="PlayerTowedMotion"/>의 호송 앵커를 읽는다.
///
/// 오검거 호송(#279)·UFO 흡입(#819)에는 그리지 않는다 — <see cref="PlayerTowedMotion.IsEscorted"/>가
/// 참이어도 앵커가 <see cref="NpcController"/>가 아니거나(UFO) 임무가 납치가 아니면(오검거) 숨긴다.
/// 이 판정을 캐리어 NPC의 <see cref="NpcDutyAgent.IsAbductionDuty"/>로 하므로, 물리(CC 충돌 여부,
/// #902)와 이 표시는 서로 다른 조건이다 — 맨홀 하강(#775) 구간은 CC를 다시 끄지만(물리는 직접 이동)
/// 임무는 여전히 납치이므로 밧줄은 하강 중에도 계속 보인다(끝까지 끌려가는 그림).
/// </summary>
[RequireComponent(typeof(PlayerTowedMotion))]
public class AbductionRopeView : MonoBehaviour
{
    [Header("밧줄 선")]
    [Tooltip("밧줄 선 머티리얼 — 비우면 표시를 끈다")]
    [SerializeField] private Material m_ropeMaterial;

    [Tooltip("밧줄 굵기(m)")]
    [SerializeField] private float m_ropeWidth = 0.035f;

    [Range(2, 32)]
    [Tooltip("선 분할 수 — 늘어짐 곡선의 부드러움. 2면 직선이다")]
    [SerializeField] private int m_segments = 12;

    [Tooltip("완전히 늘어졌을 때 가운데가 처지는 최대 깊이(m). 팽팽해질수록 0에 가까워진다")]
    [SerializeField] private float m_maxSag = 0.25f;

    [Tooltip("팽팽함 판정 기준 길이(m) — 이 거리에 가까울수록 늘어짐이 얕아진다")]
    [SerializeField] private float m_ropeLength = 1.4f;

    [Tooltip("손 뼈를 못 찾는 NPC의 대체 높이(m) — 루트(발밑) 기준")]
    [SerializeField] private float m_handHeight = 1.1f;

    [Tooltip("몸통 뼈를 못 찾을 때 내 몸의 대체 매듭 높이(m) — 루트(발밑) 기준")]
    [SerializeField] private float m_knotHeight = 0.5f;

    // 밧줄 하나분의 표시 — 선과 매듭(캐리어 손) 뼈 캐시. 슬롯 단위로 재사용한다.
    private class RopeVisual
    {
        public LineRenderer Line;
        public Transform HandSource; // 이 캐시가 가리키는 캐리어 — 바뀌면 뼈를 다시 잡는다
        public Transform HandAnchor; // 못 찾았으면 null로 캐시해 재검색을 막는다
    }

    private PlayerTowedMotion m_towed;
    private readonly List<RopeVisual> m_visuals = new();

    // 내 몸(플레이어) 쪽 매듭점 캐시 — self는 바뀌지 않으니 한 번만 찾으면 된다
    private bool m_selfKnotResolved;
    private Transform m_selfKnotAnchor;

    private void Awake()
    {
        m_towed = GetComponent<PlayerTowedMotion>();
    }

    // 앵커 위치는 Update에서 갱신되므로 이번 프레임의 최종 위치를 잇는다 (RopeDragView와 같은 이유)
    private void LateUpdate()
    {
        if (!m_towed.IsEscorted)
        {
            HideAll();
            return;
        }

        NpcController carrierA = ResolveAbductionCarrier(m_towed.EscortAnchorA);
        if (carrierA == null)
        {
            HideAll();
            return;
        }

        NpcController carrierB = ResolveAbductionCarrier(m_towed.EscortAnchorB);

        Vector3 knot = KnotPoint();

        int count = 0;
        DrawTo(count++, carrierA, knot);
        if (carrierB != null && carrierB != carrierA)
            DrawTo(count++, carrierB, knot);

        for (int i = count; i < m_visuals.Count; i++)
            HideVisual(i);
    }

    private void OnDisable() => HideAll();

    // 이 앵커가 납치 임무 중인 NPC인가 — 오검거 호송·UFO는 null을 돌려받아 표시가 숨는다.
    private static NpcController ResolveAbductionCarrier(Transform anchor)
    {
        if (anchor == null)
            return null;

        NpcController npc = anchor.GetComponent<NpcController>();
        return npc != null && npc.Penalty.IsAbductionDuty ? npc : null;
    }

    private void DrawTo(int index, NpcController carrier, Vector3 knot)
    {
        RopeVisual visual = EnsureVisual(index);
        if (visual == null)
            return; // 머티리얼이 없어 그릴 수 없다

        Vector3 hand = HandPoint(visual, carrier.transform);
        visual.Line.enabled = true;

        float distance = Vector3.Distance(hand, knot);
        float slack = 1f - Mathf.Clamp01(distance / Mathf.Max(0.01f, m_ropeLength));
        float sag = m_maxSag * slack;

        if (visual.Line.positionCount != m_segments + 1)
            visual.Line.positionCount = m_segments + 1;

        for (int i = 0; i <= m_segments; i++)
        {
            float t = (float)i / m_segments;
            Vector3 point = Vector3.Lerp(hand, knot, t);
            point.y -= sag * Mathf.Sin(t * Mathf.PI); // 양 끝 0, 가운데 최대로 처진다
            visual.Line.SetPosition(i, point);
        }
    }

    // 캐리어 NPC 쪽 매듭점 — 오른손 뼈, 없으면 대체 높이 (RopeDragView.KnotPoint와 같은 관례)
    private Vector3 HandPoint(RopeVisual visual, Transform carrier)
    {
        if (carrier != visual.HandSource)
        {
            visual.HandSource = carrier;
            Animator animator = carrier.GetComponentInChildren<Animator>();
            visual.HandAnchor = animator != null && animator.isHuman
                ? animator.GetBoneTransform(HumanBodyBones.RightHand)
                : null;
        }

        return visual.HandAnchor != null
            ? visual.HandAnchor.position
            : carrier.position + Vector3.up * m_handHeight;
    }

    // 내 몸 쪽 매듭점 — 가슴 뼈, 없으면 대체 높이. 대상이 나 자신이라 한 번만 찾으면 된다.
    private Vector3 KnotPoint()
    {
        if (!m_selfKnotResolved)
        {
            m_selfKnotResolved = true;
            Animator animator = GetComponentInChildren<Animator>();
            m_selfKnotAnchor = animator != null && animator.isHuman
                ? animator.GetBoneTransform(HumanBodyBones.Chest)
                : null;
        }

        return m_selfKnotAnchor != null
            ? m_selfKnotAnchor.position
            : transform.position + Vector3.up * m_knotHeight;
    }

    private void HideAll()
    {
        for (int i = 0; i < m_visuals.Count; i++)
            HideVisual(i);
    }

    private void HideVisual(int index)
    {
        if (index >= m_visuals.Count)
            return;

        if (m_visuals[index].Line != null)
            m_visuals[index].Line.enabled = false;
    }

    // 슬롯의 표시 인스턴스를 필요할 때 한 번만 만든다 — 끌려가지 않는 플레이어는 비용이 0이다.
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
            Debug.LogWarning(
                $"[AbductionRopeView] 밧줄 선 머티리얼이 없어 표시를 끈다. {name} 프리팹에 지정할 것", this);
            return null;
        }

        GameObject ropeObject = new GameObject("AbductionRopeLine");
        ropeObject.transform.SetParent(transform, false);

        var visual = new RopeVisual();
        visual.Line = ropeObject.AddComponent<LineRenderer>();
        visual.Line.useWorldSpace = true; // 양 끝이 서로 다른 오브젝트라 월드 좌표로 그린다
        visual.Line.sharedMaterial = m_ropeMaterial;
        visual.Line.widthMultiplier = m_ropeWidth;
        visual.Line.numCapVertices = 2;
        visual.Line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        visual.Line.receiveShadows = true;
        visual.Line.generateLightingData = true;
        visual.Line.enabled = false;

        return visual;
    }
}
