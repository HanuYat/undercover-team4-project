using UnityEngine;

/// <summary>
/// 안개의 클라이언트 표현 — 안개 이벤트 플래그(<see cref="FogEvent.IsFog"/>)를 구독해 거리 안개와
/// 파티클을 켜고 끈다. (GDD 6-4, #227) 서버·원격 클라·오프라인 모든 피어에서 각자 자기 화면을
/// 처리한다 (플래그 변화는 <see cref="FogEvent.OnFogChanged"/>가 전 피어에서 발행한다).
///
/// <b>화면을 덮는 방식이 아니다</b> — 전자기기 먹통의 전체 화면 시야 제한이 이동·상호작용을 막아
/// 폐기된 선례(<see cref="DeviceBlackoutView"/>, #434)를 따른다. 안개는 <b>거리 안개</b>(멀리가
/// 안 보임)로 시야를 제한하고 파티클은 분위기용 장식이라, 이동·조작은 그대로 되고
/// "멀리 있는 것을 못 본다"만 성립한다.
///
/// 거리 안개는 씬 전역(<see cref="RenderSettings"/>)이라, 켜기 전의 원래 값을 저장해 두었다가
/// 걷힐 때·파괴될 때 되돌린다 — 씬이 원래 안개를 쓰는 구성을 덮어쓰지 않기 위함이다.
/// </summary>
public class FogView : MonoBehaviour
{
    [Header("참조 (비우면 씬에서 자동 탐색)")]
    [SerializeField]
    private FogEvent m_fog;

    [Header("거리 안개 (RenderSettings — URP 환경 안개)")]
    [Tooltip("안개 색")]
    [SerializeField]
    private Color m_fogColor = new Color(0.6f, 0.62f, 0.66f);

    [Tooltip("안개 밀도 — 높을수록 가까이서부터 뿌예진다 (ExponentialSquared 기준)")]
    [SerializeField]
    private float m_fogDensity = 0.03f;

    [Header("파티클 (선택 — 씬에 배치한 안개 FX를 연결, 비활성 상태로 둘 것)")]
    [Tooltip("안개 동안만 켜지는 파티클 오브젝트들 (예: FX_Fog). 비워도 거리 안개는 동작한다")]
    [SerializeField]
    private GameObject[] m_fogVfx;

    // 씬의 원래 안개 설정 — 안개가 걷히면 그대로 되돌린다 (씬 고유 안개 구성 보호)
    private bool m_savedFogEnabled;
    private Color m_savedFogColor;
    private FogMode m_savedFogMode;
    private float m_savedFogDensity;
    private bool m_saved;

    private void Start()
    {
        // 비워두면 돌발 이벤트 매니저에게 물어본다 — 안개 이벤트는 App에 따로 등록하지 않는다 (#372 리뷰, R1/R3).
        // 이벤트 풀에서 항목을 껐거나 등록하지 않은 구성이면 null이 오고, 그 경우 안개는 발생하지도 않는다.
        if (m_fog == null)
            m_fog = App.Game.SuddenEvent?.GetEvent<FogEvent>();

        if (m_fog == null)
        {
            Debug.LogWarning("FogView: FogEvent를 찾지 못해 안개 표현이 동작하지 않는다", this);
            return;
        }

        SetVfxActive(false); // 시작은 꺼둔 상태 — 활성 전파가 오면 켠다

        m_fog.OnFogChanged += HandleFogChanged;
        // 늦게 붙었을 때(이미 안개 진행 중) 현재 상태를 즉시 반영
        HandleFogChanged(m_fog.IsFog);
    }

    private void OnDestroy()
    {
        if (m_fog != null)
            m_fog.OnFogChanged -= HandleFogChanged;

        // 안개를 켜둔 채 씬이 내려가면 다음 씬까지 안개가 남는다 — 파괴 시 원복한다.
        if (m_saved)
            RestoreFog();
    }

    private void HandleFogChanged(bool active)
    {
        if (active)
            ApplyFog();
        else if (m_saved)
            RestoreFog();

        SetVfxActive(active);
    }

    private void ApplyFog()
    {
        // 원래 값은 최초 1회만 저장한다 — 이미 안개 중에 다시 적용돼도 우리 값을 원본으로 오인하지 않게.
        if (!m_saved)
        {
            m_savedFogEnabled = RenderSettings.fog;
            m_savedFogColor = RenderSettings.fogColor;
            m_savedFogMode = RenderSettings.fogMode;
            m_savedFogDensity = RenderSettings.fogDensity;
            m_saved = true;
        }

        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = m_fogColor;
        RenderSettings.fogDensity = m_fogDensity;
    }

    private void RestoreFog()
    {
        RenderSettings.fog = m_savedFogEnabled;
        RenderSettings.fogColor = m_savedFogColor;
        RenderSettings.fogMode = m_savedFogMode;
        RenderSettings.fogDensity = m_savedFogDensity;
        m_saved = false;
    }

    private void SetVfxActive(bool active)
    {
        if (m_fogVfx == null)
            return;

        for (int i = 0; i < m_fogVfx.Length; i++)
        {
            if (m_fogVfx[i] != null)
                m_fogVfx[i].SetActive(active);
        }
    }
}
