using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// 비·번개 표현 계층 — <see cref="LightningEvent"/>의 on/off와 낙뢰 순간을 받아 화면에 낸다. (#227)
/// 판정·피해는 이벤트가 하고 여기는 보이는 것만 맡는다 (DeviceBlackoutView와 같은 가름).
///
/// 비 자체는 <see cref="PrecipitationScreen"/>이 화면 셰이더로 그린다 (#782) — 이 클래스는 on/off와
/// 먹구름·섬광·낙뢰 FX를 쥔다. 파티클 리그로 하늘에 매달던 방식은 폐기했다.
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

    [Tooltip(
        "떨어질 자리에 미리 띄우는 예고 파티클 (#647) — 벼락이 떨어질 때 치운다.\n\n"
            + "비워 두면 예고 연출이 없다. 판정은 그대로 예고 시간 뒤에 나므로 회피는 성립한다"
    )]
    [SerializeField] private GameObject m_warningPrefab;

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

    private LightningEvent m_lightningEvent;
    private bool m_overcastPushed; // 내가 먹구름을 요청해 둔 상태인가 — Push/Pop 짝을 뷰가 직접 센다

    // 이벤트가 켜지기 전의 밝기 — 되돌릴 기준값. 섬광·먹구름이 모두 이 값을 기준으로 움직인다.
    private float m_baseIntensity;
    private bool m_hasBaseIntensity;

    // 돌고 있는 섬광을 끊는 손잡이 — 낙뢰가 겹치면 앞엣것을 끊고 다시 친다.
    private CancellationTokenSource m_flashCts;

    // 떠 있는 예고 연출 — 벼락이 떨어지거나 비가 그치면 치운다. 한 번에 하나뿐이다 (#647)
    private GameObject m_warningFx;

    private void Start()
    {
        m_lightningEvent = App.Game.SuddenEvent?.GetEvent<LightningEvent>();
        if (m_lightningEvent == null)
            return;

        CaptureBaseIntensity();

        m_lightningEvent.OnLightningChanged += HandleLightningChanged;
        m_lightningEvent.OnStrikeWarning += HandleStrikeWarning;
        m_lightningEvent.OnStrike += HandleStrike;
        HandleLightningChanged(m_lightningEvent.IsLightningActive); // 늦게 들어온 클라 — 이미 오는 중이면 지금 띄운다
    }

    private void OnDestroy()
    {
        if (m_lightningEvent != null)
        {
            m_lightningEvent.OnLightningChanged -= HandleLightningChanged;
            m_lightningEvent.OnStrikeWarning -= HandleStrikeWarning;
            m_lightningEvent.OnStrike -= HandleStrike;
        }

        ClearWarningFx();
        App.Sound?.StopAmbient2D(EAudioClip.RainLoop); // 비가 켜진 채 파괴되면 소리만 남는다

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

    // 비는 화면 셰이더가 그린다 (#782) — 파티클 리그를 만들지 않는다. 섬광·낙뢰 FX·소리는 그대로다.
    private void ShowRain()
    {
        CaptureBaseIntensity();

        Screen?.Show(PrecipitationScreen.EKind.Rain);

        if (!m_overcastPushed)
        {
            m_overcastPushed = true;
            WeatherOvercast.Push(m_overcastIntensityScale, m_overcastFadeSeconds);
        }

        App.Sound?.PlayAmbient2D(EAudioClip.RainLoop); // 비 소리는 실내에서도 들린다 (#647)
    }

    private void HideRain()
    {
        Screen?.Hide();

        StopFlash();
        ClearWarningFx();
        PopOvercast(m_overcastFadeSeconds);

        App.Sound?.StopAmbient2D(EAudioClip.RainLoop);
    }

    // 같은 오브젝트(SuddenEvents)에 붙어 있다 — 인스펙터로 물릴 것이 없다.
    private PrecipitationScreen Screen =>
        m_screen != null ? m_screen : m_screen = GetComponent<PrecipitationScreen>();

    private PrecipitationScreen m_screen;

    // 예고 — 떨어질 자리에 표시를 띄운다. 전 피어에서 불린다. (#647)
    private void HandleStrikeWarning(Vector3 position)
    {
        ClearWarningFx(); // 앞 예고가 남아 있으면 치우고 — 한 번에 하나다

        if (m_warningPrefab != null)
            m_warningFx = Instantiate(m_warningPrefab, position, Quaternion.identity);
    }

    private void ClearWarningFx()
    {
        if (m_warningFx != null)
            Destroy(m_warningFx);

        m_warningFx = null;
    }

    // 낙뢰 — 지점에 파티클을 터뜨리고 화면을 번쩍인다. 전 피어에서 불린다.
    private void HandleStrike(Vector3 position)
    {
        ClearWarningFx();

        // 떨어진 자리에서 난다 — 멀리서도 방향이 읽혀야 어디에 쳤는지 안다 (#647)
        App.Sound?.PlaySfxAt(EAudioClip.LightningStrike, position);

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
