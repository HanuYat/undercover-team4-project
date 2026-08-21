using UnityEngine;

/// <summary>
/// 화면 강수 마스크 (#782) — <b>"이 픽셀이 보는 지점이 하늘에 열려 있는가"</b>를 저해상 텍스처로 굽는다.
///
/// 강수 표현이 파티클 리그에서 화면 셰이더로 옮겨 오면서, 예전에 배치로 풀던 세 예외
/// (실외 배치 / 기둥·처마 회피 #647 / 창 너머 재배치 #733)가 전부 이 마스크 하나로 대체된다 —
/// 물어야 하는 것이 "내가 실외인가"가 아니라 "이 방향이 열려 있는가"이기 때문이다.
/// 설계 근거: <c>docs/superpowers/specs/2026-08-21-precipitation-shader-design.md</c>
///
/// <b>베이크하지 않는다.</b> 진짜 카메라에서 쏜 진짜 레이라 층이 있는 구조(다리 아래·2층 실내)가
/// 자연히 맞고, 맵마다 표시해 둘 것이 없다(<see cref="WeatherShelter"/> 철학).
///
/// <b>판정은 빌려 쓴다.</b> 하늘이 막혔는지는 <see cref="WeatherShelter.IsSheltered"/> 하나가 답한다 —
/// 낙뢰 대상(<c>LightningEvent</c>)·빙판 누적(<c>SnowEvent</c>)이 쓰는 그 함수다. 규칙을 복제하면
/// "눈은 그쳤는데 벼락은 떨어진다"가 다시 난다.
/// </summary>
public class PrecipitationMask : MonoBehaviour
{
    [Header("해상도")]
    [Tooltip(
        "화면을 한 변 몇 칸으로 나눌지 — 9면 레이 81발. 올리면 창틀 경계가 날카로워지지만 "
            + "레이 수가 제곱으로 는다. 셰이더가 바이리니어로 늘려 읽으므로 낮아도 부드럽다"
    )]
    [Range(3, 17)]
    [SerializeField] private int m_grid = 9;

    [Header("하늘 판정")]
    [Tooltip("이 거리(m) 안에 뭔가 있으면 막힌 것 — WeatherShelter와 같은 기준. 건물 높이보다 넉넉히")]
    [Min(0f)]
    [SerializeField] private float m_probeHeight = 25f;

    [Tooltip("하늘을 막는 것으로 칠 레이어 — 건물은 Default다")]
    [SerializeField] private LayerMask m_blockMask = 1;

    [Tooltip("픽셀이 보는 지점을 찾는 최대 거리(m) — 넘으면 하늘로 본다")]
    [Min(1f)]
    [SerializeField] private float m_viewProbeDistance = 60f;

    [Tooltip(
        "맞은 표면에서 물러설 거리(m) — 벽 콜라이더 <b>안에서</b> 위로 쏘면 그 콜라이더를 맞히지 못해 "
            + "실내 벽이 하늘로 잡힌다. WeatherSkyRig의 창 탐색이 같은 이유로 쓰던 값이다"
    )]
    [Min(0.01f)]
    [SerializeField] private float m_surfaceBackoff = 0.5f;

    [Header("떨림 억제")]
    [Tooltip("칸 값이 0↔1로 옮겨 가는 속도(1/초) — 낮으면 경계가 부드럽고 높으면 반응이 빠르다")]
    [Min(0.1f)]
    [SerializeField] private float m_settleSpeed = 6f;

    [Header("진단")]
    [Tooltip("켜면 마스크 격자를 콘솔에 그린다 — 셰이더 없이 판정만 확인할 때 쓴다 (#782)")]
    [SerializeField] private bool m_logMask;

    [Tooltip("찍는 간격(초)")]
    [Min(0.1f)]
    [SerializeField] private float m_logInterval = 1f;

