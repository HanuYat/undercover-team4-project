using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 검거 성립 전체 알림 (#616) — 진범이 잡히면(생포·시체 인계 모두) 검거자를 뺀 전 플레이어에게
/// "〈이름〉 검거 — 남은 N명"을 띄운다. 검거자는 개인 배너(<see cref="ArrestVerdictFeedback"/>, #306)로
/// 이미 알고 있으므로 제외한다.
///
/// 이름·남은 수만 싣는다 — 누가 잡았는지·보상액은 안 보낸다(무전으로 알릴 몫을 남겨 둔다).
/// 경범죄·오검거는 알리지 않는다(수배 리스트가 안 줄거나 개인 실수라서). 반출 후 재수감은 남은
/// 수가 그대로여도 다시 알린다(팀 결정) — 진행 신호로 본다.
///
/// 남은 수는 <see cref="RemainingCriminalsHud"/>(#331)와 같은 수배 리스트에서 뽑는다.
/// 전파는 <see cref="SettlementController"/>(#107)와 같은 네임드 메시지 패턴, 완성 문장 대신
/// 이름·숫자만 보내 받는 쪽이 자기 언어로 조립한다(localization.md 결정 (g)).
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

    [Tooltip("알림 배경색 — 판정 배너(VerdictBanner)와 같은 공용 팔레트의 '성공' 색을 쓴다 (#951)")]
    [SerializeField] private UiColorPalette m_palette;

    [Tooltip("알림 배경 채움 투명도")]
    [Range(0f, 1f)]
    [SerializeField] private float m_noticeAlpha = 0.95f;

    private ArrestJudge Judge => App.Game.ArrestJudge;
    private WantedListManager WantedList => App.Game.WantedList;


    // 검거마다 재사용하는 버퍼 — 서버에서만 쓴다.
    private readonly HashSet<ulong> m_arresters = new HashSet<ulong>();
    private readonly List<ulong> m_targets = new List<ulong>();

    private void OnEnable()
    {
        if (Judge == null) // 판정기가 없는 단독 테스트 씬은 조용히 빠진다
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

    // 네임드 메시지 수신 — 등록·해제 절차는 NamedMessageSubscription이 맡는다.
    private NamedMessageSubscription m_message;

    private void Start()
    {
        m_message = new NamedMessageSubscription(k_messageName, ReceiveNotice);
        m_message.Attach();
    }

    private void OnDestroy() => m_message?.Detach();

    private void HandleArrestJudged(ArrestResult result)
    {
        if (result.Verdict != ArrestVerdict.WantedCriminal)
            return;

        WantedListManager wantedList = WantedList;
        if (wantedList == null || !wantedList.IsSpawned) // 오프라인 단독 Play엔 수배 리스트가 없다
            return;

        // WantedListManager도 같은 이벤트를 구독해 항목을 지운다 — 구독 순서와 무관하게 맞는
        // 값을 내려고 "아직 리스트에 있으면 하나 뺀다"로 직접 계산한다.
        int remaining = wantedList.Wanted.Count - (IsOnWantedList(wantedList, result.Npc) ? 1 : 0);

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null)
            return;

        string citizenName = ResolveName(result);

        // 검거자(줄다리기 인계자 전원, #390)는 개인 배너로 이미 안다 — 대상에서 뺀다.
        m_arresters.Clear();
        foreach (PlayerEscorter deliverer in result.DeliveredBy)
        {
            if (deliverer != null)
                m_arresters.Add(deliverer.OwnerClientId);
        }

        if (!m_arresters.Contains(nm.LocalClientId))
            ShowLocal(citizenName, remaining);

        Broadcast(citizenName, remaining);
    }

    // 이름 기준은 판정 배너와 같다 (ArrestJudge.LogVerdict와 동일).
    private static string ResolveName(ArrestResult result)
    {
        if (result.Profile != null)
            return result.Profile.CitizenName;

        return result.Npc != null ? result.Npc.name : "알 수 없음";
    }

    // NetworkList는 인덱스 조회뿐이라 훑는다.
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

        // 호스트·검거자는 이미 처리했다 — 남는 사람에게만 보낸다.
        m_targets.Clear();
        foreach (ulong clientId in nm.ConnectedClientsIds)
        {
            if (clientId == nm.LocalClientId || m_arresters.Contains(clientId))
                continue;

            m_targets.Add(clientId);
        }

        if (m_targets.Count == 0)
            return;

        FixedString64Bytes name = citizenName.ToFixed64();

        using FastBufferWriter writer = new FastBufferWriter(k_writerSize, Allocator.Temp);
        writer.WriteValueSafe(remaining);
        writer.WriteValueSafe(name);
        nm.CustomMessagingManager.SendNamedMessage(k_messageName, m_targets, writer, NetworkDelivery.Reliable);
    }

    private void ReceiveNotice(ulong senderClientId, FastBufferReader reader)
    {
        if (senderClientId != NetworkManager.ServerClientId)
            return;

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer) // 방어적 가드
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

        m_noticeMessage.Arguments = new object[] { citizenName, remaining }; // Show보다 먼저
        App.UI.Toast?.Show(m_noticeMessage, m_noticeSeconds, NoticeTone()); // HUD 없으면 무동작
    }

    // 배선이 빠지면 흰색으로 뜬다 — 검거 알림이 평범한 토스트가 돼 눈에 띄므로 경고도 남긴다.
    private Color NoticeTone()
    {
        if (m_palette == null)
        {
            Debug.LogWarning("ArrestNoticeBroadcaster: 색 팔레트가 연결되지 않았다", this);
            return Color.white;
        }

        return UiColorPalette.WithAlpha(m_palette.Positive, m_noticeAlpha);
    }
}
