using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// 비·번개 표현 계층 — <see cref="LightningEvent"/>의 on/off와 낙뢰 순간을 받아 화면에 낸다. (#227)
/// 판정·피해는 이벤트가 하고 여기는 보이는 것만 맡는다 (DeviceBlackoutView와 같은 가름).
///
/// 하늘에 매다는 방식은 <see cref="WeatherSkyRig"/>가 쥔다 — 카메라 위치만 따라가고 회전은 물려받지
/// 않아 비가 월드 -Y로 떨어지며, 구름과 비가 한 리그에 매달려 <b>구름 아래에서 내리는</b> 그림이 된다.
///
/// <b>싱글톤을 쓰지 않는다</b> — 예전에는 이벤트가 <c>LightningView.Instance</c>로 뷰를 직접 불렀는데,
/// 새 <c>static Instance</c>는 아키텍처 규칙 R2가 금지한다. 지금은 <see cref="LightningEvent.OnStrike"/>를
/// 구독한다: 뷰가 없어도 이벤트가 그대로 돌고, 구독자를 더 붙일 수도 있다.
/// </summary>
public class LightningView : MonoBehaviour
{
    [Header("FX 프리팹")]
    [Tooltip("낙뢰 지점에 터지는 파티클")]
    [SerializeField] private GameObject m_strikeParticlePrefab;

    [Tooltip("내리는 비 파티클")]
    [SerializeField] private GameObject m_rainParticlePrefab;

    [Tooltip("머리 위 먹구름 파티클")]
    [SerializeField] private GameObject m_cloudPrefab;

    [Header("하늘 배치")]
    [Tooltip("카메라 기준 먹구름층 높이(m) — 낮으면 하늘이 아니라 '거리 위 연기'로 보인다")]
    [SerializeField] private float m_cloudHeight = 80f;

    [Tooltip("비가 방출되는 높이(m) — 구름 높이와 따로 준다. 너무 높으면 화면에 닿기까지 오래 걸린다")]
    [SerializeField] private float m_precipitationHeight = 14f;

    [Tooltip("켜면 비를 보는 쪽에만 뿌린다 — 시야 앞 한 덩이로 해결한다(수평 방향만 따라가므로 낙하는 그대로 아래)")]
    [SerializeField] private bool m_rainFollowsView = true;

    [Tooltip("시야 앞으로 밀 거리(m)")]
    [Min(0f)]
    [SerializeField] private float m_rainForwardOffset = 8f;

    [Header("크기")]
    [SerializeField] private float m_rainScale = 2f;
    [SerializeField] private float m_cloudScale = 10f;

    [Header("먹구름")]
    [Tooltip(
        "구름 파티클을 실제로 띄울지. <b>기본은 끔</b> — 하늘을 덮으려면 파티클 시스템이 수십 개 필요해 "
            + "프레임이 크게 떨어진다. 끄면 먹구름은 '하늘이 어두워지는 것'으로만 표현된다"
    )]
    [SerializeField] private bool m_useCloudFx;

    [Header("먹구름 파티클 (m_useCloudFx가 켜졌을 때만)")]
    [Tooltip("구름을 한 변 몇 장으로 깔 것인가 — 9면 81장")]
    [Range(1, 11)]
    [SerializeField] private int m_cloudTiles = 9;

    [Tooltip("구름 장 사이 간격(m) — 구름 크기보다 좁게 둬야 겹쳐서 틈이 안 보인다")]
    [Min(1f)]
    [SerializeField] private float m_cloudSpacing = 70f;

    [Header("비 진하기")]
    [Tooltip("빗줄기 크기 배율")]
    [Min(0.1f)]
    [SerializeField] private float m_rainSizeBoost = 1.5f;

    [Tooltip("비 방출량 배율 — 상한(maxParticles)도 함께 올라간다")]
    [Min(1f)]
    [SerializeField] private float m_rainRateBoost = 4f;

