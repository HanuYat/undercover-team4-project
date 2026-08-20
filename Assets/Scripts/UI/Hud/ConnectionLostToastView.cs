using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 세션 드롭 사유 토스트 (#764). 끊기는 시점엔 토스트 자리가 없는 씬일 수 있어 사유를 들고 있다가
/// 타이틀 복귀 + 로딩 종료 후 띄운다 (PlayerPresenceToastView·SuddenEventToastView와 같은 폴링 방식).
/// 매니저가 아니다 — 참조자가 없으므로 App에 올리지 않는다 (R3).
/// </summary>
public class ConnectionLostToastView : MonoBehaviour
{
    private const string k_titleTable = "TitleTable";
    private const string k_prefix = "Title.ConnectionLost.";

    [Tooltip("알림이 떠 있는 시간(초)")]
    [SerializeField]
    private float m_toastSeconds = 5f;

    private SessionManager Session => App.Net.Session;
    private EConnectionLostReason m_pending;

    private void Start()
    {
        if (Session != null)
            Session.OnConnectionLost += HandleConnectionLost;
        else
            Debug.LogWarning(
                "ConnectionLostToastView: SessionManager가 없어 드롭 토스트를 걸 수 없다",
                this
            );
    }

    private void OnDestroy()
    {
        if (Session != null)
            Session.OnConnectionLost -= HandleConnectionLost;
    }

    private void HandleConnectionLost(EConnectionLostReason reason) => m_pending = reason;

    private void Update()
    {
        if (m_pending == EConnectionLostReason.None)
            return;
        if (App.CurrentScene != EScene.Title)
            return;
        if (App.UI.Toast == null)
            return;
        if (App.UI.Loading != null && App.UI.Loading.IsBusy)
            return;

        var message = new LocalizedString(k_titleTable, k_prefix + m_pending);
        App.UI.Toast.Show(message, m_toastSeconds);
        m_pending = EConnectionLostReason.None;
    }
}
