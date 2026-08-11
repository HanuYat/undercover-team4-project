using UnityEngine;

public class SnowView : MonoBehaviour
{
    [Header("FX Settings")]
    [SerializeField] private GameObject m_snowParticlePrefab;
    
    private SnowEvent m_snowEvent;
    private GameObject m_particleInstance;

    private void Start()
    {
        // App 싱글톤을 통한 이벤트 조회 (아키텍처 규칙 준수)
        m_snowEvent = App.Game.SuddenEvent?.GetEvent<SnowEvent>();
        
        if (m_snowEvent == null)
        {
            Debug.LogWarning("SnowEvent를 찾을 수 없습니다.");
            return;
        }

        m_snowEvent.OnSnowChanged += HandleSnowChanged;
        
        // 초기 상태 즉시 반영
        HandleSnowChanged(m_snowEvent.IsSnow);
    }

    private void OnDestroy()
    {
        if (m_snowEvent != null)
        {
            m_snowEvent.OnSnowChanged -= HandleSnowChanged;
        }
    }

    private void HandleSnowChanged(bool isSnowing)
    {
        if (isSnowing)
        {
            if (m_particleInstance == null && m_snowParticlePrefab != null)
            {
                // 로컬 카메라 기준으로 스폰하여 뷰를 덮도록 설정 (위치/회전값은 인스펙터 프리팹에서 세팅 권장)
                Transform localCamera = Camera.main.transform; 
                m_particleInstance = Instantiate(m_snowParticlePrefab, localCamera);
                
                // URP 환경에서 파티클이 자홍색으로 깨진다면 
                // PolygonGeneric/Prefabs/FX/FX_Snow_01 (URP 셰이더)를 사용하세요.
            }
        }
        else
        {
            if (m_particleInstance != null)
            {
                Destroy(m_particleInstance);
            }
        }
    }
}