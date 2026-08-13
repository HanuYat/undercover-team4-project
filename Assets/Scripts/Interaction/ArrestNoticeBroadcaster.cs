using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 검거 성립 전체 알림 (#616) — 현상수배범이 수배 리스트에서 내려가는 순간 전 플레이어(본부·현장)
/// 화면에 "검거 — 남은 N명"을 띄운다.
///
/// 개인 판정 배너(<see cref="ArrestVerdictFeedback"/>, #306)는 검거자 한 명에게 가는 개인 피드백이라
/// 그대로 두고 이쪽을 따로 얹는다 — 자리도 상단 토스트 대 배너 패널로 갈려 겹치지 않는다.
///
/// <b>남은 수만 싣고, 진범 검거만 알린다</b> (#616 결정):
/// <list type="bullet">
///   <item>검거자·대상 이름·보상액까지 보내면 무전으로 알려야 할 것이 자동으로 새어나간다.</item>
///   <item>경범죄는 수배 리스트에 오르지 않고, 시체 인계는 <see cref="WantedListManager"/>가
///   <see cref="ArrestJudge.OnCorpseJudged"/>를 구독하지 않아 항목이 내려가지 않는다 — 알리면
///   남은 수가 그대로인 알림이 된다. 오검거는 진행이 아니고, 개인의 실수는 본인 배너로 족하다.</item>
///   <item>남은 수는 수배 리스트에서 뽑는다 — <see cref="RemainingCriminalsHud"/>(#331)가 "잡은/전체"를
///   같은 곳에서 뽑으므로 따로 세면 두 표시가 어긋난다.</item>
/// </list>
///
/// 전파는 <see cref="SettlementController"/>(#107)의 네임드 메시지 패턴이되 대상을 좁히지 않는다.
/// 문장이 아니라 숫자를 보내고 받는 쪽이 자기 언어로 조립한다 (localization.md 결정 (g)).
/// NetworkBehaviour가 아니라 씬 네트워크 구성을 건드리지 않는다.
/// </summary>
public class ArrestNoticeBroadcaster : MonoBehaviour
{
    private const string k_messageName = "ArrestNotice";
    private const int k_writerSize = 4; // int 하나

    [Tooltip("전 플레이어에게 띄울 문구 — Hud.Arrest.Notice ({0}=남은 수배자 수)")]
    [SerializeField] private LocalizedString m_noticeMessage;

    [Tooltip("토스트가 화면에 머무는 시간(초)")]
    [Min(0f)]
    [SerializeField] private float m_noticeSeconds = 3f;

    private ArrestJudge Judge => App.Game.ArrestJudge;
    private WantedListManager WantedList => App.Game.WantedList;

    private bool m_handlerRegistered;

    private void OnEnable()
    {
        // 판정은 서버·오프라인에서만 발행된다 — 권위 피어가 이 훅으로 전파한다.
        // 판정기가 없는 단독 테스트 씬은 조용히 빠진다 (ArrestVerdictFeedback와 같은 방침).
        if (Judge == null)
            return;

        Judge.OnArrestJudged += HandleArrestJudged;
    }

    private void OnDisable()
    {
        if (Judge == null)
            return;

        Judge.OnArrestJudged -= HandleArrestJudged;
    }

    private void Start()
    {
        // 수신 등록은 NGO가 시작된 뒤에만 가능하다 — CustomMessagingManager가 그때 생긴다.
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

        nm.CustomMessagingManager.RegisterNamedMessageHandler(k_messageName, ReceiveNotice);
        m_handlerRegistered = true;
    }

    // 서버: 수배 리스트가 실제로 줄어드는 판정만 전원에게 알린다.
    private void HandleArrestJudged(ArrestResult result)
    {
        if (result.Verdict != ArrestVerdict.WantedCriminal)
            return;

        // 수배 리스트는 세션에서만 돈다 — 오프라인 단독 Play에 알릴 남은 수가 없는 것은 정상이다.
        WantedListManager wantedList = WantedList;
        if (wantedList == null || !wantedList.IsSpawned)
            return;

        // 이미 내려간 대상이면 남은 수가 그대로다 — 반출한 수감자를 다시 넣는 재판정(#358)이 그 경우.
        // (탈옥 후 재검거는 재등재되므로 여기 걸리지 않는다.)
        if (!IsOnWantedList(wantedList, result.Npc))
            return;

        // 항목을 지우는 것은 같은 이벤트의 다른 구독자(WantedListManager)다. 구독 순서에 따라 지금
        // Count가 지우기 전일 수도 후일 수도 있어, "곧 하나 준다"를 여기서 직접 반영한다.
        int remaining = wantedList.Wanted.Count - 1;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null)
            return;

        ShowLocal(remaining); // 호스트 자신
        Broadcast(remaining); // 나머지 클라이언트
    }

    // NetworkList는 인덱스 조회뿐이라 훑는다 — 항목은 한 자릿수다.
    private static bool IsOnWantedList(WantedListManager wantedList, NpcController npc)
    {
        if (npc == null)
            return false;

        for (int i = 0; i < wantedList.Wanted.Count; i++)
        {
            if (wantedList.Wanted[i].NpcId == npc.NetworkObjectId)
                return true;
        }

        return false;
    }

    private void Broadcast(int remaining)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null)
            return;

        using FastBufferWriter writer = new FastBufferWriter(k_writerSize, Allocator.Temp);
        writer.WriteValueSafe(remaining);
        nm.CustomMessagingManager.SendNamedMessageToAll(k_messageName, writer, NetworkDelivery.Reliable);
    }

    private void ReceiveNotice(ulong senderClientId, FastBufferReader reader)
    {
        if (senderClientId != NetworkManager.ServerClientId)
            return;

        // 호스트는 자기 브로드캐스트를 되받는다 — 이미 ShowLocal로 띄웠으니 무시한다.
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            return;

        reader.ReadValueSafe(out int remaining);
        ShowLocal(remaining);
    }

    private void ShowLocal(int remaining)
    {
        if (m_noticeMessage == null || m_noticeMessage.IsEmpty)
        {
            Debug.LogWarning("ArrestNoticeBroadcaster: 전체 알림 문구가 연결되지 않았다", this);
            return;
        }

        // 인자를 먼저 넣고 띄운다 — 순서를 어기면 첫 발화가 인자 없는 문장으로 나간다
        // (localization.md 결정 (e)).
        m_noticeMessage.Arguments = new object[] { remaining };

        // HUD가 없는 환경(데디케이티드 서버 등)에선 App.UI.Toast가 null이라 무동작.
        App.UI.Toast?.Show(m_noticeMessage, m_noticeSeconds);
    }
}