    [Header("먹구름 — 어두워지기")]
    [Tooltip(
        "비가 오는 동안 어둡게 할 라이트. <b>비워 두는 것이 기본이다</b> — 비어 있으면 씬의 태양"
            + "(Lighting 창의 Sun Source = RenderSettings.sun)을 런타임에 찾아 쓴다.\n\n"
            + "이 컴포넌트는 프리팹(SuddenEvents)에 붙으므로 씬 오브젝트를 직렬화로 물릴 수 없다 — "
            + "씬마다 다른 라이트를 가리켜야 하는데 프리팹은 씬을 모른다. 특정 라이트를 강제하고 싶을 때만 채운다"
    )]
    [SerializeField] private Light m_globalLight;

    [Tooltip("먹구름이 낀 동안의 밝기 배율 — 1이면 그대로. 구름이 꼈는데 한낮처럼 밝으면 구름이 안 읽힌다")]
    [Range(0.1f, 1f)]
    [SerializeField] private float m_overcastIntensityScale = 0.55f;

    [Tooltip("밝아지고 어두워지는 데 걸리는 시간(초) — 즉시 바꾸면 조명이 툭 튄다")]
    [Min(0f)]
    [SerializeField] private float m_overcastFadeSeconds = 2.5f;

    [Header("낙뢰 섬광")]
    [Tooltip("섬광 최대 밝기 — 원래 밝기가 아니라 절대값이다")]
    [SerializeField] private float m_maxFlashIntensity = 3f;

    [Tooltip("섬광 한 번의 전체 길이(초). 이 안에서 밝아짐-죽음-더 밝아짐-감쇠가 일어난다")]
    [Min(0.05f)]
    [SerializeField] private float m_flashDuration = 0.45f;

    [Tooltip("낙뢰 파티클이 남아 있는 시간(초)")]
    [Min(0.1f)]
    [SerializeField] private float m_strikeFxSeconds = 3f;

    [Header("실내 차단")]
    [Tooltip(
        "머리 위로 이 거리(m) 안에 지붕이 있으면 비를 그친다 — 0이면 실내에서도 내린다.\n\n"
            + "건물 높이보다 넉넉히 잡을 것. 판정은 방출 지점의 수평 위치에서 위로 쏘는 레이 하나다"
    )]
    [Min(0f)]
    [SerializeField] private float m_shelterProbeHeight = 25f;

    [Tooltip("하늘을 막는 것으로 칠 레이어 — 건물은 Default다")]
    [SerializeField] private LayerMask m_shelterMask = 1;

    [Tooltip("문을 드나들 때 비가 여닫히는 시간(초) — 0이면 툭 끊긴다")]
    [Min(0f)]
    [SerializeField] private float m_shelterFadeSeconds = 0.35f;

    [Header("그치는 연출")]
    [Tooltip("비가 그칠 때 방출만 멈추고 이 시간(초) 뒤에 리그를 없앤다 — 공중의 비가 끝까지 떨어지게")]
    [Min(0f)]
    [SerializeField] private float m_stopFadeSeconds = 4f;

    private LightningEvent m_lightningEvent;
    private WeatherSkyRig m_rig;
    private bool m_overcastPushed; // 내가 먹구름을 요청해 둔 상태인가 — Push/Pop 짝을 뷰가 직접 센다

    // 이벤트가 켜지기 전의 밝기 — 되돌릴 기준값. 섬광·먹구름이 모두 이 값을 기준으로 움직인다.
    private float m_baseIntensity;
    private bool m_hasBaseIntensity;

    // 돌고 있는 섬광을 끊는 손잡이 — 낙뢰가 겹치면 앞엣것을 끊고 다시 친다.
    private CancellationTokenSource m_flashCts;

    private void Start()
    {
        m_lightningEvent = App.Game.SuddenEvent?.GetEvent<LightningEvent>();
        if (m_lightningEvent == null)
            return;

        CaptureBaseIntensity();

        m_lightningEvent.OnLightningChanged += HandleLightningChanged;
        m_lightningEvent.OnStrike += HandleStrike;
        HandleLightningChanged(m_lightningEvent.IsLightningActive); // 늦게 들어온 클라 — 이미 오는 중이면 지금 띄운다
    }

