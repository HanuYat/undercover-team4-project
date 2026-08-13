using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 돌발 이벤트 발생 알림 (#609) — 이벤트가 터지면 <b>본부·현장 전원</b>의 화면에 이름을 띄운다.
///
/// <b>전파는 이미 깔려 있었고 구독자만 없었다.</b> <see cref="SuddenEventManager.Announce"/>가 서버에서
/// 로컬 발행 + 전 클라 RPC까지 하고 있으므로 여기서는 새 전파 경로를 만들지 않고 그 훅을 받아 내 화면에
/// 띄우기만 한다 — <b>판단은 전부 이벤트 쪽에 있다</b>: 언제 알릴지는
/// <see cref="ISuddenEvent.AnnounceOnBegin"/>이, 무엇을 알릴지는 각 이벤트의 이름이 정한다.
/// 그래서 알림 시점을 스스로 고르는 이벤트(범인 탈출은 침입자가 자물쇠에 손댈 때까지 알리지 않는다)도
/// 같은 자리로 들어오고, 이벤트를 늘려도 이 파일은 그대로다.
///
/// 토스트가 없는 씬(타이틀)·데디케이티드 서버에서는 App.UI.Toast가 null이라 무동작한다 —
/// <see cref="PlayerPresenceToastView"/>와 같은 방침이다.
/// </summary>
public class SuddenEventToastView : MonoBehaviour
{
    [Tooltip("발생 알림 — Hud.Event.Notice ({0}=이벤트 이름)")]
    [SerializeField] private LocalizedString m_noticeMessage;

    [Tooltip("알림이 떠 있는 시간(초)")]
    [Min(0.5f)]
    [SerializeField] private float m_noticeSeconds = 3f;

    [Tooltip("알림 배경색 — 검거 알림(#616)의 초록과 갈라 두는 경고색. 좋은 소식이 아니라는 것이 색으로 먼저 읽혀야 한다")]
    [SerializeField] private Color m_noticeTone = new Color(0.80f, 0.42f, 0.10f, 0.95f);

    // 구독해 둔 매니저. 이벤트 매니저는 맵 씬에 있고 원격 클라에서는 스폰 동기화가 늦어 아직 없을 수
    // 있으므로 잡힐 때까지 기다린다 (PlayerPresenceToastView와 같은 방식). 라운드가 끝나고 맵을 벗어나면
    // 참조가 죽으므로 다음 맵에서 다시 잡는다.
    private SuddenEventManager m_manager;

    private void OnDisable() => Unbind();

    // 아직 못 잡았을 때만 도는 폴링 — 잡는 즉시 이벤트 구동으로 넘어간다.
    private void Update()
    {
        if (m_manager == null)
            TryBind();
    }

    private void TryBind()
    {
        SuddenEventManager manager = App.Game.SuddenEvent;
        if (manager == null)
            return;

        m_manager = manager;
        m_manager.OnEventAnnounced += HandleEventAnnounced;
    }

    private void Unbind()
    {
        // ?. 금지 — 파괴된 Unity 오브젝트 fake null 우회 방지 (HqPanelView 관례)
        if (m_manager != null)
            m_manager.OnEventAnnounced -= HandleEventAnnounced;

        m_manager = null;
    }

    private void HandleEventAnnounced(string displayName)
    {
        if (string.IsNullOrEmpty(displayName))
            return; // 이름 없는 알림은 띄울 것이 없다

        if (m_noticeMessage == null || m_noticeMessage.IsEmpty)
        {
            Debug.LogWarning("SuddenEventToastView: 발생 알림 문구가 연결되지 않았다", this);
            return;
        }

        // 인자를 먼저 넣는다 — PlayerPresenceToastView와 같은 순서다.
        m_noticeMessage.Arguments = new object[] { displayName };

        App.UI.Toast?.Show(m_noticeMessage, m_noticeSeconds, m_noticeTone);
    }
}
