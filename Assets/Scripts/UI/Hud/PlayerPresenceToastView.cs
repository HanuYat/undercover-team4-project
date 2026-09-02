using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 세션 입퇴장 알림 (#598) — "누가 들어왔다 / 나갔다"를 토스트로 띄운다.
///
/// AppBootstrap에 붙는 상주 컴포넌트다. 로비 패널이 들고 있던 책임을 여기로 옮긴 이유는 그 패널이
/// 로비에만 있어 상점·게임맵을 덮지 못하기 때문이다. 명부(<see cref="SessionRoster"/>)도 같은 이유로
/// 세션 상주가 됐다.
///
/// 판단은 하지 않는다 — 누구에게 보일지는 명부가 정하고(본인 제외), 여기서는 내 화면에 띄우기만 한다.
/// 토스트가 없는 씬(타이틀)에서는 App.UI.Toast가 null이라 무동작한다.
/// </summary>
public class PlayerPresenceToastView : MonoBehaviour
{
    [Tooltip("입장 알림 — Session.Presence.Joined ({0}=닉네임)")]
    [SerializeField] private LocalizedString m_joinedToast;

    [Tooltip("퇴장 알림 — Session.Presence.Left ({0}=닉네임). 조사(이/가)를 피하려 \"님이\"로 적는다 (#598)")]
    [SerializeField] private LocalizedString m_leftToast;

    [Tooltip("알림이 떠 있는 시간(초)")]
    [Min(0.5f)]
    [SerializeField] private float m_toastSeconds = 3f;

    [Tooltip("알림 배경색 — 검거 알림(ArrestNoticeBroadcaster)과 같은 공용 팔레트를 쓴다 (#951)")]
    [SerializeField] private UiColorPalette m_palette;

    [Tooltip("알림 배경 채움 투명도")]
    [Range(0f, 1f)]
    [SerializeField] private float m_toastAlpha = 0.95f;

    // 구독해 둔 명부. 세션 상주라 대개 이미 스폰돼 있지만, 세션 시작 전(타이틀)이나 원격 클라의 늦은
    // 스폰 동기화에서는 아직 없다 — 잡힐 때까지 기다린다 (TeamFundBalanceView와 같은 방식).
    private SessionRoster m_roster;

    private void OnDisable() => Unbind();

    // 아직 못 잡았을 때만 도는 폴링 — 잡는 즉시 이벤트 구동으로 넘어간다.
    // 세션이 끝나면 명부가 사라져 참조가 죽으므로 다음 세션에서 다시 잡는다.
    private void Update()
    {
        if (m_roster == null)
            TryBind();
    }

    private void TryBind()
    {
        SessionRoster roster = App.Game.Roster;
        if (roster == null)
            return;

        m_roster = roster;
        m_roster.OnPlayerJoined += HandlePlayerJoined;
        m_roster.OnPlayerLeft += HandlePlayerLeft;
    }

    private void Unbind()
    {
        // ?. 금지 — 파괴된 Unity 오브젝트 fake null 우회 방지 (HqPanelView 관례)
        if (m_roster != null)
        {
            m_roster.OnPlayerJoined -= HandlePlayerJoined;
            m_roster.OnPlayerLeft -= HandlePlayerLeft;
        }
        m_roster = null;
    }

    // 입장은 성공색, 퇴장은 중립색 — 나가는 것은 사고가 아니라 그냥 일어난 일이라 실패색을 쓰지 않는다.
    private void HandlePlayerJoined(string nickname) =>
        ShowToast(m_joinedToast, nickname, Tone(true));

    private void HandlePlayerLeft(string nickname) =>
        ShowToast(m_leftToast, nickname, Tone(false));

    // 배선이 빠지면 프리팹 기본색으로 뜬다 — 색만 빠질 뿐 알림 자체는 살아 있어야 한다.
    private Color? Tone(bool joined)
    {
        if (m_palette == null)
            return null;

        return UiColorPalette.WithAlpha(
            joined ? m_palette.Positive : m_palette.Neutral,
            m_toastAlpha
        );
    }

    private void ShowToast(LocalizedString message, string nickname, Color? tone)
    {
        if (string.IsNullOrEmpty(nickname))
            return; // 닉네임 보고가 닿기 전에 끊긴 경우 — 알릴 이름이 없으면 조용히 넘어간다

        if (message == null || message.IsEmpty)
        {
            Debug.LogWarning("PlayerPresenceToastView: 알림 문구가 연결되지 않았습니다.", this);
            return;
        }

        // 인자를 먼저 넣는다 — 무전 키 안내(LobbyRosterPanel.RefreshRadioKey)와 같은 순서다.
        message.Arguments = new object[] { nickname };

        // 토스트가 없는 환경(타이틀·데디케이티드 서버)에선 null이라 무동작 — PlayerTheftView와 같은 방침
        App.UI.Toast?.Show(message, m_toastSeconds, tone);
    }
}
