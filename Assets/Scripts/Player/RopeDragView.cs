using UnityEngine;

/// <summary>
/// 밧줄 끌기 표현 — 끄는 플레이어의 손과 끌리는 NPC를 잇는 밧줄 선, 바닥에 쓸리는 먼지. (#269 표현 계층)
///
/// <b>순수 로컬 연출이다</b>(<see cref="BombExplosionVfx"/>와 같은 방침). 네트워크로 오가는 건
/// "누가 누구를 끄는가" 하나뿐이고(<see cref="PlayerEscorter.DraggedNpcTransform"/>), 각 피어가 그 두 끝점을
/// 보고 자기 화면에 선을 그린다 — 선 자체를 스폰하거나 동기화하지 않는다.
///
/// 전 피어에서 돈다(오너 전용이 아니다) — 남이 끌고 가는 모습도 밧줄이 보여야 한다.
/// 선의 시작점은 3인칭 손 앵커(<see cref="PlayerHeldItemView.HandAnchor"/>)라 손에 든 밧줄 코일에서
/// 자연스럽게 이어진다. 앵커가 없는 구성(테스트 등)이면 몸통 높이로 대체한다.
/// </summary>
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

    [Tooltip("NPC 쪽 매듭 높이(m) — 누운 몸 위쪽에 걸리게 살짝 띄운다")]
    [SerializeField] private float m_npcKnotHeight = 0.25f;

    [Header("먼지")]
    [Tooltip("끌리는 몸 아래에 따라다니는 먼지 파티클 프리팹 — 비우면 먼지 없이 선만 그린다")]
    [SerializeField] private GameObject m_dustPrefab;

    private PlayerEscorter m_escorter;
    private PlayerHeldItemView m_heldItemView;

    // 표시용 인스턴스 — 첫 끌기에 만들고 이후 껐다 켠다(끌 때마다 생성/파괴하지 않는다).
    private LineRenderer m_rope;
    private GameObject m_dust;

    private void Awake()
    {
        m_escorter = GetComponent<PlayerEscorter>();
        m_heldItemView = GetComponent<PlayerHeldItemView>(); // 없는 구성(테스트 등)이면 null
    }

    // 끌기 위치는 서버가 Update에서 갱신하고 플레이어도 Update에서 움직인다 —
    // 선을 LateUpdate에서 그려야 이번 프레임의 최종 위치를 잇는다(한 프레임 늦게 따라붙지 않는다).
    private void LateUpdate()
    {
        Transform dragged = m_escorter.DraggedNpcTransform;
        if (dragged == null)
        {
            SetVisible(false);
            return;
        }

        SetVisible(true);
        DrawRope(HandPoint, dragged.position + Vector3.up * m_npcKnotHeight);

        if (m_dust != null)
            m_dust.transform.position = dragged.position;
    }

    private void OnDisable() => SetVisible(false);

    private Vector3 HandPoint
    {
        get
        {
            Transform anchor = m_heldItemView != null ? m_heldItemView.HandAnchor : null;
            return anchor != null ? anchor.position : transform.position + Vector3.up * m_fallbackHandHeight;
        }
    }

    // 두 끝점을 잇되 가운데를 아래로 늘어뜨린다. 늘어짐은 밧줄이 팽팽할수록(길이에 가까울수록) 얕아진다 —
    // 멈춰 있으면 축 처지고, 끌기 시작하면 팽팽해지는 변화가 "당기고 있다"를 보여준다.
    private void DrawRope(Vector3 handPoint, Vector3 knotPoint)
    {
        if (m_rope == null)
            return;

        float distance = Vector3.Distance(handPoint, knotPoint);
        float slack = 1f - Mathf.Clamp01(distance / Mathf.Max(0.01f, m_escorter.RopeLength));
        float sag = m_maxSag * slack;

        if (m_rope.positionCount != m_segments + 1)
            m_rope.positionCount = m_segments + 1;

        for (int i = 0; i <= m_segments; i++)
        {
            float t = (float)i / m_segments;
            Vector3 point = Vector3.Lerp(handPoint, knotPoint, t);
            point.y -= sag * Mathf.Sin(t * Mathf.PI); // 양 끝 0, 가운데 최대로 처진다
            m_rope.SetPosition(i, point);
        }
    }

    private void SetVisible(bool visible)
    {
        if (visible && m_rope == null)
            Build();

        if (m_rope != null)
            m_rope.enabled = visible;

        if (m_dust != null && m_dust.activeSelf != visible)
            m_dust.SetActive(visible);
    }

    // 선·먼지 인스턴스를 첫 끌기 때 한 번만 만든다 — 끌지 않는 플레이어는 비용이 0이다.
    private void Build()
    {
        if (m_ropeMaterial == null)
        {
            enabled = false; // 머티리얼 없이는 그릴 수 없다 — 매 프레임 헛돌지 않게 스스로 꺼진다
            Debug.LogWarning($"[RopeDragView] 밧줄 선 머티리얼이 없어 표시를 끈다. {name} 프리팹에 지정할 것", this);
            return;
        }

        GameObject ropeObject = new GameObject("RopeLine");
        ropeObject.transform.SetParent(transform, false);

        m_rope = ropeObject.AddComponent<LineRenderer>();
        m_rope.useWorldSpace = true; // 양 끝이 서로 다른 오브젝트라 월드 좌표로 그린다
        m_rope.sharedMaterial = m_ropeMaterial;
        m_rope.widthMultiplier = m_ropeWidth;
        m_rope.numCapVertices = 2;
        m_rope.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        m_rope.receiveShadows = false;
        m_rope.enabled = false;

        if (m_dustPrefab != null)
        {
            m_dust = Instantiate(m_dustPrefab); // 월드에 독립 — 부모를 따라 회전하면 먼지가 같이 돌아버린다
            m_dust.SetActive(false);
        }
    }

    private void OnDestroy()
    {
        if (m_dust != null)
            Destroy(m_dust);
    }
}
