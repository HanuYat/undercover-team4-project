using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 라운드 종료 사유를 모든 플레이어 화면에 텍스트로 보여주는 임시 피드백. (#210)
/// 정식 결과/정산 UI(#43, #107)가 붙기 전까지의 자리표시 구현 — OnGUI로만 그린다.
///
/// 전파 흐름 (라운드 진행이 서버 권위이므로, RoundManager·#56 방침):
///  · 서버·오프라인 — <see cref="RoundManager.OnRoundEnded"/>를 직접 구독해 표시하고,
///    네트워크 세션이면 커스텀 네임드 메시지로 전 클라이언트에 사유를 보낸다.
///    (RoundEndResetter가 셧다운까지 리셋 지연을 두므로 메시지는 그 안에 도착한다)
///  · 클라이언트 — 네임드 메시지를 수신해 동일한 텍스트를 표시한다.
///    NetworkBehaviour가 아니므로 씬 네트워크 구성(NetworkObject·프리팹 등록)을 건드리지 않는다.
/// 표시는 RoundEndResetter의 씬 재로드와 함께 자연히 사라진다.
/// </summary>
public class RoundEndFeedback : MonoBehaviour
{
    private const string k_messageName = "RoundEndFeedback";

    private RoundManager Round => App.Game.Round;

    [Header("표시")]
    [Tooltip("종료 사유 텍스트 폰트 크기")]
    [SerializeField]
    private int m_fontSize = 36;

    private string m_message;
    private bool m_handlerRegistered;

    private void OnEnable()
    {
        // 라운드 종료는 서버·오프라인에서만 발행된다 — 권위 피어는 이 훅으로 즉시 표시·전파한다.
        if (Round != null)
            Round.OnRoundEnded += HandleRoundEnded;
    }

    private void OnDisable()
    {
        if (Round != null)
            Round.OnRoundEnded -= HandleRoundEnded;
    }

    private void Start()
    {
        // 클라이언트 수신 등록 — CustomMessagingManager는 NGO가 시작된 뒤에만 존재하므로
        // 시작 콜백에서 등록한다. (NetworkManager는 자체 Awake에서 Singleton 세팅 — Start 구독은 RoundEndResetter와 동일 패턴)
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null)
            return;

        nm.OnClientStarted += RegisterMessageHandler;
        if (nm.IsListening)
            RegisterMessageHandler();
    }

    private void OnDestroy()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null)
            return;

        nm.OnClientStarted -= RegisterMessageHandler;
        if (m_handlerRegistered && nm.CustomMessagingManager != null)
            nm.CustomMessagingManager.UnregisterNamedMessageHandler(k_messageName);
    }

    private void RegisterMessageHandler()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.CustomMessagingManager == null)
            return;

        nm.CustomMessagingManager.RegisterNamedMessageHandler(k_messageName, ReceiveRoundEnded);
        m_handlerRegistered = true;
    }

    // 서버·오프라인: 종료 즉시 표시하고, 세션 중이면 전 클라이언트에 사유를 전파한다.
    private void HandleRoundEnded(RoundResult result, RoundEndReason reason)
    {
        ShowMessage(reason);
        BroadcastToClients(reason);
    }

    private static void BroadcastToClients(RoundEndReason reason)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null)
            return;

        using FastBufferWriter writer = new FastBufferWriter(sizeof(byte), Allocator.Temp);
        writer.WriteValueSafe((byte)reason);
        nm.CustomMessagingManager.SendNamedMessageToAll(
            k_messageName,
            writer,
            NetworkDelivery.Reliable
        );
    }

    // 클라이언트: 서버가 보낸 종료 사유 수신. (호스트는 자기 메시지를 되받아도 같은 텍스트라 무해)
    private void ReceiveRoundEnded(ulong senderClientId, FastBufferReader reader)
    {
        if (senderClientId != NetworkManager.ServerClientId)
            return;

        reader.ReadValueSafe(out byte reasonByte);
        ShowMessage((RoundEndReason)reasonByte);
    }

    private void ShowMessage(RoundEndReason reason)
    {
        m_message = reason switch
        {
            RoundEndReason.QuotaMet => "라운드 성공!\n진범 검거 할당량 달성",
            RoundEndReason.TimeOver => "게임 오버\n제한시간 초과 — 검거 할당량 미달",
            RoundEndReason.AllPlayersDown => "게임 오버\n플레이어 전원 다운",
            _ => "라운드 종료",
        };
    }

    private void OnGUI()
    {
        if (string.IsNullOrEmpty(m_message))
            return;

        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            fontSize = m_fontSize,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
        };

        // 어떤 배경에서도 읽히도록 검정 그림자를 먼저 깔고 흰 글자를 겹친다
        Rect rect = new Rect(0f, 0f, Screen.width, Screen.height);
        style.normal.textColor = Color.black;
        GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), m_message, style);
        style.normal.textColor = Color.white;
        GUI.Label(rect, m_message, style);
    }
}
