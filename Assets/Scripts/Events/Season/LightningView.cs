using System.Collections;
using Unity.Netcode;
using UnityEngine;

public class LightningView : MonoBehaviour
{
    public static LightningView Instance { get; private set; }

    [Header("Synty Assets")]
    [SerializeField] private GameObject m_strikeParticlePrefab;
    [SerializeField] private GameObject m_rainParticlePrefab;  

    [Header("Rain Settings")]
    [SerializeField] private float m_rainScale = 2f;
    [SerializeField] private Vector3 m_rainPositionOffset = new Vector3(0f, 5f, 5f);

    [Header("Cloud Settings")]
    [SerializeField] private GameObject m_cloudPrefab;
    [SerializeField] private float m_cloudScale = 10f;
    [SerializeField] private Vector3 m_cloudPositionOffset = new Vector3(0f, 20f, 0f);

    [Header("Screen Flash Settings")]
    [SerializeField] private Light m_globalLight;
    [SerializeField] private float m_flashDuration = 0.1f;
    [SerializeField] private float m_maxFlashIntensity = 1.5f;

    private LightningEvent m_lightningEvent;
    private float m_originalLightIntensity;
    private Coroutine m_flashCoroutine;
    
    private GameObject m_currentRainFx;
    private GameObject m_currentCloudFx; 

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void Start()
    {
        m_lightningEvent = App.Game.SuddenEvent?.GetEvent<LightningEvent>();
        if (m_lightningEvent == null) return;

        if (m_globalLight != null) m_originalLightIntensity = m_globalLight.intensity;

        m_lightningEvent.OnLightningChanged += OnLightningChanged;
        ToggleLightningEffects(m_lightningEvent.IsLightningActive);
    }

    private void OnDestroy()
    {
        if (m_lightningEvent != null) m_lightningEvent.OnLightningChanged -= OnLightningChanged;
        ToggleLightningEffects(false); 
    }

    private void OnLightningChanged(bool isActive) => ToggleLightningEffects(isActive);

    private void ToggleLightningEffects(bool isActive)
    {
        if (isActive)
        {
            if (Camera.main == null) return;
            Transform camTransform = Camera.main.transform;

            // 1. 비 생성
            if (m_currentRainFx == null && m_rainParticlePrefab != null)
            {
                m_currentRainFx = Instantiate(m_rainParticlePrefab, camTransform);
                m_currentRainFx.transform.localPosition = m_rainPositionOffset;
                m_currentRainFx.transform.localScale = new Vector3(m_rainScale, m_rainScale, m_rainScale);
                
                // [수정됨] CS1612 에러 해결: 임시 변수에 할당 후 수정
                foreach (var ps in m_currentRainFx.GetComponentsInChildren<ParticleSystem>())
                {
                    var mainModule = ps.main;
                    mainModule.scalingMode = ParticleSystemScalingMode.Hierarchy;
                }
                
                m_currentRainFx.GetComponentInChildren<ParticleSystem>()?.Play();
            }

            // 2. 먹구름 생성
            if (m_currentCloudFx == null && m_cloudPrefab != null)
            {
                m_currentCloudFx = Instantiate(m_cloudPrefab, camTransform);
                m_currentCloudFx.transform.localPosition = m_cloudPositionOffset;
                m_currentCloudFx.transform.localScale = new Vector3(m_cloudScale, m_cloudScale, m_cloudScale);
                
                // [수정됨] CS1612 에러 해결: 임시 변수에 할당 후 수정
                foreach (var ps in m_currentCloudFx.GetComponentsInChildren<ParticleSystem>())
                {
                    var mainModule = ps.main;
                    mainModule.scalingMode = ParticleSystemScalingMode.Hierarchy;
                }
            }
        }
        else
        {
            if (m_currentRainFx != null) { Destroy(m_currentRainFx); m_currentRainFx = null; }
            if (m_currentCloudFx != null) { Destroy(m_currentCloudFx); m_currentCloudFx = null; }

            if (m_globalLight != null) m_globalLight.intensity = m_originalLightIntensity;
            if (m_flashCoroutine != null) StopCoroutine(m_flashCoroutine);
        }
    }

    public void PlayStrikeEffects(Vector3 position)
    {
        if (m_strikeParticlePrefab != null)
        {
            Destroy(Instantiate(m_strikeParticlePrefab, position, Quaternion.identity), 3f); 
        }

        if (m_globalLight != null && gameObject.activeInHierarchy)
        {
            if (m_flashCoroutine != null) StopCoroutine(m_flashCoroutine);
            m_flashCoroutine = StartCoroutine(FlashRoutine());
        }
    }

    private IEnumerator FlashRoutine()
    {
        m_globalLight.intensity = m_maxFlashIntensity;
        yield return new WaitForSeconds(m_flashDuration);
        m_globalLight.intensity = m_originalLightIntensity;
        m_flashCoroutine = null;
    }
}