    // 셰이더가 읽는 전역 — 강수 셰이더 하나뿐이라 머티리얼마다 물리지 않는다
    private static readonly int s_maskId = Shader.PropertyToID("_PrecipMask");
    private static readonly int s_amountId = Shader.PropertyToID("_PrecipAmount");

    private Camera m_camera; // 시점 카메라 — 꺼지면 다시 찾는다 (ResolveCamera)

    private float m_nextLogAt;
    private System.Text.StringBuilder m_logBuffer;

    private Texture2D m_mask;
    private Color[] m_pixels; // Texture2D.SetPixels용 버퍼 — 매 프레임 새로 만들지 않는다
    private int m_built; // 지금 버퍼가 만들어진 격자 크기 (인스펙터에서 바뀌면 다시 만든다)

    /// <summary>강수 세기(0~1) — 뷰(<c>SnowView</c>·<c>LightningView</c>)가 on/off·페이드로 쓴다.</summary>
    public float Amount { get; set; }

    /// <summary>마지막으로 구운 칸 중 하늘이 열린 비율 — 진단·테스트용. 격자가 없으면 0.</summary>
    public float OpenRatio { get; private set; }

    private void OnDisable()
    {
        // 강수가 끝나면 셰이더가 남은 마스크로 계속 그리지 않게 세기를 0으로 눌러 둔다
        Shader.SetGlobalFloat(s_amountId, 0f);
    }

    // 카메라가 움직인 뒤에 굽는다 — 시점 제어(PlayerLook)가 Update 구간에서 카메라를 옮기므로,
    // Update에서 구우면 마스크가 한 프레임 뒤처져 빠르게 돌 때 경계가 밀린다. (WeatherSkyRig와 같은 사정)
    private void LateUpdate()
    {
        Camera camera = ResolveCamera();
        if (camera == null)
            return;

        EnsureBuffers();
        Bake(camera);

        m_mask.SetPixels(m_pixels);
        m_mask.Apply(updateMipmaps: false);

        Shader.SetGlobalTexture(s_maskId, m_mask);
        Shader.SetGlobalFloat(s_amountId, Mathf.Clamp01(Amount));

        if (m_logMask)
            TickLog(camera);
    }

    // 격자를 그대로 그린다 — 숫자 하나로는 "창가에서 그 방향만 열렸다"가 안 보인다.
    // # = 열림, . = 막힘, 중간값은 + (시간 보간 중). 화면 위쪽이 첫 줄이다.
    private void TickLog(Camera camera)
    {
        if (Time.time < m_nextLogAt)
            return;

        m_nextLogAt = Time.time + m_logInterval;
        m_logBuffer ??= new System.Text.StringBuilder();
        m_logBuffer.Clear();

        bool cameraSheltered = WeatherShelter.IsSheltered(
            camera.transform.position,
            m_blockMask,
            m_probeHeight
        );

        m_logBuffer
            .Append("[강수마스크] 열림 ")
            .Append((OpenRatio * 100f).ToString("F0"))
            .Append("% · 카메라 머리 위=")
            .Append(cameraSheltered ? "막힘(실내)" : "열림(실외)")
            .Append('\n');

        for (int y = m_grid - 1; y >= 0; y--)
        {
            m_logBuffer.Append("  ");
            for (int x = 0; x < m_grid; x++)
            {
                float v = m_pixels[y * m_grid + x].r;
                m_logBuffer.Append(v > 0.75f ? '#' : v < 0.25f ? '.' : '+');
            }
            m_logBuffer.Append('\n');
        }

        Debug.Log(m_logBuffer.ToString(), this);
    }

