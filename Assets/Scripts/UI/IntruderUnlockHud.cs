using UnityEngine;

/// <summary>
/// [임시] 침입자 해제 시도 표시 — 자물쇠를 따는 침입자 머리 위에 "해방 시도 중" 라벨과
/// 차오르는 진행 게이지를 그린다. (#311)
/// 대응 구간(<see cref="JailbreakEvent"/>의 unlockSeconds)이 얼마나 남았는지 한눈에 보여
/// 팀이 막으러 갈 판단을 하게 한다 — 게이지가 다 차면 자물쇠가 열린다.
///
/// <see cref="JailLock"/>의 해제 시도 ClientRpc를 받은 각 피어가 침입자 NPC에 런타임으로 붙인다 —
/// 프리팹 배선이 없고 순수 로컬 표시라 네트워크에 실리는 것도 없다. 진행도는 수신 시점부터의
/// 로컬 카운트다운(10초 게이지라 전송 지연 오차는 무시 가능). 동기화된 상태가 Intruding을 벗어나면
/// (제압됨·해제 완료 후 도주) 스스로 정리된다.
/// NpcSubdueGaugeHud의 임시 OnGUI 관례를 따른다 — 정식 상호작용 UI(#65 계열)로 대체 예정.
/// </summary>
public class IntruderUnlockHud : MonoBehaviour
{
    private const string k_label = "해방 시도 중";
    private const float k_headHeight = 2.2f;      // 게이지를 띄울 머리 위 높이(m) — NpcSubdueGaugeHud와 동일
    private const float k_maxDrawDistance = 40f;  // 카메라에서 이보다 멀면 생략 — 본부 개입 판단용이라 제압 게이지보다 넉넉히
    private const float k_barWidth = 96f;
    private const float k_barHeight = 10f;
    private const int k_fontSize = 13;

    private NpcController m_controller;
    private float m_endTime;
    private float m_duration;
    private GUIStyle m_style; // OnGUI에서 지연 생성

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
    }

    private void Update()
    {
        // 해제 채널링이 끝났거나(시간 만료 — 자물쇠 개방) 침입 상태를 벗어났으면(제압·도주) 표시를 접는다.
        // 상태는 동기화 값이라 모든 피어에서 같은 시점에 정리된다.
        if (Time.time >= m_endTime || m_controller == null || m_controller.CurrentState != NpcState.Intruding)
            Destroy(this);
    }

    private void OnGUI()
    {
        Camera cam = NpcSubdueGaugeHud.ViewCamera;
        if (cam == null)
            return;

        Vector3 screen = cam.WorldToScreenPoint(transform.position + Vector3.up * k_headHeight);
        if (screen.z <= 0f || screen.z > k_maxDrawDistance)
            return;

        float fill = m_duration > 0f
            ? Mathf.Clamp01(1f - (m_endTime - Time.time) / m_duration)
            : 0f;

        if (m_style == null)
        {
            m_style = new GUIStyle(GUI.skin.label)
            {
                fontSize = k_fontSize,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
        }

        // 스크린 좌표(하단 원점) → GUI 좌표(상단 원점)
        float x = screen.x - k_barWidth * 0.5f;
        float y = Screen.height - screen.y - k_barHeight - 4f;

        Color prev = GUI.color;

        // 라벨 — 게이지 위에 경고색으로. 배경 박스로 가독성 확보
        var labelRect = new Rect(x - 12f, y - k_fontSize - 10f, k_barWidth + 24f, k_fontSize + 8f);
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(labelRect, Texture2D.whiteTexture);
        GUI.color = new Color(1f, 0.55f, 0.2f, 1f);
        GUI.Label(labelRect, k_label, m_style);

        // 게이지 — 위협이 차오르는 방향(주황→빨강). 다 차면 자물쇠가 열린다
        var barRect = new Rect(x, y, k_barWidth, k_barHeight);
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(barRect, Texture2D.whiteTexture);
        var inner = new Rect(barRect.x + 1f, barRect.y + 1f, (barRect.width - 2f) * fill, barRect.height - 2f);
        GUI.color = Color.Lerp(new Color(1f, 0.6f, 0.15f, 0.95f), new Color(0.95f, 0.2f, 0.15f, 0.95f), fill);
        GUI.DrawTexture(inner, Texture2D.whiteTexture);

        GUI.color = prev;
    }
}
