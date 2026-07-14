using Unity.Netcode;
using UnityEngine;

/// <summary>
/// [임시] 저항(Attack) NPC의 제압 게이지 표시 — 머리 위 게이지 바. (#76/#79)
/// 저항 중일 때만 모든 클라 화면에 그려지며, 홀드 타격으로 게이지가 깎이는 진행도를 보여준다.
/// 값은 <see cref="NpcController.SubdueGauge"/>·<see cref="NpcController.CurrentState"/>(동기화 값)를 읽으므로
/// 서버·클라 구분 없이 각자 화면에 그린다.
/// NetworkBootstrap·PlayerReviveHud의 임시 OnGUI 관례를 따른다 — 정식 상호작용 UI(#65 계열)로 대체 예정.
/// </summary>
[RequireComponent(typeof(NpcController))]
public class NpcSubdueGaugeHud : MonoBehaviour
{
    [Tooltip("게이지 바를 띄울 머리 위 높이(m)")]
    [SerializeField] private float m_headHeight = 2.2f;

    [Tooltip("카메라에서 이 거리(m)보다 멀면 그리지 않는다")]
    [SerializeField] private float m_maxDrawDistance = 20f;

    private const float k_barWidth = 84f;
    private const float k_barHeight = 10f;

    // 로컬 플레이어 시점 카메라 캐시 — 모든 NPC가 같은 '내 화면' 카메라를 공유하므로 static.
    // Camera.main은 씬 오버뷰(CCTV용) 카메라를 가리킬 수 있어 쓰지 않는다.
    private static Camera s_viewCamera;

    private NpcController m_controller;

    private void Awake()
    {
        m_controller = GetComponent<NpcController>();
    }

    // 내 화면을 렌더하는 로컬 플레이어 카메라를 찾는다(파괴되면 재탐색). 없으면 Camera.main 폴백.
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

    private void OnGUI()
    {
        // 저항 상태에서만 표시 — 배회·도주 NPC에는 제압 게이지 개념이 없다
        if (m_controller.CurrentState != NpcState.Attack)
            return;

        Camera cam = ViewCamera;
        if (cam == null)
            return;

        // 머리 위 월드 좌표 → 스크린 좌표 (z = 카메라 앞쪽 거리)
        Vector3 screen = cam.WorldToScreenPoint(transform.position + Vector3.up * m_headHeight);
        if (screen.z <= 0f || screen.z > m_maxDrawDistance)
            return; // 카메라 뒤이거나 너무 멀면 생략

        float fill = m_controller.SubdueGaugeMax > 0f
            ? Mathf.Clamp01(m_controller.SubdueGauge / m_controller.SubdueGaugeMax)
            : 0f;

        // 스크린 좌표(하단 원점) → GUI 좌표(상단 원점)로 뒤집는다
        float x = screen.x - k_barWidth * 0.5f;
        float y = Screen.height - screen.y - k_barHeight - 4f;

        DrawGauge(new Rect(x, y, k_barWidth, k_barHeight), fill);
    }

    // 남은 저항력(게이지)을 바로 그린다 — 가득=위협(빨강), 제압에 가까울수록 줄며 초록에 가까워진다.
    private static void DrawGauge(Rect rect, float fill)
    {
        Color prev = GUI.color;

        // 배경(어두운 바)
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);

        // 채워진 부분 — 남은 게이지 비율만큼 좌측부터 채움
        Rect inner = new Rect(rect.x + 1f, rect.y + 1f, (rect.width - 2f) * fill, rect.height - 2f);
        GUI.color = Color.Lerp(new Color(0.25f, 0.85f, 0.3f, 0.95f),  // 거의 제압됨
                               new Color(0.9f, 0.25f, 0.2f, 0.95f),   // 아직 강함
                               fill);
        GUI.DrawTexture(inner, Texture2D.whiteTexture);

        GUI.color = prev;
    }
}
