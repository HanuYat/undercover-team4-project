using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 폭탄 위치 표시 — 화면 안이면 폭탄 위에 마커를, 화면 밖이면 가장자리에 붙여 방향을 가리킨다. (#232, #399)
///
/// <b>추격 폭탄에서는 상시 표시가 맞다.</b> 고정 설치 시절에는 이것이 임시 UI였다 — 본부가 미니맵·CCTV로
/// 위치를 찾아 무전으로 유도하는 것이 협력 루프의 절반이라, 현장이 스스로 위치를 알면 그 협력이 사라졌다.
/// 폭탄이 <b>쫓아오는</b> 지금은 반대다: 어디서 오는지 안 보이면 달아날 방향을 고를 수 없어 대응 자체가
/// 성립하지 않는다. 남은 시간·거리는 "지금 뛸 것인가, 밀어낼 것인가"를 고르는 정보다.
///
/// 순수 표현이다. 위치·남은 시간은 <see cref="BombDevice"/>의 동기화 상태에서 읽고 상태를 바꾸지 않는다.
/// 폭탄 프리팹에 붙어 있어 폭탄이 스폰·디스폰될 때 함께 생기고 사라진다 — 씬 배선이 필요 없다.
/// [임시] 아직 OnGUI로 그린다 — 캔버스 이전은 #493에 남아 있다.
/// </summary>
[RequireComponent(typeof(BombDevice))]
public class BombLocatorHud : MonoBehaviour
{
    [Tooltip("마커를 띄울 폭탄 위쪽 높이(m)")]
    [SerializeField]
    private float m_markerHeight = 0.7f;

    [Tooltip("화면 밖일 때 가장자리에서 띄우는 여백(px)")]
    [SerializeField]
    private float m_edgeMargin = 56f;

    [Tooltip("남은 시간이 이 값(초) 이하면 경고색으로 바뀐다 — BombTimerView와 같은 기준")]
    [SerializeField]
    private float m_warnThreshold = 10f;

    [SerializeField]
    private Color m_normalColor = new Color(1f, 0.72f, 0.15f, 0.95f);

    [SerializeField]
    private Color m_warnColor = new Color(1f, 0.28f, 0.22f, 0.95f);

    private const int k_drawDepth = -90; // 먹통 오버레이 위, 매뉴얼 책(-100) 아래
    private const float k_markerSize = 22f;
    private const float k_labelWidth = 150f;
    private const float k_labelHeight = 20f;

    private BombDevice m_device;
    private GUIStyle m_labelStyle;
    private string m_offScreenArrow = "";

    // 로컬 플레이어 시점 카메라 캐시 — Camera.main은 CCTV 오버뷰 카메라를 가리킬 수 있어 쓰지 않는다.
    private static Camera s_viewCamera;

    private static Camera ViewCamera
    {
        get
        {
            if (s_viewCamera != null)
                return s_viewCamera;

            NetworkManager nm = NetworkManager.Singleton;
            if (nm != null && nm.LocalClient != null && nm.LocalClient.PlayerObject != null)
                s_viewCamera = nm.LocalClient.PlayerObject.GetComponentInChildren<Camera>();

            if (s_viewCamera == null)
                s_viewCamera = Camera.main;

            return s_viewCamera;
        }
    }

    private void Awake()
    {
        m_device = GetComponent<BombDevice>();
    }

    private void OnGUI()
    {
        if (m_device == null || !m_device.IsCountingDown)
            return; // 무장 전·폭발 후에는 안내할 것이 없다

        Camera cam = ViewCamera;
        if (cam == null)
            return;

        EnsureStyle();

        float remaining = m_device.RemainingSeconds;
        Color color = remaining <= m_warnThreshold ? m_warnColor : m_normalColor;
        float distance = Vector3.Distance(cam.transform.position, transform.position);

        Vector3 screen = cam.WorldToScreenPoint(transform.position + Vector3.up * m_markerHeight);

        // 카메라 뒤쪽은 WorldToScreenPoint가 좌우·상하 반전된 좌표를 준다 — 뒤집어 바로잡는다.
        bool behind = screen.z <= 0f;
        if (behind)
        {
            screen.x = Screen.width - screen.x;
            screen.y = Screen.height - screen.y;
        }

        // 스크린 좌표(하단 원점) → GUI 좌표(상단 원점)
        Vector2 point = new Vector2(screen.x, Screen.height - screen.y);

        bool onScreen = !behind
            && point.x >= 0f && point.x <= Screen.width
            && point.y >= 0f && point.y <= Screen.height;

        if (!onScreen)
        {
            // 화면 밖 — 중앙에서 대상 방향으로 밀어 가장자리에 붙인다(그 위치 자체가 방향 안내가 된다)
            Vector2 center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            Vector2 dir = point - center;
            if (dir.sqrMagnitude < 0.01f)
                dir = Vector2.up;
            dir.Normalize();

            float halfW = Screen.width * 0.5f - m_edgeMargin;
            float halfH = Screen.height * 0.5f - m_edgeMargin;
            // 사각 경계와 만나는 지점 — 가로/세로 중 먼저 닿는 쪽을 택한다
            float scale = Mathf.Min(
                Mathf.Abs(dir.x) > 0.0001f ? halfW / Mathf.Abs(dir.x) : float.MaxValue,
                Mathf.Abs(dir.y) > 0.0001f ? halfH / Mathf.Abs(dir.y) : float.MaxValue);
            point = center + dir * scale;
            m_offScreenArrow = ArrowFor(dir);
        }

        int previousDepth = GUI.depth;
        GUI.depth = k_drawDepth;
        DrawMarker(point, color, onScreen);
        DrawLabel(point, color, distance, remaining, onScreen);
        GUI.depth = previousDepth;
    }

    // 화면 안이면 네모 조준 마커, 화면 밖이면 채워진 점 — 한눈에 "지금 보이는가"가 구분된다
    private static void DrawMarker(Vector2 point, Color color, bool onScreen)
    {
        Color previous = GUI.color;
        GUI.color = color;

        Rect rect = new Rect(point.x - k_markerSize * 0.5f, point.y - k_markerSize * 0.5f, k_markerSize, k_markerSize);
        if (onScreen)
        {
            const float t = 2f; // 테두리 두께
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, t), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - t, rect.width, t), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.y, t, rect.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.xMax - t, rect.y, t, rect.height), Texture2D.whiteTexture);
        }
        else
        {
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
        }

        GUI.color = previous;
    }

    private void DrawLabel(Vector2 point, Color color, float distance, float remaining, bool onScreen)
    {
        int minutes = (int)(remaining / 60f);
        int seconds = (int)(remaining % 60f);
        string text = string.Format("폭탄 {0:F0}m · {1}:{2:00}{3}",
            distance, minutes, seconds, onScreen ? "" : " " + m_offScreenArrow);

        Rect rect = new Rect(point.x - k_labelWidth * 0.5f, point.y + k_markerSize * 0.5f + 2f, k_labelWidth, k_labelHeight);

        Color previous = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.55f); // 밝은 배경에서도 글자가 읽히도록 어두운 판
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = previous;

        m_labelStyle.normal.textColor = color;
        GUI.Label(rect, text, m_labelStyle);
    }

    /// <summary>
    /// 화면 밖 방향을 8방향 화살표로. GUI 좌표는 아래로 갈수록 y가 커지므로 위/아래가 뒤집힌다.
    /// 폭탄이 등 뒤에 있으면 화면 아래 가장자리로 밀리므로 ↓가 곧 "뒤돌아라"가 된다.
    /// </summary>
    private static string ArrowFor(Vector2 guiDirection)
    {
        // -y를 위로 되돌린 각도(도) — 0=오른쪽, 90=위
        float angle = Mathf.Atan2(-guiDirection.y, guiDirection.x) * Mathf.Rad2Deg;
        if (angle < 0f)
            angle += 360f;

        int sector = Mathf.RoundToInt(angle / 45f) % 8;
        switch (sector)
        {
            case 0: return "→";
            case 1: return "↗";
            case 2: return "↑";
            case 3: return "↖";
            case 4: return "←";
            case 5: return "↙";
            case 6: return "↓";
            default: return "↘";
        }
    }

    private void EnsureStyle()
    {
        if (m_labelStyle != null)
            return;

        m_labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
        };
    }
}