    private void OnDestroy()
    {
        if (m_lightningEvent != null)
        {
            m_lightningEvent.OnLightningChanged -= HandleLightningChanged;
            m_lightningEvent.OnStrike -= HandleStrike;
        }

        // 밝기는 되돌려 놓고 떠난다 — 뷰가 사라졌다고 씬이 어두운 채로 남으면 안 된다
        // 비가 켜진 채 파괴되면(호스트 종료·씬 전환 강제 정리) 요청이 영원히 남는다 — 여기서 짝을 맞춘다
        PopOvercast(0f);

        StopFlash(); // 되돌리기 전에 끊는다 — 돌던 섬광이 복원한 밝기를 덮어쓰지 않게
        RestoreIntensity();
    }

    // 먹구름 요청을 뺀다 — 요청해 둔 적이 있을 때만. 짝 없는 Pop은 같이 오는 눈의 어둠까지 걷어 버린다.
    private void PopOvercast(float fadeSeconds)
    {
        if (!m_overcastPushed)
            return;

        m_overcastPushed = false;
        WeatherOvercast.Pop(fadeSeconds);
    }

    // 라이트를 나중에 배선해도 기준값을 놓치지 않게, 처음 쓸 때 한 번 잡는다
    private void CaptureBaseIntensity()
    {
        if (m_hasBaseIntensity)
            return;

        // 인스펙터에서 비워 뒀으면 씬의 태양을 찾는다 — 프리팹은 씬 라이트를 직렬화로 물릴 수 없다.
        // RenderSettings.sun은 Lighting 창의 Sun Source다(비어 있으면 Unity가 가장 밝은 Directional을 쓴다).
        if (m_globalLight == null)
            m_globalLight = RenderSettings.sun;

        if (m_globalLight == null)
            return; // 태양이 없는 씬 — 어두워지기·섬광은 건너뛰고 파티클만 낸다

        m_baseIntensity = m_globalLight.intensity;
        m_hasBaseIntensity = true;
    }

    private void RestoreIntensity()
    {
        if (m_globalLight != null && m_hasBaseIntensity)
            m_globalLight.intensity = m_baseIntensity;
    }

    private void HandleLightningChanged(bool isActive)
    {
        if (isActive)
            ShowRain();
        else
            HideRain();
    }

    private void ShowRain()
    {
        CaptureBaseIntensity();

        if (m_rig != null)
            return; // 이미 오는 중 — 중복 통보 무시

        // 카메라가 아직 없어도 만든다 — 리그가 매 프레임 카메라를 다시 본다.
        // 예전에는 여기서 Camera.main이 null이면 return해, 그 이벤트 내내 비가 안 내렸다.
        m_rig = WeatherSkyRig.Create("WeatherSky_Rain", m_cloudHeight, m_precipitationHeight);
        m_rig.SetCloudSnap(m_cloudSpacing); // 하늘은 월드에 고정 — 구름 한 덩이가 따라오는 그림을 막는다
        m_rig.SetPrecipitationFacesView(m_rainFollowsView, m_rainForwardOffset); // 비는 보는 쪽에만
        m_rig.SetShelterProbe(m_shelterMask, m_shelterProbeHeight, m_shelterFadeSeconds); // 지붕 아래에선 그친다

        // 구름 파티클은 기본으로 띄우지 않는다 — 프레임 부담이 커서, 먹구름은 밝기로 표현한다
        if (m_useCloudFx && m_cloudPrefab != null)
            WeatherSkyRig.AttachTiled(m_cloudPrefab, m_rig.CloudAnchor, m_cloudScale, m_cloudTiles, m_cloudSpacing);

        if (m_rainParticlePrefab != null)
        {
            GameObject rain = Instantiate(m_rainParticlePrefab);
            WeatherSkyRig.Attach(rain, m_rig.PrecipitationAnchor, m_rainScale);
            // 낙하 속도·방출 볼륨은 1 — 비의 튜닝은 지금 것을 그대로 둔다. 눈에만 올린 값이라
            // (SnowView) 여기도 필요해지면 같은 인자를 노출하면 된다.
            WeatherSkyRig.Boost(rain, m_rainSizeBoost, m_rainRateBoost, 1f, 1f);
        }

        if (!m_overcastPushed)
        {
            m_overcastPushed = true;
            WeatherOvercast.Push(m_overcastIntensityScale, m_overcastFadeSeconds);
        }
    }

