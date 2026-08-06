using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 무력화의 원인 — 무력화는 하나가 아니라 성질이 다른 여럿이다. (#252, #364, #371, #524)
/// 행동을 막는 것은 같지만 <b>어떻게 풀리는가</b>가 갈리므로, 운반·전멸 판정·애니메이션이 이 값으로 분기한다.
/// </summary>
public enum IncapacitationCause
{
    None, // 무력화 아님

    // HP 0 다운 (#105) — 동료의 현장 구조로 일어나던 상태.
    // #524로 진입 경로가 끊겨 <b>현재는 발생하지 않는다</b> — HP 0은 곧바로 Die가 된다.
    // 값을 남기는 이유는 둘이다: ① NetworkVariable로 동기화되는 enum이라 순서가 곧 와이어 포맷이고,
    // ② 현장 구조를 되살릴 때 PlayerHealth.SetHp의 원인만 이걸로 되돌리면 그대로 다시 동작한다.
    Down,
    Penalty, // 오검거 광장 매달기 (#101) — 30초 뒤 자동 복귀
    Stun, // 테이저 피격 기절 (#252) — 시간이 지나면 스스로 일어난다
    Die, // HP 0 기능 정지 (#364, #524) — 현장 구조로는 못 일어난다. 본부 이송 부활(#365)만이 복구 경로
    Abducted, // 납치 호송 중 (#371) — 끌려가는 동안 걸어 나가지 못하게. 외곽에 도착하면 Lynched로 넘어간다
    Lynched, // 외곽 린치 (#371 후속) — 납치범에게 맞는 중. 행동은 막되 <b>쓰러진 자세가 아니다</b>(IsProne 제외)
    // (값은 반드시 끝에 추가한다 — NetworkVariable로 동기화되는 enum이라 순서가 곧 와이어 포맷이다)
}

/// <summary>
/// 플레이어 행동불능(무력화) 공통 기반. (#105)
/// HP 0 기능 정지(#105/#364/#524)·오검거 매달기(#101)·테이저 피격 기절(#252)·납치 호송(#371)은
/// 트리거만 다르고 결과=무력화로 같으므로, 무력화 상태 자체를 이 한 곳에서 서버 권위로 관리한다.
/// 이동·아이템·상호작용 컴포넌트가 <see cref="IsIncapacitated"/>를 읽어 각자 행동을 막는다.
///
/// 차이는 <b>풀리는 방식</b>이고, 그 구분이 <see cref="Cause"/>다:
///  · Die — HP 0으로 곧바로 들어간다(#524). 현장 구조로는 못 일어나고 본부 이송 부활(#365)만
///    남으므로, 조준 히트박스는 켜되(운반 조준용) 구조 채널링은 거부된다.
///  · 매달기·기절 — 시간이 지나면 스스로 복귀하므로 운반 대상도, 전멸 판정 대상도 아니다.
///  · 납치·린치 — 끌려가는 동안·맞는 동안 걸어 나가지 못하게 한다(#371). 동료가 납치범을 때려
///    떼어내면 풀리고, 떼어내지 못하면 HP가 0이 되어 Die로 넘어간다 — 그 자체로는 전멸 판정 대상이 아니다.
///  · Down — 현장 구조가 있던 시절의 중간 단계. 지금은 발생하지 않는다(enum 주석 참고, #524).
/// 조준 히트박스는 쓰러져 있는 동안(<see cref="IsOutOfAction"/>) 켠다.
/// </summary>
public class PlayerIncapacitation : NetworkBehaviour
{
    // 쓰러진 동안(Die·Down)만 활성화되는 조준 히트박스(Interactable 레이어). 평소 비활성. (#105, #364)
    // 플레이어 몸(CharacterController)은 Default 레이어라 PlayerInteractor의 Interactable 마스크에 안 잡히므로,
    // 쓰러진 동안 이 트리거 콜라이더를 켜서 운반자(#365)가 조준할 수 있게 한다.
    // 필드명은 구조 전용이던 시절 그대로다 — 프리팹 직렬화가 이름으로 묶여 있어 바꾸면 인스펙터 참조가 끊긴다.
    [SerializeField]
    private GameObject m_reviveHitbox;

    // 서버 권위 무력화 원인 — 서버만 쓰고 모든 클라가 읽는다. (PlayerHealth.m_syncedHp와 동일 패턴)
    // 예전에는 bool 두 개(무력화 여부 + 구조 가능 여부)였는데, 기절이 들어오며 '구조 불가'가 둘로
    // 갈려(매달기·기절) 조합으로는 구분할 수 없게 됐다 — 원인 하나로 합쳤다. (#252)
    private readonly NetworkVariable<IncapacitationCause> m_causeSynced =
        new NetworkVariable<IncapacitationCause>();

    private IncapacitationCause m_cause; // 서버·오프라인의 진실값 (비네트워크 Play 테스트 폴백)

    // 기절 회차 — 지연 복구가 '자기가 건 기절'만 풀게 하는 토큰. 기절이 풀린 뒤 다시 걸리거나 그 사이
    // 기능 정지·매달기가 들어오면 회차가 어긋나, 낡은 타이머는 무동작으로 끝난다. (#252)
    private int m_stunEpisode;

    // 기절 해제 예정 시각 — 감전 연출이 잦아드는 시점을 잡는 데 쓴다 (#477). m_cause와 같은 이중 구조.
    // 연출용이라 없어도 규칙은 돌아가지만, 클라이언트는 기절 지속 시간(서버가 쥔 Taser 프리팹 값)을
    // 알 방법이 이것뿐이다 — 없으면 "곧 일어난다"를 표현할 수 없다.
    private readonly NetworkVariable<double> m_stunDeadlineSynced = new NetworkVariable<double>();
    private double m_stunDeadline;

    // 위 마감의 시간 기준 — 온라인은 서버 시각(모든 피어가 같은 값을 읽는다), 오프라인은 로컬 시각.
    // Time.time을 그대로 동기화하면 피어마다 기점이 달라 남은 시간이 어긋난다.
    // 원래 Die 카운트다운(#364)이 두고 쓰던 것인데 #524에서 그쪽이 폐지되며 함께 지워졌고,
    // 기절 마감(#477)이 같은 이유로 그대로 필요해 되살렸다.
    private double CurrentTime =>
        IsSpawned && NetworkManager != null ? NetworkManager.ServerTime.Time : Time.timeAsDouble;

    /// <summary>무력화 원인. 서버·오프라인은 실참조, 원격 피어는 동기화값으로 판정. (PlayerEscorter.IsEscorting 관례)</summary>
    public IncapacitationCause Cause => IsSpawned && !IsServer ? m_causeSynced.Value : m_cause;

    /// <summary>무력화(행동불능) 여부 — 이동·아이템·상호작용 게이트가 읽는다. 원인을 가리지 않는다.</summary>
    public bool IsIncapacitated => Cause != IncapacitationCause.None;

    /// <summary>현장 구조 대상인 HP 0 다운인지 — <b>#524 이후로는 항상 false</b>다(Down 진입 경로가 없다).
    /// 현장 구조를 되살릴 때를 위해 남겨 둔 판정이다. 쓰러졌는지 보려면 <see cref="IsOutOfAction"/>.</summary>
    public bool IsDowned => Cause == IncapacitationCause.Down;

    /// <summary>HP 0으로 기능 정지(Die)됐는지 — 운반·본부 부활(#365)의 대상 판정용. (#364, #524)</summary>
    public bool IsDead => Cause == IncapacitationCause.Die;

    /// <summary>
    /// 스스로도 남의 손으로도 곧 일어나지 못하는 상태 — 기능 정지(또는 휴면 상태인 다운). (#364)
    /// <b>전멸(게임오버) 판정과 조준 히트박스</b>가 이걸 본다. 매달기·기절은 시간이 지나면 스스로
    /// 풀리므로 포함하지 않는다 — 곧 일어날 사람을 세면 아무도 잃지 않았는데 게임오버가 뜬다. (#252)
    /// </summary>
    public bool IsOutOfAction => IsDowned || IsDead;

    /// <summary>테이저 피격 기절인지. 모션은 기능 정지와 같으므로(#252) 표시·집계처럼 원인을 구분할 때만 쓴다.</summary>
    public bool IsStunned => Cause == IncapacitationCause.Stun;

    /// <summary>
    /// 기절이 풀릴 때까지 남은 시간(초) — 기절이 아니면 0. 감전 연출이 잦아드는 시점 계산용. (#477)
    /// <b>서버 시각</b>(<see cref="CurrentTime"/>) 기준이라 모든 피어가 같은 값을 읽는다 — 늦게 접속해도
    /// 즉시 맞는다. (Die 카운트다운이 같은 방식을 쓰다 #524에서 폐지됐고, 이 값만 남았다)
    /// </summary>
    public float RemainingStunSeconds
    {
        get
        {
            if (!IsStunned)
                return 0f;

            double deadline = IsSpawned && !IsServer ? m_stunDeadlineSynced.Value : m_stunDeadline;
            return Mathf.Max(0f, (float)(deadline - CurrentTime));
        }
    }

    /// <summary>
    /// <b>쓰러진 자세인가</b> — 다운 모션·바닥 시점·몸 회전 잠금이 함께 보는 값이다. (#371 후속)
    ///
    /// 원래 이 셋은 <see cref="IsIncapacitated"/>를 직접 봤다. "무력화는 곧 쓰러진 자세"가 참이었기 때문인데,
    /// 외곽 린치(<see cref="IncapacitationCause.Lynched"/>)가 <b>서서 맞는</b> 무력화라 그 전제가 깨졌다.
    ///
    /// 세 곳이 <b>반드시 같은 값</b>을 봐야 한다는 것이 이 프로퍼티의 존재 이유다 — 하나만 갈라지면
    /// 몸은 서 있는데 카메라는 바닥에 있는 어긋남이 난다(#252에서 이미 한 번 밟은 함정이라
    /// PlayerAnimationDriver 주석에 경고로 남아 있었다).
    ///
    /// 행동 차단은 이 값이 아니라 <see cref="IsIncapacitated"/>가 계속 맡는다 — 린치 중에도
    /// 이동·아이템·상호작용은 전부 막힌다. 갈리는 것은 자세뿐이다.
    /// </summary>
    public bool IsProne => IsIncapacitated && Cause != IncapacitationCause.Lynched;

    // 살아 있는 인스턴스 목록 — 플레이어 전원을 훑어야 하는 쪽(전멸 판정 RoundManager)이
    // FindObjectsByType으로 씬 전체를 뒤지지 않게 한다. 조회는 배열을 새로 만드는 엔진 호출이라,
    // 자주 도는 검사에 넣으면 그 비용이 그대로 상시 비용이 된다. (#365에서 도입)
    // 활성/비활성 시점에 스스로 등록·해제하므로 스폰 여부·오프라인 테스트와 무관하게 정확하다.
    private static readonly List<PlayerIncapacitation> s_instances = new();

    /// <summary>현재 씬에 존재하는 모든 플레이어의 무력화 컴포넌트. 자주 순회해도 되는 무할당 목록. (#365)</summary>
    public static IReadOnlyList<PlayerIncapacitation> All => s_instances;

    private void OnEnable() => s_instances.Add(this);

    private void OnDisable() => s_instances.Remove(this);

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

        // 늦게 접속한 클라: 이미 쓰러진 플레이어의 현재 상태를 즉시 반영한다.
        // (OnValueChanged는 '변화' 시에만 발생하므로 스폰 시 한 번 맞춰줘야 한다)
        RefreshAimHitbox();
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

        RefreshAimHitbox();

        // 원인만 바뀌고 무력화 여부는 그대로면 알리지 않는다 — 구독자는 bool만 본다
        bool was = previous != IncapacitationCause.None;
        bool now = current != IncapacitationCause.None;
        if (was != now)
            OnIncapacitatedChanged?.Invoke(now);
    }

    // 조준 히트박스 = 쓰러져 있는 동안만 켠다. 서버·원격·오프라인 모든 인스턴스에서 실행된다.
    // 켜는 이유는 운반(#365) 조준이다 — 구조가 사라진 뒤로는(#524) 그게 유일한 용도다.
    private void RefreshAimHitbox()
    {
        if (m_reviveHitbox != null)
            m_reviveHitbox.SetActive(IsOutOfAction);
    }

    /// <summary>
    /// 무력화 진입 — 서버(또는 오프라인)에서만.
    /// HP0 기능 정지(#105/#524)·매달기(#101)·납치 호송(#371)이 호출한다.
    /// 기절은 스스로 풀려야 하므로 이 경로가 아니라 <see cref="ServerStun"/>을 쓴다.
    /// 기본값을 두지 않는다 — 원인이 곧 복구 경로라, 호출자가 반드시 밝히게 한다.
    /// </summary>
    public void Incapacitate(IncapacitationCause cause)
    {
        if (IsSpawned && !IsServer)
            return; // 서버 권위 방어 (PlayerEscorter 관례)

        if (cause == IncapacitationCause.None)
        {
            Debug.LogWarning(
                "PlayerIncapacitation: 원인 None으로는 무력화할 수 없다 — 해제는 Recover()",
                this
            );
            return;
        }

        // Die는 다른 무력화로 덮이지 않는다 — 덮으면 그 원인의 자동 해제에 딸려 공짜로 살아난다.
        // 실제 경로가 있다: 오검거 매달기 폴백(WrongfulArrestPenalty.HangAsync)은 대상이 이미
        // 무력화됐는지 보지 않고 Penalty를 걸어, 30초 뒤 Recover()로 Die까지 함께 풀어 버린다. (#364)
        // 모든 HP 0이 곧 Die가 된 뒤로는(#524) 이 가드가 걸릴 상황이 그만큼 늘었다.
        if (Cause == IncapacitationCause.Die)
        {
            Debug.Log(
                $"[Die] 무력화 덮어쓰기 무시 — {name}은 기능 정지 상태다 (요청 원인: {cause})",
                this
            );
            return;
        }

        SetCause(cause);
    }

    /// <summary>
    /// 기절 진입 — seconds 뒤 <b>스스로</b> 회복한다. 테이저 아군 오사(#252)가 호출. 서버 전용.
    /// 이미 무력화된 대상은 무시한다 — 기능 정지·매달기를 기절로 덮어쓰면 그 무력화가 기절 타이머에 일찍 풀린다.
    /// </summary>
    public void ServerStun(float seconds)
    {
        if (IsSpawned && !IsServer)
            return;
        if (IsIncapacitated)
            return;

        SetCause(IncapacitationCause.Stun);
        SetStunDeadline(CurrentTime + seconds); // 연출용 (#477) — SetCause 뒤에 둔다(거기서 0으로 지운다)
        ServerStunTimerAsync(seconds, ++m_stunEpisode).Forget();
    }

    /// <summary>
    /// 납치 처형 — 외곽에서 린치당한 피해자를 기능 정지(Die)로 확정한다. 서버(또는 오프라인) 전용. (#371 후속)
    ///
    /// 보통은 이 경로를 타지 않는다 — 린치로 HP가 0이 되면 <see cref="PlayerHealth"/>가 이미 Die를
    /// 걸어 뒀고(#524), 그때 이 메서드는 조용히 무동작으로 끝난다. 남겨 두는 이유는
    /// <b>린치 상한 초과 폴백</b>이다: 지형에 껴서 때리지 못한 채 상한이 지나면 HP가 남아 있는데도
    /// 결말을 집행해야 하는데(AbductionEvent.LynchAsync 참고), 그 경로에는 HP 0이 없다.
    ///
    /// 새 원인 값을 만들지 않는 근거는 납치 사망의 성질이 Die와 정확히 같다는 것이다 — 현장 구조로는
    /// 못 일어나고, 구조 창은 HP 0 이전에 이미 닫혔다(납치범을 때려 떼어내는 것이 유일한 구조 수단).
    /// </summary>
    public void ServerKillByAbduction()
    {
        if (IsSpawned && !IsServer)
            return;

        if (Cause == IncapacitationCause.Die)
            return; // 이미 기능 정지 — HP 0으로 먼저 확정된 보통의 경로다

        Debug.Log($"[납치] 처형 — 기능 정지: {name}", this);
        SetCause(IncapacitationCause.Die);
    }

    /// <summary>무력화 해제(부활·복구) — 서버(또는 오프라인)에서만.</summary>
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
                TimeSpan.FromSeconds(seconds),
                cancellationToken: destroyCancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            return; // 파괴·퇴장 — 복구할 대상이 이미 없다
        }

        // 내가 건 기절이 그대로일 때만 푼다 — 그 사이 기능 정지·매달기가 들어왔으면 남의 상태다
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

        // 기절에서 벗어나면 마감도 지운다 — 남겨 두면 다음 기절 전까지 옛 값이 읽힌다.
        // 기절로 '들어가는' 경우의 값은 ServerStun이 이 호출 직후에 채운다 (#477).
        if (cause != IncapacitationCause.Stun)
        {
            SetStunDeadline(0d);
        }

        RefreshAimHitbox();
        if (was != IsIncapacitated)
            OnIncapacitatedChanged?.Invoke(IsIncapacitated);
        OnAnyIncapacitatedChanged?.Invoke(); // 전역 훅 — RoundManager가 전원 행동불능(전멸) 여부를 재검사
    }

    // 기절 해제 예정 시각 갱신 — 실참조와 동기화값을 함께 쓴다(m_cause/m_causeSynced와 동일 관례).
    // 연출(#477)만 읽는다.
    private void SetStunDeadline(double deadline)
    {
        m_stunDeadline = deadline;
        if (IsSpawned && IsServer)
            m_stunDeadlineSynced.Value = deadline;
    }
}
