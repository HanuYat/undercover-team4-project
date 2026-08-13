using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 검거 성립 전체 알림 (#616) — 현상수배범이 유치장에 들어가면 전 플레이어(본부·현장) 화면에
/// "〈이름〉 검거 — 남은 N명"을 띄운다.
///
/// 개인 판정 배너(<see cref="ArrestVerdictFeedback"/>, #306)는 검거자 한 명에게 가는 개인 피드백이라
/// 그대로 두고 이쪽을 따로 얹는다 — 자리도 상단 토스트 대 배너 패널로 갈려 겹치지 않는다.
///
/// <b>싣는 것은 대상 이름과 남은 수까지다</b> (#616 결정). 누가 잡았는지와 보상액은 보내지 않는다 —
/// 그것까지 자동으로 흐르면 무전으로 주고받을 것이 없어진다.
///
/// <b>알리는 것은 진범 검거뿐이다.</b> 경범죄는 수배 리스트에 오르지 않고, 오검거는 진행이 아닌 데다
/// 개인의 실수는 본인 배너로 족하다. 시체 인계(<see cref="ArrestJudge.OnCorpseJudged"/>)는 알린다 —
/// <see cref="WantedListManager"/>가 그 훅도 구독해 수배 항목을 내리므로 남은 수가 실제로 준다.
///
/// <b>같은 대상이 두 번 뜰 수 있다.</b> 반출(<see cref="JailIntake.ServerExtract"/>)은 수배 리스트를
/// 건드리지 않으므로, 꺼냈다 다시 넣으면 남은 수가 그대로인 채 알림만 다시 뜬다 — 팀에게는 "빠졌던
/// 놈을 되돌렸다"가 진행이라는 판단이다(팀 결정). 탈옥 후 재검거는 항목이 재등재되므로 숫자도 준다.
///
/// 남은 수는 수배 리스트에서 뽑는다 — <see cref="RemainingCriminalsHud"/>(#331)가 "잡은/전체"를 같은
/// 곳에서 뽑으므로 따로 세면 두 표시가 어긋난다.
///
/// 전파는 <see cref="SettlementController"/>(#107)의 네임드 메시지 패턴이되 대상을 좁히지 않는다.
/// 완성된 문장이 아니라 이름·숫자를 보내고 받는 쪽이 자기 언어로 조립한다 (localization.md 결정 (g)).
/// NetworkBehaviour가 아니라 씬 네트워크 구성을 건드리지 않는다.
/// </summary>
public class ArrestNoticeBroadcaster : MonoBehaviour
{
    private const string k_messageName = "ArrestNotice";
    private const int k_writerSize = 128; // int + FixedString64(최대 66) < 128

    [Tooltip("전 플레이어에게 띄울 문구 — Hud.Arrest.Notice ({0}=대상 이름, {1}=남은 수배자 수)")]
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
        Judge.OnCorpseJudged += HandleArrestJudged;
    }

    private void OnDisable()
    {
        if (Judge == null)
            return;

        Judge.OnArrestJudged -= HandleArrestJudged;
        Judge.OnCorpseJudged -= HandleArrestJudged;
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

    // 서버: 진범 판정이 나면 대상 이름과 남은 수를 전원에게 알린다.
    private void HandleArrestJudged(ArrestResult result)
    {
        if (result.Verdict != ArrestVerdict.WantedCriminal)
            return;

        // 수배 리스트는 세션에서만 돈다 — 오프라인 단독 Play에 알릴 남은 수가 없는 것은 정상이다.
        WantedListManager wantedList = WantedList;
        if (wantedList == null || !wantedList.IsSpawned)
            return;

        // 항목을 지우는 것은 같은 이벤트의 다른 구독자(WantedListManager)다. 구독 순서에 따라 지금
        // Count가 지우기 전일 수도 후일 수도 있어, 아직 남아 있으면 "곧 하나 준다"를 직접 반영한다.
        // 반출했다 다시 넣은 대상은 애초에 리스트에 없어 그대로 센다 — 그 경우 숫자가 안 줄고 알림만 뜬다.
        int remaining = wantedList.Wanted.Count - (IsOnWantedList(wantedList, result.Npc) ? 1 : 0);

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null)
            return;

        string citizenName = ResolveName(result);

        ShowLocal(citizenName, remaining); // 호스트 자신
        Broadcast(citizenName, remaining); // 나머지 클라이언트
    }

    // 이름 기준은 판정 배너와 같다 — 정본 CitizenName, 없으면 NPC 이름 (ArrestJudge.LogVerdict와 동일).
    private static string ResolveName(ArrestResult result)
    {
        if (result.Profile != null)
            return result.Profile.CitizenName;

        return result.Npc != null ? result.Npc.name : "알 수 없음";
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

    private void Broadcast(string citizenName, int remaining)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null)
            return;

        FixedString64Bytes name = citizenName.ToFixed64();

        using FastBufferWriter writer = new FastBufferWriter(k_writerSize, Allocator.Temp);
        writer.WriteValueSafe(remaining);
        writer.WriteValueSafe(name);
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
        reader.ReadValueSafe(out FixedString64Bytes name);
        ShowLocal(name.ToString(), remaining);
    }

    private void ShowLocal(string citizenName, int remaining)
    {
        if (m_noticeMessage == null || m_noticeMessage.IsEmpty)
        {
            Debug.LogWarning("ArrestNoticeBroadcaster: 전체 알림 문구가 연결되지 않았다", this);
            return;
        }

        // 인자를 먼저 넣고 띄운다 — 순서를 어기면 첫 발화가 인자 없는 문장으로 나간다
        // (localization.md 결정 (e)).
        m_noticeMessage.Arguments = new object[] { citizenName, remaining };

        // HUD가 없는 환경(데디케이티드 서버 등)에선 App.UI.Toast가 null이라 무동작.
        App.UI.Toast?.Show(m_noticeMessage, m_noticeSeconds);
    }
}
