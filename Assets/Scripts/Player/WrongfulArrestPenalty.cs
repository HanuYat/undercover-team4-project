using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 오검거 페널티 (GDD 7-3, #101) — 팀 공유 오검거 카운트를 서버 권위로 누적하고,
/// 허용 횟수(k_maxWrongful) 초과 시 마지막에 임계치를 넘긴 플레이어를 광장에 매단다(행동불능).
/// ArrestJudge.OnArrestJudged를 구독해 오검거 판정을 받는다 — 판정·카운트·매달기 모두 서버에서만
/// 일어나고 결과(팀 카운트·행동불능 상태)만 NetworkVariable·RPC로 클라에 동기화된다. (#56 패턴)
///
/// 매달기는 즉발이 아니라 k_graceSeconds 유예 뒤 집행된다 — 그 사이 팀원이 호루라기(#250)로
/// 카운트를 임계치 이하로 내리면(<see cref="ConsumeWhistle"/>) 유예가 취소돼 페널티를 피할 수 있다.
/// 행동불능 상태 자체는 PlayerIncapacitation(#105)이 관리한다 — 여기선 트리거만 건다.
///
/// 개인별 오검거 집계는 정산 "최다 오검거" 코믹 스탯(GDD 7-3)용으로 팀 카운트와 별개로 유지한다:
/// 팀 카운트는 페널티 게이지(호루라기로 감소), 개인 집계는 실제 오검거 기록(호루라기 무관).
/// 오검거는 팀 자금·라운드 종료와 무관하다(GDD 7-3 확정) — 여기서 자금/라운드를 건드리지 않는다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class WrongfulArrestPenalty : NetworkedManagerBase
{
    private const int k_maxWrongful = 1;      // 이 값을 "초과"하면(4회째) 매달기 유예 시작
    private const float k_graceSeconds = 15f; // 매달기 집행 전 유예 — 호루라기로 취소 가능
    private const float k_hangSeconds = 30f;  // 광장 매달기(행동불능) 지속 시간

    [Header("광장 (매달기 이송 지점) — 비우면 원점")]
    [Tooltip("오검거 누적 초과 시 이송할 맵 중앙 지점. 씬의 빈 GameObject를 지정한다")]
    [SerializeField] private Transform m_plazaPoint;

    private ArrestJudge Judge => App.Game.ArrestJudge;

    // 서버만 쓰고 전 클라가 읽는 팀 공유 카운트 = 페널티 게이지. (정산 개인 집계와 별개)
    private readonly NetworkVariable<int> m_teamCountSynced = new NetworkVariable<int>();

    // 정산 코믹 스탯용 개인 집계 — clientId별 실제 오검거 횟수. 서버에서만 누적(표시·동기화는 정산 UI 이슈).
    private readonly Dictionary<ulong, int> m_perPlayerCounts = new Dictionary<ulong, int>();

    // 매달기 유예(15초)의 서버 권위 채널 — 호루라기로 Cancel() 가능. 30초 매달기 본체는 별도 Delay.
    private readonly ServerChannel m_grace = new ServerChannel();

    // 유예 진행 중인 대상 — 중복 유예 방지 및 취소 대상 식별용. 유예가 없으면 null.
    private PlayerEscorter m_pendingTarget;

    /// <summary>팀 공유 오검거 카운트(페널티 게이지). 전 피어 읽기 가능.</summary>
    public int TeamWrongfulCount => m_teamCountSynced.Value;

    /// <summary>정산용 개인 오검거 집계(clientId→횟수). 서버에서만 채워진다.</summary>
    public IReadOnlyDictionary<ulong, int> PerPlayerCounts => m_perPlayerCounts;

    public override void OnNetworkSpawn()
    {
        // 판정은 서버 권위이므로 서버에서만 구독한다 (WantedListManager 관례).
        // 재시작(Shutdown 후 StartHost) 시 씬 NetworkObject에 이전 세션 값이 남으므로 새로 0에서 시작한다.
        if (IsServer)
        {
            m_teamCountSynced.Value = 0;
            m_perPlayerCounts.Clear();

            if (Judge != null)
                Judge.OnArrestJudged += HandleArrestJudged;
            else
                Debug.LogWarning("WrongfulArrestPenalty: ArrestJudge를 찾지 못해 오검거를 집계할 수 없다", this);
        }
    }

    public override void OnNetworkDespawn()
    {
        if (Judge != null)
            Judge.OnArrestJudged -= HandleArrestJudged;
    }

    public override void OnDestroy()
    {
        m_grace.Dispose(); // 진행 중 유예 취소 + CTS 정리
        base.OnDestroy();  // App 등록 해제
    }

    // 오검거 판정 수신 — 진범이 아닌 대상을 인계한 플레이어의 카운트를 올린다. (서버 전용)
    private void HandleArrestJudged(ArrestResult result)
    {
        if (result.Verdict != ArrestVerdict.WrongfulArrest || result.DeliveredBy == null)
            return;

        // 개인 집계는 실제 오검거 기록 — 호루라기와 무관하게 항상 +1 (정산 코믹 스탯용).
        ulong clientId = result.DeliveredBy.OwnerClientId;
        m_perPlayerCounts.TryGetValue(clientId, out int prev);
        m_perPlayerCounts[clientId] = prev + 1;

        // 팀 카운트(페널티 게이지) +1.
        m_teamCountSynced.Value += 1;
        Debug.Log($"[오검거] {result.DeliveredBy.name}(client {clientId}) 개인 {m_perPlayerCounts[clientId]}회 · 팀 카운트 {m_teamCountSynced.Value} — {FormatPerPlayerCounts()}");

        // 임계치 초과 + 유예 진행 중이 아니면 → 이번에 넘긴 플레이어를 대상으로 매달기 유예를 건다.
        if (m_teamCountSynced.Value > k_maxWrongful && m_pendingTarget == null)
            StartGrace(result.DeliveredBy);
    }

    // 개인 집계 전체를 "client N:x회" 형태로 이어붙인다 — 정산 코믹 스탯이 붙기 전까지 로그로 확인용.
    private string FormatPerPlayerCounts()
    {
        if (m_perPlayerCounts.Count == 0)
            return "개인집계 없음";

        var sb = new System.Text.StringBuilder("개인집계 [");
        bool first = true;
        foreach (KeyValuePair<ulong, int> pair in m_perPlayerCounts)
        {
            if (!first) sb.Append(", ");
            sb.Append($"client {pair.Key}:{pair.Value}회");
            first = false;
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// 호루라기(#250)가 호출 — 팀 카운트를 1 줄인다(하한 0). 매달기 유예 중이고 임계치 이하로
    /// 내려가면 유예를 취소해 페널티를 무른다. 서버(또는 오프라인)에서만 유효하다.
    /// </summary>
    public void ConsumeWhistle()
    {
        if (IsSpawned && !IsServer)
            return;

        if (m_teamCountSynced.Value > 0)
            m_teamCountSynced.Value -= 1;

        // 유예 중 임계치 이하로 내려갔으면 취소 — ServerChannel이 Canceled로 끝나 GraceAsync가 매달기를 건너뛴다.
        if (m_pendingTarget != null && m_teamCountSynced.Value <= k_maxWrongful)
            m_grace.Cancel();
    }

    private void StartGrace(PlayerEscorter target)
    {
        m_pendingTarget = target;

        // 대상 본인에게만 "곧 광장 이송" 경고 — 팀원이 호루라기로 구제할 시간을 준다.
        PlayerPenaltyView view = target.GetComponent<PlayerPenaltyView>();
        if (view != null)
            view.ShowWarning(k_graceSeconds);

        GraceAsync(target, view).Forget();
    }

    private async UniTaskVoid GraceAsync(PlayerEscorter target, PlayerPenaltyView view)
    {
        ServerChannel.Result result = await m_grace.RunAsync(k_graceSeconds);

        // 호루라기로 취소됨 → 매달기 무산. 카운트는 이미 임계치 이하로 내려간 상태.
        if (result == ServerChannel.Result.Canceled)
        {
            if (view != null)
                view.HideWarning();
            m_pendingTarget = null;
            Debug.Log("[오검거] 호루라기로 매달기 취소됨");
            return;
        }

        // 집행 — 카운트를 0으로 리셋한다. 리셋하지 않으면 계속 임계치 초과라 이후 오검거마다 재발동한다.
        m_teamCountSynced.Value = 0;
        m_pendingTarget = null;

        await HangAsync(target);
    }

    // 광장 이송 + 30초 행동불능 → 자동 복귀. (서버 전용)
    private async UniTask HangAsync(PlayerEscorter target)
    {
        if (target == null) // 유예 중 퇴장·파괴
            return;

        PlayerMovement movement = target.GetComponent<PlayerMovement>();
        PlayerIncapacitation incap = target.GetComponent<PlayerIncapacitation>();

        if (m_plazaPoint == null)
            Debug.LogWarning("WrongfulArrestPenalty: Plaza Point 미할당 — 원점(0,0,0)으로 이송된다. 인스펙터에 광장 지점을 지정할 것", this);

        Vector3 pos = m_plazaPoint != null ? m_plazaPoint.position : Vector3.zero;
        Quaternion rot = m_plazaPoint != null ? m_plazaPoint.rotation : Quaternion.identity;

        if (movement != null)
            movement.ServerTeleport(pos, rot); // 오너 권한 경로 — 호스트·원격 클라 모두 이동
        if (incap != null)
            incap.Incapacitate(revivable: false); // 매달기는 구조 불가 — 30초 자동 복귀만

        Debug.Log($"[오검거] 광장 매달기 집행 — {target.name} → {pos}, {k_hangSeconds}초");

        // 30초 대기(호루라기로 못 무름 — 유예 창은 이미 지났다). 씬 전환·파괴 시 토큰으로 안전 중단한다.
        try
        {
            await UniTask.Delay(TimeSpan.FromSeconds(k_hangSeconds), cancellationToken: destroyCancellationToken);
        }
        catch (OperationCanceledException)
        {
            return; // 매니저 파괴 — 복귀 처리 없이 종료(대상도 함께 정리되는 상황)
        }

        // 자동 복귀 — 대상이 퇴장·파괴됐을 수 있어 fake-null 가드
        if (incap != null)
            incap.Recover();
    }
}