    /// <summary>
    /// 마스크를 구울 카메라 — <b>로컬 플레이어의 시점 카메라가 1순위, `Camera.main`은 폴백이다.</b>
    ///
    /// ⚠ <c>Camera.main</c>만 믿으면 안 된다: <c>Player.prefab</c>의 시점 카메라는 <b>Untagged</b>라
    /// Camera.main으로 잡히지 않는다. 그러면 씬에 놓인 고정 카메라가 잡혀 마스크가 <b>맵의 한 지점
    /// 기준으로 굳는다</b>. (<c>WeatherSkyRig.ResolveView</c>가 같은 이유로 같은 순서를 쓴다)
    ///
    /// 꺼진 카메라는 다시 찾는다 — 관전 전환·CCTV로 갈아 끼워지기 때문이다.
    /// </summary>
    private Camera ResolveCamera()
    {
        if (m_camera != null && m_camera.isActiveAndEnabled)
            return m_camera;

        m_camera = null;

        Unity.Netcode.NetworkManager manager = Unity.Netcode.NetworkManager.Singleton;
        if (manager != null && manager.IsListening && manager.LocalClient.PlayerObject != null)
        {
            foreach (Camera camera in manager.LocalClient.PlayerObject.GetComponentsInChildren<Camera>(true))
            {
                if (!camera.isActiveAndEnabled)
                    continue;

                m_camera = camera;
                return m_camera;
            }
        }

        m_camera = Camera.main;
        return m_camera;
    }

    private void EnsureBuffers()
    {
        if (m_built == m_grid && m_mask != null)
            return;

        m_built = m_grid;
        m_pixels = new Color[m_grid * m_grid];

        // R8 하나면 충분하다 — 값이 "열렸나" 하나뿐이다. 바이리니어로 늘려 읽고 경계는 물리지 않는다.
        m_mask = new Texture2D(m_grid, m_grid, TextureFormat.R8, mipChain: false, linear: true)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            name = "PrecipMask",
        };

        // 처음 한 번은 전부 열린 것으로 둔다 — 실외에서 시작하는 것이 흔하고, 닫히는 쪽으로
        // 수렴하는 것이 반대(실외인데 한 박자 안 내림)보다 눈에 덜 걸린다.
        for (int i = 0; i < m_pixels.Length; i++)
            m_pixels[i] = Color.white;
    }

    // 격자 한 칸 = 화면 한 점. 그 점이 보는 지점을 찾고, 그 지점에서 하늘이 열렸는지 묻는다.
    private void Bake(Camera camera)
    {
        float step = 1f / (m_grid - 1);
        float lerp = Mathf.Clamp01(m_settleSpeed * Time.deltaTime);
        int openCount = 0;

        for (int y = 0; y < m_grid; y++)
        {
            for (int x = 0; x < m_grid; x++)
            {
                Ray ray = camera.ViewportPointToRay(new Vector3(x * step, y * step, 0f));
                bool open = IsSkyOpenAlong(ray);
                if (open)
                    openCount++;

                int index = y * m_grid + x;
                float settled = Mathf.MoveTowards(m_pixels[index].r, open ? 1f : 0f, lerp);
                m_pixels[index].r = settled;
            }
        }

        OpenRatio = (float)openCount / (m_grid * m_grid);
    }

    // 이 시선이 닿는 지점 위가 열려 있는가.
    //
    // 2단이다. ① 시선이 닿는 끝 지점을 잡고 ② 그 지점에서 위로 쏴 하늘을 묻는다. ②가 핵심이다 —
    // 없으면 천장 있는 큰 실내 홀도 하늘로 읽힌다(레이가 끝까지 날아가도 아무것도 안 맞으므로).
    // WeatherSkyRig.TryFindWindow가 창 하나를 찾던 판정을 화면 전체로 넓힌 것이다.
    private bool IsSkyOpenAlong(Ray ray)
    {
        Vector3 probe = Physics.Raycast(
            ray,
            out RaycastHit hit,
            m_viewProbeDistance,
            m_blockMask,
            QueryTriggerInteraction.Ignore
        )
            ? hit.point - ray.direction * m_surfaceBackoff
            : ray.origin + ray.direction * m_viewProbeDistance;

        return !WeatherShelter.IsSheltered(probe, m_blockMask, m_probeHeight);
    }
}
