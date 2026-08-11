using System.Collections;
using Unity.Netcode;
using UnityEngine;

// 번개 날씨 표현: 비(Rain) 파티클 활성화 + 간헐적 낙뢰(Strike) 및 섬광
public class LightningView : MonoBehaviour
{
    public static LightningView Instance { get; private set; }

    [Header("Synty Assets (Assets/Imported/Synty/...)")]
    [SerializeField] private GameObject m_strikeParticlePrefab; // FX_LightningStrike_01
    [SerializeField] private GameObject m_rainParticlePrefab;   // FX_Rain_01

    [Header("Screen Flash Settings (Optional)")]
    [SerializeField] private Light m_globalLight;
    [SerializeField] private float m_flashDuration = 0.1f;
    [SerializeField] private float m_maxFlashIntensity = 1.5f;

    private LightningEvent m_lightningEvent;
    private float m_originalLightIntensity;
    private Coroutine m_flashCoroutine;
    
    private GameObject m_currentRainFx;

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

        if (m_lightningEvent == null)
        {
            Debug.LogWarning("[LightningView] LightningEvent not found in SuddenEventManager.");
            enabled = false;
            return;
        }

        if (m_globalLight != null)
        {
            m_originalLightIntensity = m_globalLight.intensity;
        }

        m_lightningEvent.OnLightningChanged += OnLightningChanged;
        ToggleLightningEffects(m_lightningEvent.IsLightningActive);
    }

    private void OnDestroy()
    {
        if (m_lightningEvent != null)
        {
            m_lightningEvent.OnLightningChanged -= OnLightningChanged;
        }
        ToggleLightningEffects(false);
    }

    private void OnLightningChanged(bool isActive)
    {
        ToggleLightningEffects(isActive);
    }

    private void ToggleLightningEffects(bool isActive)
    {
        if (isActive)
        {
            // 1. 비 내리기 시작 (카메라 자식으로 생성)
            if (m_currentRainFx == null && m_rainParticlePrefab != null && Camera.main != null)
            {
                m_currentRainFx = Instantiate(m_rainParticlePrefab, Camera.main.transform);
                m_currentRainFx.transform.localPosition = new Vector3(0f, 5f, 5f);
                m_currentRainFx.GetComponentInChildren<ParticleSystem>()?.Play();
            }
        }
        else
        {
            // 1. 비 멈춤 (파티클 제거)
            if (m_currentRainFx != null)
            {
                Destroy(m_currentRainFx);
                m_currentRainFx = null;
            }

            // 2. 섬광 라이트 원복
            if (m_globalLight != null)
            {
                m_globalLight.intensity = m_originalLightIntensity;
            }
            if (m_flashCoroutine != null) StopCoroutine(m_flashCoroutine);
        }
    }

    // --- 낙뢰 이펙트 (서버 ClientRpc 수신부) ---
    public void PlayStrikeEffects(Vector3 position)
    {
        if (m_strikeParticlePrefab != null)
        {
            GameObject fx = Instantiate(m_strikeParticlePrefab, position, Quaternion.identity);
            Destroy(fx, 3f); 
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
