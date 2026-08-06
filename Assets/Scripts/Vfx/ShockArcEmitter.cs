using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 감전 아크 방출기 — 켜져 있는 동안 몸 여기저기에서 전기 아크를 간헐적으로 튀긴다. (#477)
/// NPC(<see cref="NpcShockView"/>)와 플레이어(<see cref="PlayerHitView"/>)가 <b>같은 부품을 공유한다</b> —
/// 테이저에 맞은 몸은 누구든 같은 그림이어야 하고, 튜닝 값이 두 벌로 갈리면 조용히 어긋난다.
///
/// <b>순수 로컬 연출이다</b> — 아크는 네트워크에 싣지 않는다(프리팹 등록 불필요). 켜고 끄는 판단을
/// 각 피어가 동기화 값으로 내리므로 모든 화면에 같은 시점에 나타난다(본부 CCTV 포함).
///
/// 아크를 <b>이 오브젝트의 자식으로</b> 낳는 이유: 밧줄에 끌려가는 동안에도 몸에 붙어 있어야 한다.
/// </summary>
public class ShockArcEmitter : MonoBehaviour
{
    [Tooltip("전기 아크 파티클 프리팹. 비우면 아무것도 나오지 않는다(연출은 선택 사항)")]
    [SerializeField] private GameObject m_arcPrefab;

    // 트레일 방식이라 '파티클이 죽은 뒤'에도 줄기가 남는다(FX_TaserArc는 trail lifetime 0.16s).
    // 파티클 수명만 보고 잡으면 꼬리가 잘려 번개가 툭 끊긴다 — 방출 duration + 파티클 수명 + 트레일 수명.
    [Tooltip("아크 하나가 살아 있는 시간(초) — 프리팹의 파티클 수명 + 트레일 수명보다 길게 잡을 것")]
    [SerializeField] private float m_arcLifetime = 0.45f;

    // 간격 < 수명이라 아크끼리 겹친다 — 의도된 것이다. 하나씩 띄엄띄엄 뜨면 '경련'이 아니라
    // 뭉근하게 떠 있는 장식이 된다. 겹치되 각각이 짧아야 지직거리는 인상이 나온다.
    [Tooltip("아크가 튀는 간격의 최솟값(초). 짧을수록 급박해 보인다")]
    [SerializeField] private float m_minInterval = 0.12f;

    [SerializeField] private float m_maxInterval = 0.26f;

    [Tooltip("잦아드는 구간에서 간격에 곱하는 배수 — 클수록 성겨진다")]
    [SerializeField] private float m_calmIntervalScale = 2.5f;

    /// <summary>
    /// 기절 <b>마지막 이만큼(초)</b>이 잦아드는 구간이다 — "곧 깨어난다"를 알리는 예고. (#477)
    /// NPC(<see cref="NpcShockView"/>)와 플레이어(<see cref="PlayerHitView"/>)가 <b>같은 값을 봐야 한다.</b>
    /// 각자 상수를 들면 같은 테이저에 맞았는데 잦아드는 시점이 다르다.
    /// </summary>
    public const float k_calmTailSeconds = 1.2f;

    // 아크를 낳을 자리의 후보. Awake에서 한 번만 모은다 — 이후 자식으로 생기는 아크 파티클이
    // 목록에 섞여 들어가지 않게 하려면 반드시 사전 캐시여야 한다.
    private readonly List<Renderer> m_bodyRenderers = new List<Renderer>();

    // 그중 지금 실제로 보이는 것만 추린 것. 방출을 켜는 순간 다시 만든다 — 아래 주석 참고.
    private readonly List<Renderer> m_visibleRenderers = new List<Renderer>();

    private bool m_emitting;
    private bool m_calm;
    private float m_nextBurstTime;

    private void Awake()
    {
        foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
        {
            // 파티클·트레일은 몸이 아니다 — 자기가 낳은 아크를 다음 아크의 위치 기준으로 삼으면
            // 방출점이 몸에서 점점 떠내려간다.
            if (renderer is ParticleSystemRenderer || renderer is TrailRenderer)
                continue;
            m_bodyRenderers.Add(renderer);
        }
    }

    /// <summary>
    /// 방출 온/오프. 켜는 순간 첫 아크가 즉시 나간다 — 맞은 티가 나야 하므로 간격을 기다리지 않는다.
    /// </summary>
    public void SetEmitting(bool emitting)
    {
        if (emitting == m_emitting)
            return;

        m_emitting = emitting;
        if (!emitting)
            return;

        RefreshVisibleRenderers();
        m_nextBurstTime = Time.time;
    }

    /// <summary>
    /// 잦아들기 시작 — 간격만 벌어지고 <b>멈추지는 않는다</b>. 완전히 끊으면 이미 깨어난 것처럼 보인다. (#477)
    /// </summary>
    public void SetCalm(bool calm) => m_calm = calm;

    private void Update()
    {
        if (!m_emitting || m_arcPrefab == null || Time.time < m_nextBurstTime)
            return;

        GameObject arc = Instantiate(m_arcPrefab, RandomBodyPoint(), Quaternion.identity, transform);
        Destroy(arc, m_arcLifetime);

        float interval = Random.Range(m_minInterval, m_maxInterval);
        m_nextBurstTime = Time.time + (m_calm ? interval * m_calmIntervalScale : interval);
    }

    /// <summary>
    /// <b>지금 켜져 있는</b> 렌더러만 추린다 — 방출 시작 시점에 한 번 돈다.
    /// </summary>
    /// <remarks>
    /// Awake에서 걸러 둘 수 없다. Synty 캐릭터는 바디 변형 스킨드 메시를 여럿(20개 남짓) 달고
    /// 있고 <see cref="NpcAppearance"/>가 그중 <b>하나만</b> 켜는데, 그 배정이 OnNetworkSpawn/Start라
    /// Awake보다 나중이다 — Awake 시점에는 어느 것이 켜질지 알 수 없다.
    ///
    /// 걸러내지 않으면 대부분의 아크가 <b>꺼진 렌더러</b>의 바운드에 붙는다. 꺼진 스킨드 메시는
    /// 스키닝이 갱신되지 않아 누운 자세를 반영하지 못하므로, 쓰러진 NPC인데 아크는 서 있을 때
    /// 높이의 허공에 뜬다.
    ///
    /// 뽑을 때마다 재추첨하는 방식은 쓰지 않는다 — 20개 중 1개만 켜져 있어 몇 번 굴려서는 맞히지 못한다.
    /// </remarks>
    private void RefreshVisibleRenderers()
    {
        m_visibleRenderers.Clear();
        for (int i = 0; i < m_bodyRenderers.Count; i++)
        {
            Renderer renderer = m_bodyRenderers[i];
            if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy)
                m_visibleRenderers.Add(renderer);
        }
    }

    // 몸 어딘가에서 하나 튀긴다 — 매번 다른 자리여야 경련으로 읽힌다(한 점에서만 나면 장식으로 보인다).
    // 바운드는 월드 기준이라 누운 자세·끌려가는 중에도 몸을 따라간다.
    private Vector3 RandomBodyPoint()
    {
        if (m_visibleRenderers.Count == 0)
            return transform.position + Vector3.up * 0.5f;

        Bounds bounds = m_visibleRenderers[Random.Range(0, m_visibleRenderers.Count)].bounds;
        return new Vector3(
            Random.Range(bounds.min.x, bounds.max.x),
            Random.Range(bounds.min.y, bounds.max.y),
            Random.Range(bounds.min.z, bounds.max.z)
        );
    }
}
