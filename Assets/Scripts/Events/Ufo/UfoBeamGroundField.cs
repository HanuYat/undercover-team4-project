using UnityEngine;

/// <summary>
/// UFO 빔 지면 높이맵 (#907) — 기체 아래를 격자로 쏴 지면 높이를 텍스처로 굽는다.
/// 빔 셰이더(<c>Undercover/Events/UfoBeam</c>)가 픽셀마다 읽어 그보다 아래를 잘라낸다.
/// 굽는 방식은 <see cref="PrecipitationMask"/>와 같고, 판정과 같은 콜라이더·마스크를 쓴다.
/// </summary>
[RequireComponent(typeof(UfoCraft))]
public class UfoBeamGroundField : MonoBehaviour
{
    [Tooltip("빔 기둥의 렌더러 — 이 렌더러의 재질을 복제해 높이맵을 물린다. 비우면 굽지 않는다")]
    [SerializeField] private Renderer m_beamRenderer;

    [Tooltip("높이맵 한 변의 칸 수 — 32면 레이 1024발. 올리면 지붕 경계가 날카로워지지만 레이 수가 제곱으로 는다")]
    [Range(8, 64)]
    [SerializeField] private int m_grid = 32;

    [Tooltip("기둥 반경 바깥으로 더 재는 여유(m) — 기둥 벽면이 높이맵 가장자리에 걸치지 않게")]
    [Min(0.1f)]
    [SerializeField] private float m_padding = 0.5f;

    [Tooltip("기체보다 이만큼(m) 위에서 쏜다 — 기체 높이에 딱 붙은 면도 잡히게")]
    [Min(0f)]
    [SerializeField] private float m_rayLift = 0.5f;

    [Tooltip("다시 굽는 간격(초) — 높이맵은 구운 자리에 월드로 박혀 있어 건너뛴 동안에도 지면이 밀리지 않는다. " +
             "그사이 기체가 움직인 만큼만 발자국이 뒤처지므로 여유(m_padding)를 넘길 만큼 벌리지 말 것")]
    [Min(0f)]
    [SerializeField] private float m_refreshInterval = 0.05f;

    private static readonly int s_heightMapId = Shader.PropertyToID("_HeightMap");
    private static readonly int s_heightFieldId = Shader.PropertyToID("_HeightField");

    private UfoCraft m_craft;
    private Material m_beamMaterial; // 기체마다 다른 높이맵이 들어가므로 재질을 복제해서 쓴다
    private Texture2D m_map;
    private float[] m_heights;
    private int m_built; // 지금 버퍼가 만들어진 격자 크기 (인스펙터에서 바뀌면 다시 만든다)
    private float m_nextBakeAt;

    /// <summary>한 번이라도 구웠는가 — 굽기 전에는 잘라 낼 근거가 없다.</summary>
    public bool HasField { get; private set; }

    /// <summary>마지막 베이크에서 가장 낮았던 지면 높이 — 기둥을 여기까지 늘려야 길 쪽이 바닥에 닿는다.</summary>
    public float LowestGround { get; private set; }

    private void Awake() => m_craft = GetComponent<UfoCraft>();

    private void OnDestroy()
    {
        // 런타임에 만든 것은 스스로 정리한다
        if (m_map != null)
            Destroy(m_map);
        m_map = null;

        if (m_beamMaterial != null)
            Destroy(m_beamMaterial);
        m_beamMaterial = null;
    }

    // 기체가 움직인 뒤에 굽는다 — Update에서 구우면 높이맵이 한 프레임 뒤처져 기둥이 밀린다
    private void LateUpdate()
    {
        if (m_beamRenderer == null || Time.time < m_nextBakeAt)
            return;

        m_nextBakeAt = Time.time + m_refreshInterval;
        EnsureBuffers();

        // 구운 자리도 같이 넘긴다 — 월드 좌표라 건너뛰는 동안에도 경계가 제자리다
        Vector3 center = transform.position;
        float size = FieldSize;
        Bake(center, size);

        m_map.SetPixelData(m_heights, 0);
        m_map.Apply(updateMipmaps: false);

        m_beamMaterial ??= m_beamRenderer.material; // 첫 접근에서 복제된다
        m_beamMaterial.SetTexture(s_heightMapId, m_map);
        m_beamMaterial.SetVector(
            s_heightFieldId,
            new Vector4(center.x - size * 0.5f, center.z - size * 0.5f, 1f / size, 1f)
        );

        HasField = true;
    }

    private float FieldSize => (m_craft.BeamRadius + m_padding) * 2f;

    private void EnsureBuffers()
    {
        if (m_built == m_grid && m_map != null)
            return;

        m_built = m_grid;
        m_heights = new float[m_grid * m_grid];

        if (m_map != null)
            Destroy(m_map);

        // 월드 Y를 그대로 담아서 부동소수 한 채널이다 (R8은 0~1밖에 못 담는다)
        m_map = new Texture2D(m_grid, m_grid, TextureFormat.RFloat, mipChain: false, linear: true)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            name = "UfoBeamGround",
        };
    }

    // 칸마다 위에서 아래로 한 발 — 가장 가까운 히트가 그 자리에서 빔이 멈출 면이다
    private void Bake(Vector3 center, float size)
    {
        float step = size / m_grid;
        float corner = -size * 0.5f + step * 0.5f;

        float probe = m_craft.GroundProbeDistance;
        float miss = center.y - probe; // 아무것도 없으면 아주 아래 — 잘라 낼 것이 없다는 뜻
        float lowest = float.MaxValue;
        bool anyHit = false;

        for (int z = 0; z < m_grid; z++)
        {
            for (int x = 0; x < m_grid; x++)
            {
                Vector3 origin = new Vector3(
                    center.x + corner + x * step,
                    center.y + m_rayLift,
                    center.z + corner + z * step
                );

                bool hitGround = Physics.Raycast(
                    origin, Vector3.down, out RaycastHit hit,
                    probe + m_rayLift, m_craft.GroundMask, QueryTriggerInteraction.Ignore);

                m_heights[z * m_grid + x] = hitGround ? hit.point.y : miss;

                // 빗나간 칸은 길이에 치지 않는다 — 폴백 200m가 섞이면 기둥이 허공으로 늘어난다
                if (hitGround && hit.point.y < lowest)
                {
                    lowest = hit.point.y;
                    anyHit = true;
                }
            }
        }

        LowestGround = anyHit ? lowest : center.y;
    }
}
