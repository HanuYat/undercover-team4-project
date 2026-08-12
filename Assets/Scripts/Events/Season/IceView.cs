using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 빙판 표현 계층 — <see cref="SnowEvent.IceRatio"/>를 받아 바닥에 얼음 데칼을 깐다. (#227)
/// 판정(누적·해빙)은 이벤트가 하고 여기는 보이는 것만 맡는다.
///
/// <b>도시 전체에 데칼을 뿌리지 않는다.</b> 플레이어 주변에만 정해진 개수를 깔고 따라다니게 한다 —
/// 맵 전체를 덮으려면 데칼이 수백 장 필요하고 대부분은 화면에 없다.
///
/// <b>월드 격자에 스냅한다</b> — 이게 핵심이다. 그냥 플레이어를 따라 옮기면 얼음판이 발밑에 붙어
/// 같이 미끄러져 다니는 것이 보인다(얼음이 아니라 그림자처럼 읽힌다). 격자 칸에 스냅하면 데칼은
/// 월드에 <b>고정된 채</b>로 있고, 플레이어가 칸을 넘어갈 때만 뒤쪽 것이 앞으로 재배치된다.
///
/// 투명도는 머티리얼이 아니라 <see cref="DecalProjector.fadeFactor"/>로 조절한다 — URP의
/// Shader Graphs/Decal에는 색·알파 프로퍼티가 없다(Base_Map·Normal_Blend뿐).
///
/// 각 피어가 자기 화면에 자기 데칼을 만든다 — 표현이라 복제하지 않는다.
/// <b>Decal Renderer Feature가 렌더러에 등록돼 있어야 보인다</b>(PC_Renderer에 추가해 뒀다).
/// </summary>
public class IceView : MonoBehaviour
{
    [Header("데칼")]
    [Tooltip("얼음 데칼 머티리얼 — URP Decal 셰이더여야 한다")]
    [SerializeField] private Material m_iceMaterial;

    [Tooltip("데칼 한 장의 크기(m). 격자 칸 크기이기도 하다")]
    [Min(1f)]
    [SerializeField] private float m_patchSize = 12f;

    [Tooltip("플레이어 주변 몇 칸까지 깔 것인가 — 1이면 3x3=9장, 2면 5x5=25장")]
    [Range(1, 3)]
    [SerializeField] private int m_gridRadius = 2;

    [Tooltip("데칼이 아래로 투사되는 깊이(m) — 경사·계단을 덮을 만큼")]
    [Min(1f)]
    [SerializeField] private float m_projectionDepth = 8f;

    [Tooltip("빙판이 가장 두꺼울 때의 데칼 진하기 — 1은 너무 하얗게 덮인다")]
    [Range(0.1f, 1f)]
    [SerializeField] private float m_maxOpacity = 0.7f;

    private SnowEvent m_snowEvent;
    private Transform m_root;
    private DecalProjector[] m_patches;
    private float m_ratio;

    private void Start()
    {
        m_snowEvent = App.Game.SuddenEvent?.GetEvent<SnowEvent>();
        if (m_snowEvent == null)
            return;

        m_snowEvent.OnIceRatioChanged += HandleIceRatioChanged;
        HandleIceRatioChanged(m_snowEvent.IceRatio); // 늦게 들어온 클라 — 이미 깔려 있으면 지금 반영
    }

    private void OnDestroy()
    {
        if (m_snowEvent != null)
            m_snowEvent.OnIceRatioChanged -= HandleIceRatioChanged;
    }

    private void HandleIceRatioChanged(float ratio)
    {
        m_ratio = ratio;

        if (ratio <= 0f)
        {
            Dispose();
            return;
        }

        EnsurePatches();
        ApplyOpacity();
    }

    private void EnsurePatches()
    {
        if (m_patches != null || m_iceMaterial == null)
            return;

        m_root = new GameObject("IcePatches").transform;

        int side = m_gridRadius * 2 + 1;
        m_patches = new DecalProjector[side * side];
        for (int i = 0; i < m_patches.Length; i++)
        {
            GameObject go = new GameObject("IcePatch");
            go.transform.SetParent(m_root, false);
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // 아래로 투사

            DecalProjector projector = go.AddComponent<DecalProjector>();
            projector.material = m_iceMaterial;
            projector.size = new Vector3(m_patchSize, m_patchSize, m_projectionDepth);
            projector.pivot = new Vector3(0f, 0f, m_projectionDepth * 0.5f); // 위에서 아래로 훑게
            m_patches[i] = projector;
        }
    }

    private void ApplyOpacity()
    {
        if (m_patches == null)
            return;

        float fade = m_ratio * m_maxOpacity;
        foreach (DecalProjector p in m_patches)
            if (p != null)
                p.fadeFactor = fade;
    }

    private void Dispose()
    {
        if (m_root != null)
            Destroy(m_root.gameObject);

        m_root = null;
        m_patches = null;
    }

    // 격자 스냅 — 플레이어가 칸을 넘을 때만 재배치된다. 카메라가 아니라 <b>플레이어</b>를 따라간다:
    // 본부 CCTV로 시점을 옮겼을 때 얼음판이 카메라를 따라 날아오면 안 된다.
    private void LateUpdate()
    {
        if (m_patches == null)
            return;

        Transform anchor = ResolveLocalPlayer();
        if (anchor == null)
            return;

        // 중심 칸을 구한다 — 월드 좌표를 칸 크기로 내림해 격자에 고정한다
        float cx = Mathf.Floor(anchor.position.x / m_patchSize) * m_patchSize;
        float cz = Mathf.Floor(anchor.position.z / m_patchSize) * m_patchSize;
        float y = anchor.position.y + m_projectionDepth * 0.5f; // 발밑을 덮게 위에서 시작

        int side = m_gridRadius * 2 + 1;
        for (int i = 0; i < m_patches.Length; i++)
        {
            int gx = i % side - m_gridRadius;
            int gz = i / side - m_gridRadius;
            m_patches[i].transform.position = new Vector3(cx + gx * m_patchSize, y, cz + gz * m_patchSize);
        }
    }

    // 이 화면의 기준이 되는 로컬 플레이어 — 없으면(관전·로딩 중) 카메라로 물러난다
    private Transform ResolveLocalPlayer()
    {
        Unity.Netcode.NetworkManager manager = Unity.Netcode.NetworkManager.Singleton;
        if (manager != null && manager.LocalClient.PlayerObject != null)
            return manager.LocalClient.PlayerObject.transform;

        return Camera.main != null ? Camera.main.transform : null;
    }
}
