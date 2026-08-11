using UnityEngine;

public class SnowView : MonoBehaviour
{
    [Header("FX Settings")]
    [SerializeField] private GameObject m_snowParticlePrefab;
    
    [Header("Cloud Settings")]
    [SerializeField] private GameObject m_cloudPrefab;
    [SerializeField] private float m_cloudScale = 10f;
    [SerializeField] private Vector3 m_cloudPositionOffset = new Vector3(0f, 20f, 0f);

    [Header("Transform Settings")]
    [SerializeField] private float m_snowScale = 1f;
    [SerializeField] private Vector3 m_positionOffset = new Vector3(0f, 5f, 5f);

    private SnowEvent m_snowEvent;
    private GameObject m_particleInstance;
    private GameObject m_currentCloudFx; 

    private void Start()
    {
        m_snowEvent = App.Game.SuddenEvent?.GetEvent<SnowEvent>();
        if (m_snowEvent == null) return;

        m_snowEvent.OnSnowChanged += HandleSnowChanged;
        HandleSnowChanged(m_snowEvent.IsSnow);
    }

    private void OnDestroy()
    {
        if (m_snowEvent != null) m_snowEvent.OnSnowChanged -= HandleSnowChanged;
    }

    private void HandleSnowChanged(bool isSnowing)
    {
        if (isSnowing)
        {
            if (Camera.main == null) return;
            Transform camTransform = Camera.main.transform; 

            // 1. 눈 생성
            if (m_particleInstance == null && m_snowParticlePrefab != null)
            {
                m_particleInstance = Instantiate(m_snowParticlePrefab, camTransform);
                m_particleInstance.transform.localPosition = m_positionOffset;
                m_particleInstance.transform.localScale = new Vector3(m_snowScale, m_snowScale, m_snowScale);

                // [수정됨] CS1612 에러 해결
                foreach (var ps in m_particleInstance.GetComponentsInChildren<ParticleSystem>())
                {
                    var mainModule = ps.main;
                    mainModule.scalingMode = ParticleSystemScalingMode.Hierarchy;
                }
            }

            // 2. 먹구름 생성
            if (m_currentCloudFx == null && m_cloudPrefab != null)
            {
                m_currentCloudFx = Instantiate(m_cloudPrefab, camTransform);
                m_currentCloudFx.transform.localPosition = m_cloudPositionOffset;
                m_currentCloudFx.transform.localScale = new Vector3(m_cloudScale, m_cloudScale, m_cloudScale);
                
                // [수정됨] CS1612 에러 해결
                foreach (var ps in m_currentCloudFx.GetComponentsInChildren<ParticleSystem>())
                {
                    var mainModule = ps.main;
                    mainModule.scalingMode = ParticleSystemScalingMode.Hierarchy;
                }
            }
        }
        else
        {
            if (m_particleInstance != null) { Destroy(m_particleInstance); m_particleInstance = null; }
            if (m_currentCloudFx != null) { Destroy(m_currentCloudFx); m_currentCloudFx = null; }
        }
    }
}