using UnityEngine;

/// <summary>
/// [임시] 전역 이벤트 팝업(토스트) — 화면 상단 중앙에 경고 문구를 몇 초 띄웠다가 페이드 아웃한다. (#311)
/// 모든 플레이어 화면(현장·본부 공통)에 뜨는 로컬 표시로, 전파는 호출자(예: <see cref="JailLock"/>의
/// 해제 시도 ClientRpc)가 책임진다 — 이 컴포넌트는 "내 화면에 띄우기"만 한다.
/// NetworkBootstrap·NpcSubdueGaugeHud의 임시 OnGUI 관례를 따른다 — 정식 HUD(#43 계열)로 대체 예정.
/// 씬·프리팹 배선 없이 첫 호출 때 스스로 오브젝트를 만든다.
/// </summary>
public class SuddenEventToastHud : MonoBehaviour
{
    private const float k_showSeconds = 4f;   // 총 표시 시간
    private const float k_fadeSeconds = 1f;   // 마지막 이 시간 동안 서서히 사라진다
    private const float k_topMargin = 60f;    // 화면 상단에서의 여백(px)
    private const int k_fontSize = 22;

    private static SuddenEventToastHud s_instance;

    private string m_message;
    private float m_hideTime;
    private GUIStyle m_style; // OnGUI 스레드에서 지연 생성 — 생성자에서 만들면 GUI 밖 접근 예외

    /// <summary>토스트 표시 — 같은 화면에 이미 떠 있으면 문구·시간을 갱신한다(중첩 없이 최신 하나만).</summary>
    public static void Show(string message)
    {
        if (s_instance == null)
        {
            var host = new GameObject("SuddenEventToastHud");
            s_instance = host.AddComponent<SuddenEventToastHud>();
        }

        s_instance.m_message = message;
        s_instance.m_hideTime = Time.time + k_showSeconds;
    }

    private void Update()
    {
        // 표시가 끝나면 오브젝트째 정리한다 — 다음 토스트가 다시 만든다 (씬 전환 시에도 잔재가 없다)
        if (Time.time >= m_hideTime)
            Destroy(gameObject);
    }

    private void OnDestroy()
    {
        if (s_instance == this)
            s_instance = null;
    }

    private void OnGUI()
    {
        float remaining = m_hideTime - Time.time;
        if (remaining <= 0f || string.IsNullOrEmpty(m_message))
            return;

        if (m_style == null)
        {
            m_style = new GUIStyle(GUI.skin.label)
            {
                fontSize = k_fontSize,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = false,
            };
        }

        float alpha = Mathf.Clamp01(remaining / k_fadeSeconds);
        Vector2 size = m_style.CalcSize(new GUIContent(m_message));
        var rect = new Rect(
            (Screen.width - size.x) * 0.5f - 16f,
            k_topMargin,
            size.x + 32f,
            size.y + 16f);

        Color prev = GUI.color;

        // 배경 박스 + 경고색 텍스트 — 어두운 배경이 있어야 밝은 하늘/조명 위에서도 읽힌다
        GUI.color = new Color(0f, 0f, 0f, 0.65f * alpha);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = new Color(1f, 0.55f, 0.2f, alpha);
        GUI.Label(rect, m_message, m_style);

        GUI.color = prev;
    }
}
