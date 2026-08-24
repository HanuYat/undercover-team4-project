using System.Collections;
using UnityEngine;

/// <summary>
/// 구역 스캔 결과 링 (#490) — 사용 지점에서 서서히 퍼지는 원형 링으로 판정 결과(초록/빨강)를 보여준다.
///
/// <b>순수 로컬 연출이다</b> — 네트워크에 싣지 않고 각 피어가 자기 화면에 스스로 만든다
/// (docs/architecture.md 연출 전파 규칙의 '일회성 연출' 행, <see cref="ShockArcEmitter"/>와 같은 방침).
/// 그래서 <see cref="AreaScanner"/>가 <c>Instantiate</c>로 낳을 뿐 <c>DefaultNetworkPrefabs.asset</c>
/// 등록이 필요 없다.
///
/// <see cref="LineRenderer"/> 세그먼트 원을 0 → 반경으로 확장한 뒤 색을 유지한 채 페이드 아웃하고
/// 스스로 파괴된다. 반경·색은 <see cref="AreaScanner"/>가 판정에 쓴 값을 그대로 넘겨받는다(단일
/// 출처) — 표시 반경이 판정 반경과 갈리면 안 되기 때문이다.
///
/// <see cref="RopeDragView"/>가 프로젝트 내 유일한 LineRenderer 선례라 월드 좌표·<c>sharedMaterial</c>
/// 관례를 그대로 따른다.
/// </summary>
[RequireComponent(typeof(LineRenderer))]
public class AreaScanRingView : MonoBehaviour
{
    [Tooltip("링 두께(m)")]
    [SerializeField]
    private float m_lineWidth = 0.15f;

    [Tooltip("세그먼트 수 — 원의 매끄러움")]
    [Range(8, 128)]
    [SerializeField]
    private int m_segments = 64;

    [Tooltip("0 → 반경까지 퍼지는 데 걸리는 시간(초) — '서서히 퍼지는' 속도")]
    [SerializeField]
    private float m_expandSeconds = 1.2f;

    [Tooltip("퍼짐 진행 곡선 — 기본은 초반 빠르게 시작해 끝에서 감속한다")]
    [SerializeField]
    private AnimationCurve m_expandEase = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Tooltip("다 퍼진 뒤 완전히 사라지기까지 걸리는 시간(초)")]
    [SerializeField]
    private float m_fadeSeconds = 0.6f;

    [Tooltip("바닥에 파묻히지 않게 링을 띄우는 높이(m)")]
    [SerializeField]
    private float m_groundOffset = 0.05f;

    private LineRenderer m_line;

    private void Awake()
    {
        m_line = GetComponent<LineRenderer>();
        m_line.useWorldSpace = true; // 부모 없이 월드에 독립 — 회전해도 원이 비틀리지 않는다
        m_line.loop = true; // 닫힌 원 — 시작점과 끝점을 겹치는 보정이 필요 없다
        m_line.numCapVertices = 0;
        m_line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        m_line.receiveShadows = false;
    }

    /// <summary>
    /// 링 재생을 시작한다 — <see cref="AreaScanner"/>가 인스턴스화 직후 즉시 호출한다.
    /// </summary>
    /// <param name="center">사용 지점(월드 좌표) — 지면 높이는 호출자가 넘긴 값을 그대로 쓴다</param>
    /// <param name="radius">최종 반경(m) — <see cref="AreaScanner"/>의 판정 반경과 같은 값</param>
    /// <param name="color">판정 결과색 — 초록(진범 있음) 또는 빨강(없음)</param>
    public void Play(Vector3 center, float radius, Color color)
    {
        m_line.widthMultiplier = m_lineWidth;
        m_line.startColor = color;
        m_line.endColor = color;

        StartCoroutine(PlayRoutine(center + Vector3.up * m_groundOffset, radius, color));
    }

    private IEnumerator PlayRoutine(Vector3 center, float radius, Color color)
    {
        // 확장 구간 — 색은 처음부터 결과색이다. 판정은 사용 시점 스냅샷이라 다 퍼진 뒤에
        // 색을 정할 이유가 없고, 즉시 읽히는 편이 정보 도구로서 낫다.
        float elapsed = 0f;
        while (elapsed < m_expandSeconds)
        {
            elapsed += Time.deltaTime;
            float progress = m_expandEase.Evaluate(Mathf.Clamp01(elapsed / m_expandSeconds));
            DrawCircle(center, radius * progress);
            yield return null;
        }

        DrawCircle(center, radius);

        // 페이드 구간 — 다 퍼진 반경을 유지한 채 알파만 지운다(존재감만 사라진다).
        float fadeElapsed = 0f;
        while (fadeElapsed < m_fadeSeconds)
        {
            fadeElapsed += Time.deltaTime;
            Color faded = color;
            faded.a = color.a * (1f - Mathf.Clamp01(fadeElapsed / m_fadeSeconds));
            m_line.startColor = faded;
            m_line.endColor = faded;
            yield return null;
        }

        Destroy(gameObject);
    }

    private void DrawCircle(Vector3 center, float radius)
    {
        if (m_line.positionCount != m_segments)
            m_line.positionCount = m_segments;

        for (int i = 0; i < m_segments; i++)
        {
            float angle = i * Mathf.PI * 2f / m_segments;
            Vector3 point = center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
            m_line.SetPosition(i, point);
        }
    }
}