    private void HideRain()
    {
        if (m_rig != null)
        {
            m_rig.StopAndDispose(m_stopFadeSeconds);
            m_rig = null;
        }

        StopFlash();
        PopOvercast(m_overcastFadeSeconds);
    }

    // 낙뢰 — 지점에 파티클을 터뜨리고 화면을 번쩍인다. 전 피어에서 불린다.
    private void HandleStrike(Vector3 position)
    {
        if (m_strikeParticlePrefab != null)
            Destroy(Instantiate(m_strikeParticlePrefab, position, Quaternion.identity), m_strikeFxSeconds);

        if (m_globalLight == null || !gameObject.activeInHierarchy)
            return;

        CaptureBaseIntensity();

        StopFlash();

        // 파괴 토큰과 묶는다 — 뷰가 죽은 뒤에도 섬광이 씬 라이트를 계속 건드리지 않게
        m_flashCts = CancellationTokenSource.CreateLinkedTokenSource(this.GetCancellationTokenOnDestroy());
        FlashAsync(m_flashCts.Token).Forget();
    }

    /// <summary>
    /// 섬광 — <b>다중 피크</b>다. 예전에는 0.1초 최대 밝기 후 원래 값으로 딱 되돌리는 사각 펄스였는데,
    /// 그건 번개가 아니라 "형광등 깜빡임"으로 읽힌다. 실제 낙뢰는 한 번 터지고 잠깐 죽었다가 더 세게
    /// 터진 뒤 서서히 잦아든다 — 그 봉우리 두 개와 감쇠 꼬리를 곡선으로 만든다.
    /// </summary>
    private async UniTaskVoid FlashAsync(CancellationToken token)
    {
        // 시간 비율 → 밝기 배율. 첫 봉우리(0.35) → 죽음(0.15) → 둘째 봉우리(1.0) → 감쇠.
        float dark = m_hasBaseIntensity ? m_globalLight.intensity : 0f;

        for (float t = 0f; t < m_flashDuration; t += Time.deltaTime)
        {
            float p = t / m_flashDuration;
            float strength = StrikeEnvelope(p);
            m_globalLight.intensity = Mathf.Lerp(dark, m_maxFlashIntensity, strength);
            await UniTask.NextFrame(token);
        }

        // 섬광이 끝나면 지금 국면의 밝기로 돌아간다 — 먹구름 요청이 살아 있으면 그 어둠, 없으면 원래 밝기.
        // 목표를 공유 클래스가 들고 있어 눈·비가 겹쳐 있어도 어긋나지 않는다.
        if (WeatherOvercast.HasSun)
            m_globalLight.intensity = WeatherOvercast.CurrentTarget;
    }

    // 낙뢰 밝기 곡선 (0~1 입력, 0~1 출력) — 봉우리 둘과 감쇠 꼬리.
    private static float StrikeEnvelope(float p)
    {
        if (p < 0.12f)
            return Mathf.InverseLerp(0f, 0.12f, p) * 0.55f; // 첫 봉우리로 치솟음
        if (p < 0.22f)
            return Mathf.Lerp(0.55f, 0.15f, Mathf.InverseLerp(0.12f, 0.22f, p)); // 잠깐 죽는다
        if (p < 0.32f)
            return Mathf.Lerp(0.15f, 1f, Mathf.InverseLerp(0.22f, 0.32f, p)); // 둘째 봉우리 — 가장 밝다
        return Mathf.Lerp(1f, 0f, Mathf.InverseLerp(0.32f, 1f, p)); // 감쇠 꼬리
    }

    private void StopFlash()
    {
        if (m_flashCts == null)
            return;

        m_flashCts.Cancel();
        m_flashCts.Dispose();
        m_flashCts = null;
    }
}
