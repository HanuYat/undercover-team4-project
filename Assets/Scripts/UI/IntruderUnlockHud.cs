using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// [임시] 침입자 해제 시도 표시 — 자물쇠를 따는 침입자 머리 위에 "해방 시도 중" 라벨과
/// 시계방향으로 차오르는 <b>원형(링) 진행 게이지</b>를 그린다. (#311)
/// 대응 구간(<see cref="JailbreakEvent"/>의 unlockSeconds)이 얼마나 남았는지 한눈에 보여
/// 팀이 막으러 갈 판단을 하게 한다 — 링이 다 차면 자물쇠가 열린다.
///
/// <see cref="JailLock"/>의 해제 시도 ClientRpc를 받은 각 피어가 침입자 NPC에 런타임으로 붙인다 —
/// 프리팹 배선이 없고 순수 로컬 표시라 네트워크에 실리는 것도 없다. 링은 스크린 스페이스 오버레이
/// 캔버스에 그리고(uGUI Image Radial360 — 원형 필의 표준 경로), 링 스프라이트는 NpcPenaltyMark처럼
/// 절차 생성한다 [임시 — 아트 교체 지점]. 진행도는 수신 시점부터의 로컬 카운트다운(10초 게이지라
/// 전송 지연 오차는 무시 가능). 동기화된 상태가 Intruding을 벗어나면(제압됨·해제 완료 후 도주)
/// 스스로 정리된다. 정식 상호작용 UI(#65 계열)로 대체 예정.
/// </summary>
public class IntruderUnlockHud : MonoBehaviour
{
    private const string k_label = "해방 시도 중";
    private const float k_headHeight = 2.2f;      // 게이지를 띄울 머리 위 높이(m) — NpcSubdueGaugeHud와 동일
    private const float k_maxDrawDistance = 40f;  // 카메라에서 이보다 멀면 생략 — 본부 개입 판단용이라 제압 게이지보다 넉넉히
    private const float k_ringSize = 56f;         // 링 지름(px)
    private const int k_fontSize = 14;

    // 링 스프라이트 절차 생성 파라미터 — 128px 텍스처 기준 반지름(px)
    private const int k_texSize = 128;
    private const float k_ringInnerRadius = 46f;
    private const float k_ringOuterRadius = 58f;

    private static Sprite s_ringSprite; // 절차 생성 링 — 전 인스턴스 공유

    private NpcController m_controller;
    private GameObject m_canvasRoot;   // 오버레이 캔버스(자식) — 파괴 시 함께 정리
    private RectTransform m_ring;      // 스크린 좌표로 옮겨 다니는 게이지 루트
    private Image m_fill;
    private float m_endTime;
    private float m_duration;

    /// <summary>
    /// 해제 시도 표시 시작 — 침입자에 이 컴포넌트를 붙이고(없으면 생성) 카운트다운을 건다.
    /// 각 피어가 자기 화면용으로 호출한다 (JailLock의 해제 시도 전파 수신 지점).
    /// </summary>
    public static void Begin(NpcController npc, float seconds)
    {
        if (npc == null || seconds <= 0f)
            return;

        IntruderUnlockHud hud = npc.GetComponent<IntruderUnlockHud>();
        if (hud == null)
            hud = npc.gameObject.AddComponent<IntruderUnlockHud>();

        hud.m_endTime = Time.time + seconds;
        hud.m_duration = seconds;
    }

    private void Awake()
    {
        m_controller = GetComponent<NpcController>();
        BuildCanvas();
    }

    private void Update()
    {
        // 해제 채널링이 끝났거나(시간 만료 — 자물쇠 개방) 침입 상태를 벗어났으면(제압·도주) 표시를 접는다.
        // 상태는 동기화 값이라 모든 피어에서 같은 시점에 정리된다.
        if (Time.time >= m_endTime || m_controller == null || m_controller.CurrentState != NpcState.Intruding)
        {
            Destroy(this);
            return;
        }

        Camera cam = NpcSubdueGaugeHud.ViewCamera;
        if (cam == null)
        {
            m_ring.gameObject.SetActive(false);
            return;
        }

        // 머리 위 월드 좌표 → 스크린 좌표. 카메라 뒤·원거리면 숨긴다 (오버레이 캔버스라 픽셀 좌표 그대로 쓴다)
        Vector3 screen = cam.WorldToScreenPoint(transform.position + Vector3.up * k_headHeight);
        bool visible = screen.z > 0f && screen.z <= k_maxDrawDistance;
        m_ring.gameObject.SetActive(visible);
        if (!visible)
            return;

        m_ring.position = new Vector3(screen.x, screen.y, 0f);

        // 위협이 차오르는 방향(주황→빨강) — 시계방향 라디얼 필. 다 차면 자물쇠가 열린다
        float fill = Mathf.Clamp01(1f - (m_endTime - Time.time) / m_duration);
        m_fill.fillAmount = fill;
        m_fill.color = Color.Lerp(new Color(1f, 0.6f, 0.15f, 0.95f), new Color(0.95f, 0.2f, 0.15f, 0.95f), fill);
    }

