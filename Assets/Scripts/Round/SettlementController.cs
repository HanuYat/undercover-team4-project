using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>라운드 정산 화면에 표시할 데이터 묶음 — 서버가 종료 시점에 스냅샷으로 채운다. (#107)</summary>
public struct SettlementData
{
    public RoundResult Result;      // 라운드 결과(성공/실패)
    public RoundEndReason Reason;   // 종료 사유(할당량 달성/제한시간 초과/전원 다운)
    public int FundBalance;         // 팀 자금 잔액
    public int FundDelta;           // 이번 라운드 자금 증감(현재-시작)
    public string TopOffenderName;  // 이번 판 최다 오검거 플레이어 이름 (없으면 빈 문자열)
    public int TopOffenderCount;    // 그 플레이어의 오검거 횟수 (0이면 오검거 없음)
}

/// <summary>
/// 라운드 정산 화면 제어 (#107, GDD 3-2) — 라운드 종료 시 결과·팀 자금 증감·이번 판 최다 오검거(코믹 스탯)를
/// 모아 전 클라이언트의 정산 패널(SettlementPanel)에 띄운다.
///
/// 전파 흐름은 RoundEndFeedback(#210)과 동일 — 라운드 진행이 서버 권위이므로(#56):
///  · 서버·오프라인 — RoundManager.OnRoundEnded를 직접 구독해 데이터를 모으고, 로컬 패널을 띄운 뒤
///    네트워크 세션이면 커스텀 네임드 메시지로 전 클라이언트에 같은 데이터를 보낸다.
///  · 클라이언트 — 네임드 메시지를 수신해 동일한 패널을 띄운다.
/// 팀 자금은 TeamFund NetworkVariable로 이미 동기화되지만, 결과·오검거 개인집계는 서버 전용이라
/// 종료 시점 스냅샷을 한 번에 묶어 보낸다 — 늦게 접속한 클라와 무관하게 그 순간 값을 그대로 전달한다.
///
/// NetworkBehaviour가 아니므로(RoundEndFeedback와 동일) 씬 네트워크 구성(NetworkObject·프리팹 등록)을 건드리지 않는다.
/// </summary>
public class SettlementController : MonoBehaviour
{
    private const string k_messageName = "RoundSettlement";
    private const int k_writerSize = 128; // byte + int*3 + FixedString64(최대 66) < 128

    private RoundManager Round => App.Game.Round;
    private WrongfulArrestPenalty Penalty => App.Game.WrongfulArrestPenalty;

    [Header("참조 (비우면 씬에서 자동 탐색)")]
    [SerializeField]
    private TeamFund m_teamFund;

    private bool m_handlerRegistered;

    // 이번 라운드 시작 시점의 팀 자금 — 정산 증감(현재-시작) 기준. TeamFund가 세션 지속형이라(#214)
    // 세션 초기값이 아니라 "이 라운드가 시작될 때" 잔액을 스냅샷해야 이번 라운드 증감이 나온다.
    private int m_roundStartFund;

    private void Awake()
    {
        if (m_teamFund == null)
            m_teamFund = FindFirstObjectByType<TeamFund>();
    }

    private void OnEnable()
    {
        // 라운드 종료·시작은 서버·오프라인에서만 발행된다 — 권위 피어가 이 훅들로 자금 스냅샷·정산을 처리한다.
        if (Round != null)
        {
            Round.OnRoundStarted += HandleRoundStarted;
            Round.OnRoundEnded += HandleRoundEnded;
        }
    }

    private void OnDisable()
    {
        if (Round != null)
        {
            Round.OnRoundStarted -= HandleRoundStarted;
            Round.OnRoundEnded -= HandleRoundEnded;
        }
    }

    // 라운드 시작(서버·오프라인) 시점 자금을 기록해 둔다 — 종료 시 증감 계산 기준.
    private void HandleRoundStarted()
    {
        m_roundStartFund = m_teamFund != null ? m_teamFund.Balance : 0;
    }

    private void Start()
    {
        // 클라이언트 수신 등록 — CustomMessagingManager는 NGO가 시작된 뒤에만 존재한다. (RoundEndFeedback와 동일 패턴)
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

        nm.CustomMessagingManager.RegisterNamedMessageHandler(k_messageName, ReceiveSettlement);
        m_handlerRegistered = true;
    }

    // 서버·오프라인: 종료 후 데이터를 모아 로컬 표시 + 세션이면 전 클라 전파.
    private void HandleRoundEnded(RoundResult result, RoundEndReason reason)
    {
        GatherAndShowAsync(result, reason).Forget();
    }

    // 라운드 종료가 검거 판정(ArrestJudge.OnArrestJudged)과 같은 프레임에 발생하면(할당량 채운 그 검거),
    // 그 검거의 보상이 팀 자금에 아직 반영되기 전일 수 있다 — 같은 이벤트의 구독자 호출 순서 경쟁 때문.
    // 한 프레임 미뤄 그 디스패치의 모든 구독자(TeamFund 보상 가산 등)가 끝난 뒤의 확정 자금을 읽는다.
    // (라운드 종료 후 리셋까지 여유가 있어 한 프레임 지연은 화면상 보이지 않는다)
    private async UniTaskVoid GatherAndShowAsync(RoundResult result, RoundEndReason reason)
    {
        try
        {
            await UniTask.NextFrame(destroyCancellationToken);
        }
        catch (OperationCanceledException)
        {
            return; // 매니저 파괴 — 정리 중이므로 표시하지 않는다
        }

        SettlementData data = BuildData(result, reason);
        ShowLocal(data);
        Broadcast(data);
    }

    // 결과·종료 사유·자금 증감·최다 오검거를 모은다 (서버·오프라인 권위 데이터).
    private SettlementData BuildData(RoundResult result, RoundEndReason reason)
    {
        int balance = m_teamFund != null ? m_teamFund.Balance : 0;
        int delta = m_teamFund != null ? m_teamFund.Balance - m_roundStartFund : 0;

        string topName = string.Empty;
        int topCount = 0;
        if (Penalty != null)
            FindTopOffender(Penalty.PerPlayerCounts, out topName, out topCount);

        return new SettlementData
        {
            Result = result,
            Reason = reason,
            FundBalance = balance,
            FundDelta = delta,
            TopOffenderName = topName,
            TopOffenderCount = topCount,
        };
    }

    // 개인 오검거 집계에서 최다자를 뽑아 clientId를 표시 이름으로 바꾼다. 동률이면 먼저 순회된 쪽.
    private static void FindTopOffender(
        IReadOnlyDictionary<ulong, int> counts,
        out string name,
        out int count
    )
    {
        name = string.Empty;
        count = 0;
        if (counts == null)
            return;

        ulong topClient = 0;
        foreach (KeyValuePair<ulong, int> pair in counts)
        {
            if (pair.Value <= count)
                continue;
            count = pair.Value;
            topClient = pair.Key;
        }

        if (count > 0)
            name = ResolvePlayerName(topClient);
    }

    // clientId → 동기화된 표시 이름. 접속이 끊겼거나 이름이 비었으면 "플레이어 N"으로 폴백.
    private static string ResolvePlayerName(ulong clientId)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (
            nm != null
            && nm.ConnectedClients.TryGetValue(clientId, out NetworkClient client)
            && client.PlayerObject != null
        )
        {
            PlayerNameTag tag = client.PlayerObject.GetComponent<PlayerNameTag>();
            if (tag != null && !string.IsNullOrEmpty(tag.DisplayName))
                return tag.DisplayName;
        }
        return $"플레이어 {clientId}";
    }

    private void Broadcast(SettlementData data)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null)
            return;

        FixedString64Bytes name = default;
        name.CopyFromTruncated(data.TopOffenderName ?? string.Empty);

        using FastBufferWriter writer = new FastBufferWriter(k_writerSize, Allocator.Temp);
        writer.WriteValueSafe((byte)data.Result);
        writer.WriteValueSafe((byte)data.Reason);
        writer.WriteValueSafe(data.FundBalance);
        writer.WriteValueSafe(data.FundDelta);
        writer.WriteValueSafe(data.TopOffenderCount);
        writer.WriteValueSafe(name);
        nm.CustomMessagingManager.SendNamedMessageToAll(
            k_messageName,
            writer,
            NetworkDelivery.Reliable
        );
    }

    private void ReceiveSettlement(ulong senderClientId, FastBufferReader reader)
    {
        if (senderClientId != NetworkManager.ServerClientId)
            return;

        // 호스트는 자기 브로드캐스트를 되받을 수 있다 — 이미 ShowLocal로 띄웠으니 무시(중복 방지).
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            return;

        reader.ReadValueSafe(out byte resultByte);
        reader.ReadValueSafe(out byte reasonByte);
        reader.ReadValueSafe(out int balance);
        reader.ReadValueSafe(out int delta);
        reader.ReadValueSafe(out int topCount);
        reader.ReadValueSafe(out FixedString64Bytes name);

        ShowLocal(
            new SettlementData
            {
                Result = (RoundResult)resultByte,
                Reason = (RoundEndReason)reasonByte,
                FundBalance = balance,
                FundDelta = delta,
                TopOffenderCount = topCount,
                TopOffenderName = name.ToString(),
            }
        );
    }

    private static void ShowLocal(SettlementData data)
    {
        if (App.UI.Current != null && App.UI.Current.TryGetPanel(out SettlementPanel panel))
            panel.Show(data);
        else
            Debug.LogWarning("SettlementController: 정산 패널(SettlementPanel)을 찾지 못해 표시하지 못했다");
    }
}
