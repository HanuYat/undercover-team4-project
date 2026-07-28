using System;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 무력화의 원인 — 무력화는 하나가 아니라 성질이 다른 셋이다. (#252)
/// 행동을 막는 것은 같지만 <b>어떻게 풀리는가</b>가 갈리므로, 구조·전멸 판정·애니메이션이 이 값으로 분기한다.
/// </summary>
public enum IncapacitationCause
{
    None,    // 무력화 아님
    Down,    // HP 0 다운 (#105) — 동료가 구조해야 일어난다. 전멸(게임오버) 판정 대상
    Penalty, // 오검거 광장 매달기 (#101) — 30초 뒤 자동 복귀
    Stun,    // 테이저 피격 기절 (#252) — 시간이 지나면 스스로 일어난다
}

/// <summary>
/// 플레이어 행동불능(무력화) 공통 기반. (#105)
/// HP 0 다운(#105)·오검거 매달기(#101)·테이저 피격 기절(#252)은 트리거만 다르고 결과=무력화로 같으므로,
/// 무력화 상태 자체를 이 한 곳에서 서버 권위로 관리한다.
/// 이동·아이템·상호작용 컴포넌트가 <see cref="IsIncapacitated"/>를 읽어 각자 행동을 막는다.
///
/// 셋의 차이는 <b>풀리는 방식</b>이고, 그 구분이 <see cref="Cause"/>다:
///  · 다운 — 동료 구조(PlayerReviver)로만 일어난다. 전원 다운이면 전멸(게임오버).
///  · 매달기·기절 — 시간이 지나면 스스로 복귀하므로 구조 대상도, 전멸 판정 대상도 아니다.
/// 구조 히트박스도 다운에서만 켠다.
/// </summary>
public class PlayerIncapacitation : NetworkBehaviour
{
    // 다운 중에만 활성화되는 구조 대상 히트박스(Interactable 레이어). 평소 비활성 — 조준 타겟팅용. (#105)
    // 플레이어 몸(CharacterController)은 Default 레이어라 PlayerInteractor의 Interactable 마스크에 안 잡히므로,
    // 다운 시 이 트리거 콜라이더를 켜서 구조자가 조준할 수 있게 한다.
    [SerializeField]
    private GameObject m_reviveHitbox;

    // 서버 권위 무력화 원인 — 서버만 쓰고 모든 클라가 읽는다. (PlayerData.m_syncedHp와 동일 패턴)
    // 예전에는 bool 두 개(무력화 여부 + 구조 가능 여부)였는데, 기절이 들어오며 '구조 불가'가 둘로
    // 갈려(매달기·기절) 조합으로는 구분할 수 없게 됐다 — 원인 하나로 합쳤다. (#252)
    private readonly NetworkVariable<IncapacitationCause> m_causeSynced =
        new NetworkVariable<IncapacitationCause>();

    private IncapacitationCause m_cause; // 서버·오프라인의 진실값 (비네트워크 Play 테스트 폴백)

    // 기절 회차 — 지연 복구가 '자기가 건 기절'만 풀게 하는 토큰. 기절이 풀린 뒤 다시 걸리거나 그 사이
    // 다운·매달기가 들어오면 회차가 어긋나, 낡은 타이머는 무동작으로 끝난다. (#252)
    private int m_stunEpisode;

    /// <summary>무력화 원인. 서버·오프라인은 실참조, 원격 피어는 동기화값으로 판정. (PlayerEscorter.IsEscorting 관례)</summary>
    public IncapacitationCause Cause => IsSpawned && !IsServer ? m_causeSynced.Value : m_cause;

    /// <summary>무력화(행동불능) 여부 — 이동·아이템·상호작용 게이트가 읽는다. 원인을 가리지 않는다.</summary>
    public bool IsIncapacitated => Cause != IncapacitationCause.None;

    /// <summary>HP 0 다운인지 — <b>구조 대상·전멸 판정은 이것만</b> 본다. 매달기·기절은 스스로 풀린다. (#252)</summary>
    public bool IsDowned => Cause == IncapacitationCause.Down;

    /// <summary>테이저 피격 기절인지 — 애니메이션이 다운 모션과 기절 모션으로 갈리는 기준. (#252)</summary>
    public bool IsStunned => Cause == IncapacitationCause.Stun;

    /// <summary>무력화 상태가 바뀔 때 발행 — 애니메이션·UI 훅용. 원인만 바뀌면 울리지 않는다.</summary>
    public event Action<bool> OnIncapacitatedChanged;

    /// <summary>
    /// 서버·오프라인에서 '아무' 플레이어의 무력화 상태가 바뀔 때 발행 — 전원 다운(전멸) 판정 등 전역 로직용. (#105)
    /// 서버 권위 경로(<see cref="SetCause"/>)에서만 발행되므로 클라이언트에서는 울리지 않는다.
    /// </summary>
    public static event Action OnAnyIncapacitatedChanged;

    public override void OnNetworkSpawn()
    {
        // 원격 피어는 서버 Set 경로를 타지 않으므로, 동기화값 변화를 이벤트로 중계한다.
        m_causeSynced.OnValueChanged += HandleSyncedChanged;

        // 늦게 접속한 클라: 이미 다운된 플레이어의 현재 상태를 즉시 반영한다.
        // (OnValueChanged는 '변화' 시에만 발생하므로 스폰 시 한 번 맞춰줘야 한다)
        RefreshReviveHitbox();
    }

    public override void OnNetworkDespawn()
    {
        m_causeSynced.OnValueChanged -= HandleSyncedChanged;
    }

    // 서버(호스트 포함)는 SetCause에서 이벤트를 직접 발행하므로 여기선 원격 클라만 중계 (이중 발행 방지)
    private void HandleSyncedChanged(IncapacitationCause previous, IncapacitationCause current)
    {
        if (IsServer)
            return;

        RefreshReviveHitbox();

        // 원인만 바뀌고 무력화 여부는 그대로면 알리지 않는다 — 구독자는 bool만 본다
        bool was = previous != IncapacitationCause.None;
        bool now = current != IncapacitationCause.None;
        if (was != now)
            OnIncapacitatedChanged?.Invoke(now);
    }

    // 구조 대상 히트박스 = 다운 중에만 켠다. 서버·원격·오프라인 모든 인스턴스에서 실행된다.
    private void RefreshReviveHitbox()
    {
        if (m_reviveHitbox != null)
            m_reviveHitbox.SetActive(IsDowned);
    }

    /// <summary>
    /// 무력화 진입 — 서버(또는 오프라인)에서만. HP0 다운(#105)·매달기(#101)가 호출한다.
    /// 기절은 스스로 풀려야 하므로 이 경로가 아니라 <see cref="ServerStun"/>을 쓴다.
    /// </summary>
    public void Incapacitate(IncapacitationCause cause = IncapacitationCause.Down)
    {
        if (IsSpawned && !IsServer)
            return; // 서버 권위 방어 (PlayerEscorter 관례)

        if (cause == IncapacitationCause.None)
        {
            Debug.LogWarning("PlayerIncapacitation: 원인 None으로는 무력화할 수 없다 — 해제는 Recover()", this);
            return;
        }

        SetCause(cause);
    }

    /// <summary>
    /// 기절 진입 — seconds 뒤 <b>스스로</b> 회복한다. 테이저 아군 오사(#252)가 호출. 서버 전용.
    /// 이미 무력화된 대상은 무시한다 — 다운·매달기를 기절로 덮어쓰면 그 무력화가 기절 타이머에 일찍 풀린다.
    /// </summary>
    public void ServerStun(float seconds)
    {
        if (IsSpawned && !IsServer)
            return;
        if (IsIncapacitated)
            return;

        SetCause(IncapacitationCause.Stun);
        ServerStunTimerAsync(seconds, ++m_stunEpisode).Forget();
    }

    /// <summary>무력화 해제(구조·복구) — 서버(또는 오프라인)에서만.</summary>
    public void Recover()
    {
        if (IsSpawned && !IsServer)
            return;
        SetCause(IncapacitationCause.None);
    }

    // 기절 자동 회복 타이머. 씬 전환·파괴는 토큰으로 안전 중단한다 (WrongfulArrestPenalty.HangAsync 관례).
    private async UniTaskVoid ServerStunTimerAsync(float seconds, int episode)
    {
        try
        {
            await UniTask.Delay(
                TimeSpan.FromSeconds(seconds), cancellationToken: destroyCancellationToken);
        }
        catch (OperationCanceledException)
        {
            return; // 파괴·퇴장 — 복구할 대상이 이미 없다
        }

        // 내가 건 기절이 그대로일 때만 푼다 — 그 사이 구조·다운·매달기가 들어왔으면 남의 상태다
        if (m_stunEpisode != episode || m_cause != IncapacitationCause.Stun)
            return;

        Recover();
    }

    // 서버 권위 값 변경 + 로컬 이벤트 발행을 함께 처리 — 서버(또는 오프라인)에서만 호출된다.
    private void SetCause(IncapacitationCause cause)
    {
        if (m_cause == cause)
            return; // 중복 트리거 무시

        bool was = IsIncapacitated;
        m_cause = cause;
        if (IsSpawned && IsServer)
            m_causeSynced.Value = cause;

        RefreshReviveHitbox();
        if (was != IsIncapacitated)
            OnIncapacitatedChanged?.Invoke(IsIncapacitated);
        OnAnyIncapacitatedChanged?.Invoke(); // 전역 훅 — RoundManager가 전원 다운(전멸) 여부를 재검사
    }
}