    private void OnDestroy()
    {
        if (m_canvasRoot != null)
            Destroy(m_canvasRoot);
    }

    // 오버레이 캔버스 + 배경 링/필 링/라벨을 코드로 조립한다 — 씬·프리팹 배선 없음
    private void BuildCanvas()
    {
        m_canvasRoot = new GameObject("IntruderUnlockHudCanvas");
        m_canvasRoot.transform.SetParent(transform, false);
        Canvas canvas = m_canvasRoot.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100; // 다른 HUD 위에 오도록 — 임시 OnGUI HUD들과는 레이어가 달라 간섭 없음

        m_ring = CreateRect("Ring", m_canvasRoot.transform, new Vector2(k_ringSize, k_ringSize));

        // 배경 링 — 전체 윤곽을 어둡게 깔아 '어디까지 차야 하는지'를 보여준다
        Image background = CreateRect("Background", m_ring, new Vector2(k_ringSize, k_ringSize))
            .gameObject.AddComponent<Image>();
        background.sprite = GetRingSprite();
        background.color = new Color(0f, 0f, 0f, 0.55f);

        // 필 링 — 12시 방향에서 시계방향으로 차오른다
        m_fill = CreateRect("Fill", m_ring, new Vector2(k_ringSize, k_ringSize))
            .gameObject.AddComponent<Image>();
        m_fill.sprite = GetRingSprite();
        m_fill.type = Image.Type.Filled;
        m_fill.fillMethod = Image.FillMethod.Radial360;
        m_fill.fillOrigin = (int)Image.Origin360.Top;
        m_fill.fillClockwise = true;
        m_fill.fillAmount = 0f;

        // 라벨 — 링 위에 경고색으로
        Text label = CreateRect("Label", m_ring, new Vector2(160f, k_fontSize + 8f))
            .gameObject.AddComponent<Text>();
        label.rectTransform.anchoredPosition = new Vector2(0f, k_ringSize * 0.5f + 14f);
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.fontSize = k_fontSize;
        label.fontStyle = FontStyle.Bold;
        label.alignment = TextAnchor.MiddleCenter;
        label.color = new Color(1f, 0.55f, 0.2f, 1f);
        label.text = k_label;
        Outline outline = label.gameObject.AddComponent<Outline>(); // 밝은 배경 위 가독성
        outline.effectColor = new Color(0f, 0f, 0f, 0.8f);
        outline.effectDistance = new Vector2(1f, -1f);
    }

    private static RectTransform CreateRect(string name, Transform parent, Vector2 size)
    {
        var go = new GameObject(name);
        var rect = go.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.sizeDelta = size;
        return rect;
    }

    // 흰 링을 절차 생성한다 — 색은 Image.color로 입힌다. [임시 — 아트 교체 지점]
    private static Sprite GetRingSprite()
    {
        if (s_ringSprite != null)
            return s_ringSprite;

        const float soft = 1.5f; // 가장자리 안티에일리어싱 폭(px)

        var tex = new Texture2D(k_texSize, k_texSize, TextureFormat.RGBA32, false);
        var pixels = new Color32[k_texSize * k_texSize];

        for (int y = 0; y < k_texSize; y++)
        {
            for (int x = 0; x < k_texSize; x++)
            {
                float dx = x - (k_texSize - 1) * 0.5f;
                float dy = y - (k_texSize - 1) * 0.5f;
                float r = Mathf.Sqrt(dx * dx + dy * dy);

                float alpha =
                    Mathf.InverseLerp(k_ringInnerRadius - soft, k_ringInnerRadius + soft, r)
                    * (1f - Mathf.InverseLerp(k_ringOuterRadius - soft, k_ringOuterRadius + soft, r));

                pixels[y * k_texSize + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
            }
        }

        tex.SetPixels32(pixels);
        tex.Apply(false, true); // 더 안 고칠 텍스처 — CPU 사본을 버려 메모리 절약

        s_ringSprite = Sprite.Create(tex, new Rect(0, 0, k_texSize, k_texSize), new Vector2(0.5f, 0.5f), k_texSize);
        return s_ringSprite;
    }
}
