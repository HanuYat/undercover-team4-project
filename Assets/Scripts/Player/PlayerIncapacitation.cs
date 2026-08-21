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

    // HP 0 다운 (#105, #725) — 동료가 부활 키트 없이 맨손으로 일으킬 수 있는 유예 상태.
    // m_dieAfterDownSeconds 동안 방치되면 Die로 넘어간다. 전멸(게임오버) 판정 대상.
    Down,
    Penalty, // 오검거 광장 매달기 (#101) — 30초 뒤 자동 복귀
    Stun, // 테이저 피격 기절 (#252) — 시간이 지나면 스스로 일어난다
    Die, // 다운 방치 또는 확인사살로 기능 정지 (#364, #725) — 복구는 동료의 부활 키트(#613)
    Abducted, // 납치 호송 중 (#371) — 끌려가는 동안 걸어 나가지 못하게. 맨홀 아래로 내려가면 Die로 넘어간다 (#775)
    // (값은 반드시 끝에 추가한다 — NetworkVariable로 동기화되는 enum이라 순서가 곧 와이어 포맷이다)
}

/// <summary>
/// 플레이어 행동불능(무력화) 공통 기반. (#105)
/// HP 0 다운(#105/#725)·오검거 매달기(#101)·테이저 피격 기절(#252)·납치 호송(#371)은
/// 트리거만 다르고 결과=무력화로 같으므로, 무력화 상태 자체를 이 한 곳에서 서버 권위로 관리한다.
/// 이동·아이템·상호작용 컴포넌트가 <see cref="IsIncapacitated"/>를 읽어 각자 행동을 막는다.
///
/// 차이는 <b>풀리는 방식</b>이고, 그 구분이 <see cref="Cause"/>다:
///  · Down — HP 0으로 들어간다(#725). m_dieAfterDownSeconds초 안에 동료가 맨손으로 구조하면
///    (<see cref="ServerSetBeingRevived"/>) 복귀하고, 방치되면 Die로 넘어간다.
///  · Die — 다운 방치 또는 확인사살(<see cref="ServerFinishOff"/>)로 들어간다. 부활 키트로만 복구된다(#613).
///  · 매달기·기절 — 시간이 지나면 스스로 복귀하므로 운반 대상도, 전멸 판정 대상도 아니다.
///  · 납치 — 끌려가는 동안 걸어 나가지 못하게 한다(#371). 동료가 납치범을 떼어내면 풀리고,
///    떼어내지 못하면 맨홀 아래에서 Die로 확정된다 (#775).
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

    [Header("다운 유예 (#725)")]
    [Tooltip(
        "다운 상태로 이 시간(초)이 지나면 Die(기능 정지)로 전환된다 — 동료가 맨손으로 구조할 수 있는 제한시간"
    )]
    [SerializeField]
    private float m_dieAfterDownSeconds = 60f;

    /// <summary>다운 유예 전체 시간(초) — 화면 어두워짐이 <see cref="RemainingUntilDie"/>와 함께 비율을 낸다. (#725)</summary>
    public float DieAfterDownSeconds => m_dieAfterDownSeconds;

    // 서버 권위 무력화 원인 — 서버만 쓰고 모든 클라가 읽는다. (PlayerHealth.m_syncedHp와 동일 패턴)
    // 예전에는 bool 두 개(무력화 여부 + 구조 가능 여부)였는데, 기절이 들어오며 '구조 불가'가 둘로
    // 갈려(매달기·기절) 조합으로는 구분할 수 없게 됐다 — 원인 하나로 합쳤다. (#252)
    private readonly NetworkVariable<IncapacitationCause> m_causeSynced =
        new NetworkVariable<IncapacitationCause>();

    private IncapacitationCause m_cause; // 서버·오프라인의 진실값 (비네트워크 Play 테스트 폴백)

    // 몸이 회수 불가능한 곳으로 사라졌는가 — 납치 결말(맨홀 하강, #775). m_cause와 같은 이중 구조다.
    // IsDead와 별도 값인 이유: 라운드 아웃·유치장 계상·약탈·관전은 Die 그대로여야 하고, 달라지는 것은
    // '부활 대상인가' 하나뿐이다 — enum 값을 늘리면 Die를 보는 곳 전부가 함께 뒤집힌다.
    private readonly NetworkVariable<bool> m_bodyLostSynced = new NetworkVariable<bool>();
    private bool m_bodyLost;

    // 사망 전 오너 — 이관은 서버만 하고 복귀도 서버가 하므로 동기화하지 않는다. (#763 1단계)
    private ulong m_ownerBeforeDeath;
    private bool m_ownershipMovedToServer;

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
    // Die 카운트다운(#364)·기절 마감(#477)·구조 완료 시각(#725)이 모두 이 기준을 공유한다.
    private double CurrentTime =>
        IsSpawned && NetworkManager != null ? NetworkManager.ServerTime.Time : Time.timeAsDouble;

    // Die 타이머 회차 토큰 — 원인이 바뀔 때마다 올라간다. 기절 회차(m_stunEpisode)와 같은 장치. (#364, #725)
    private int m_causeEpisode;

    // Die 전환 예정 시각 — 다운이 아니면 0. m_stunDeadlineSynced와 같은 이중 구조. (#725)
    private readonly NetworkVariable<double> m_downDeadlineSynced = new NetworkVariable<double>();
    private double m_downDeadline;

    // 구조 채널링 중 얼어붙은 잔여 시간 — 채널링 시작/종료 시 한 번씩만 쓴다. (#725)
    private readonly NetworkVariable<float> m_downFrozenRemainingSynced =
        new NetworkVariable<float>();
    private float m_downFrozenRemaining;

    // 구조 완료 예정 시각 — 0이면 구조 중이 아니다. 재부팅 진행 링이 이 값을 읽는다. (#725)
    private readonly NetworkVariable<double> m_reviveEndSynced = new NetworkVariable<double>();
    private double m_reviveEnd;

    /// <summary>무력화 원인. 서버·오프라인은 실참조, 원격 피어는 동기화값으로 판정. (PlayerEscorter.IsEscorting 관례)</summary>
    public IncapacitationCause Cause => IsSpawned && !IsServer ? m_causeSynced.Value : m_cause;

    /// <summary>무력화(행동불능) 여부 — 이동·아이템·상호작용 게이트가 읽는다. 원인을 가리지 않는다.</summary>
    public bool IsIncapacitated => Cause != IncapacitationCause.None;

    /// <summary>현장 구조 대상인 HP 0 다운인지 — <b>구조(리바이브) 대상은 이것만</b>이다. 매달기·기절은
    /// 스스로 풀리고, Die는 현장 구조로 못 일어난다(부활 키트만, #364/#613/#725).</summary>
    public bool IsDowned => Cause == IncapacitationCause.Down;

    /// <summary>다운 방치 또는 확인사살로 기능 정지(Die)됐는지 — 운반·본부 부활(#365)의 대상 판정용. (#364, #725)</summary>
    public bool IsDead => Cause == IncapacitationCause.Die;

    /// <summary>몸이 회수 불가능한 곳으로 사라졌는가 — 맨홀 납치 결말. (#775)</summary>
    public bool IsBodyLost => IsSpawned && !IsServer ? m_bodyLostSynced.Value : m_bodyLost;

    /// <summary>부활 키트·본부 장치로 일으킬 수 있는가 — 기능 정지 중이고 몸이 남아 있을 때만. (#775)</summary>
    public bool IsRevivable => IsDead && !IsBodyLost;

    /// <summary>
    /// 스스로도 남의 손으로도 곧 일어나지 못하는 상태 — 다운 또는 Die. (#364, #725)
    /// <b>전멸(게임오버) 판정과 조준 히트박스</b>가 이걸 본다: 다운만 세면 전원이 Die로 넘어간 순간
    /// 판정이 통과하지 못해 게임오버가 영영 뜨지 않는다(RoundManager.AreAllPlayersOutOfAction).
    /// 매달기·기절은 시간이 지나면 스스로 풀리므로 포함하지 않는다. (#252)
    /// </summary>
    public bool IsOutOfAction => IsDowned || IsDead;

    /// <summary>테이저 피격 기절인지. 모션은 기능 정지와 같으므로(#252) 표시·집계처럼 원인을 구분할 때만 쓴다.</summary>
    public bool IsStunned => Cause == IncapacitationCause.Stun;

    /// <summary>
    /// 기절이 풀릴 때까지 남은 시간(초) — 기절이 아니면 0. 감전 연출이 잦아드는 시점 계산용. (#477)
    /// <b>서버 시각</b>(<see cref="CurrentTime"/>) 기준이라 모든 피어가 같은 값을 읽는다 — 늦게 접속해도
    /// 즉시 맞는다. Die 카운트다운(<see cref="RemainingUntilDie"/>)이 같은 방식을 쓴다.
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
    /// Die 전환까지 남은 시간(초) — 다운이 아니면 0. 화면 어두워짐과 HUD 카운트다운이 같은 값을 읽는다. (#364, #725)
    /// 구조 채널링 중에는(<see cref="IsBeingRevived"/>) 얼어붙은 값을 그대로 돌려준다 — 시계가 멈춘 것으로 보인다.
    /// </summary>
    public float RemainingUntilDie
    {
        get
        {
            if (!IsDowned)
                return 0f;

            if (IsBeingRevived)
                return IsSpawned && !IsServer
                    ? m_downFrozenRemainingSynced.Value
                    : m_downFrozenRemaining;

            double deadline = IsSpawned && !IsServer ? m_downDeadlineSynced.Value : m_downDeadline;
            return Mathf.Max(0f, (float)(deadline - CurrentTime));
        }
    }

    /// <summary>
    /// 구조 채널링이 진행 중인지 — 쓰러진 본인의 재부팅 진행 표시(진행 링)가 이 값을 본다. (#725)
    /// 참인 동안 <see cref="RemainingUntilDie"/>가 얼어붙는다.
    /// </summary>
    public bool IsBeingRevived => RemainingReviveSeconds > 0f;

    /// <summary>구조 채널링이 끝날 때까지 남은 시간(초) — 구조 중이 아니면 0. 재부팅 진행 링 계산용. (#725)</summary>
    public float RemainingReviveSeconds
    {
        get
        {
            double end = IsSpawned && !IsServer ? m_reviveEndSynced.Value : m_reviveEnd;
            return Mathf.Max(0f, (float)(end - CurrentTime));
        }
    }

    /// <summary>
    /// <b>쓰러진 자세인가</b> — 다운 모션·바닥 시점·몸 회전 잠금이 <b>반드시 같은 값</b>을 보게 모아 둔 자리다.
    /// 하나만 갈라지면 몸은 서 있는데 카메라는 바닥에 있는 어긋남이 난다 (#252에서 밟은 함정).
    ///
    /// 지금은 무력화와 값이 같다 — 서서 맞던 외곽 린치가 사라지면서 예외가 없어졌다 (#775).
    /// </summary>
    public bool IsProne => IsIncapacitated;

    // 살아 있는 인스턴스 목록 — 플레이어 전원을 훑어야 하는 쪽(전멸 판정 RoundManager)이
    // FindObjectsByType으로 씬 전체를 뒤지지 않게 한다. 조회는 배열을 새로 만드는 엔진 호출이라,
    // 자주 도는 검사에 넣으면 그 비용이 그대로 상시 비용이 된다. (#365에서 도입)
    // 활성/비활성 시점에 스스로 등록·해제하므로 스폰 여부·오프라인 테스트와 무관하게 정확하다.
    private static readonly List<PlayerIncapacitation> s_instances = new();

    /// <summary>현재 씬에 존재하는 모든 플레이어의 무력화 컴포넌트. 자주 순회해도 되는 무할당 목록. (#365)</summary>
    public static IReadOnlyList<PlayerIncapacitation> All => s_instances;

    // 같은 오브젝트의 밧줄 연결 — 무력화 진입에서만 쓴다. 테스트 구성 등 없을 수 있어 null 허용
    // (PlayerEscorter가 RopeDragLoad를 지연 조회하는 것과 같은 관례).
    private PlayerEscorter m_escorter;

    private PlayerEscorter Escorter
    {
        get
        {
            if (m_escorter == null)
                m_escorter = GetComponent<PlayerEscorter>();
            return m_escorter;
        }
    }

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
        m_bodyLostSynced.OnValueChanged += HandleBodyLostSyncedChanged;

        // 늦게 접속한 클라: 이미 쓰러진 플레이어의 현재 상태를 즉시 반영한다.
        // (OnValueChanged는 '변화' 시에만 발생하므로 스폰 시 한 번 맞춰줘야 한다)
        RefreshAimHitbox();
    }

    public override void OnNetworkDespawn()
    {
        m_causeSynced.OnValueChanged -= HandleSyncedChanged;
        m_bodyLostSynced.OnValueChanged -= HandleBodyLostSyncedChanged;
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

    // body-lost 값이 늦게 도착해도(#775) 원격 클라의 조준 히트박스를 맞춘다. 서버는 SetBodyLost에서 직접 갱신.
    private void HandleBodyLostSyncedChanged(bool previous, bool current)
    {
        if (IsServer)
            return;

        RefreshAimHitbox();
    }

    // 조준 히트박스 = 쓰러져 있고(다운·Die) 몸이 회수 가능할 때만 켠다. 서버·원격·오프라인 모든
    // 인스턴스에서 실행된다. 다운은 구조자가, Die는 운반자·부활 키트 사용자가 조준해야 하므로 둘 다
    // 켠다. (#105, #364, #725) 몸이 지하로 사라지면(#775) 부활·운반·뒤지기가 전부 이 한 곳에서 닫힌다.
    private void RefreshAimHitbox()
    {
        if (m_reviveHitbox != null)
            m_reviveHitbox.SetActive(IsOutOfAction && !IsBodyLost);
    }

    /// <summary>
    /// 무력화 진입 — 서버(또는 오프라인)에서만.
    /// HP0 다운(#105/#725)·매달기(#101)·납치 호송(#371)이 호출한다.
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

        // 다운·Die는 다른 무력화로 덮이지 않는다 — 덮으면 그 원인의 자동 해제에 딸려 공짜로 살아난다.
        // 실제 경로가 있다: 오검거 매달기 폴백(WrongfulArrestPenalty.HangAsync)은 대상이 이미
        // 무력화됐는지 보지 않고 Penalty를 걸어, 30초 뒤 Recover()로 다운·Die까지 함께 풀어 버린다. (#364, #725)
        if (Cause == IncapacitationCause.Die || Cause == IncapacitationCause.Down)
        {
            Debug.Log(
                $"[다운/Die] 무력화 덮어쓰기 무시 — {name}은 이미 쓰러져 있다 (요청 원인: {cause})",
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
    /// 납치 결말 — 맨홀 아래로 내려간 피해자를 기능 정지(Die)로 확정한다. 서버(또는 오프라인) 전용. (#775)
    ///
    /// 납치는 피해자를 때리지 않으므로 HP 0을 거치지 않는다 — 이 메서드가 유일한 진입점이다.
    /// </summary>
    public void ServerKillByAbduction()
    {
        if (IsSpawned && !IsServer)
            return;

        // 하강 중 폭탄 등 외부 사유로 이미 Die가 걸렸어도 "몸이 맨홀 아래"는 참이다 — 중복 호출
        // 방어(아래 return) 앞에 세운다.
        SetBodyLost(true);

        if (Cause == IncapacitationCause.Die)
            return; // 이미 기능 정지 — 중복 호출 방어

        Debug.Log($"[납치] 결말 — 기능 정지: {name}", this);
        SetCause(IncapacitationCause.Die);
    }

    /// <summary>
    /// 유예 중인 몸을 즉시 완전 사망으로 — 유예 중 확인사살(환경·NPC·아군 진압봉 모두). 서버(또는 오프라인) 전용. (#725)
    /// </summary>
    public void ServerFinishOff()
    {
        if (IsSpawned && !IsServer)
            return;
        if (!IsDowned)
            return; // 유예 중이 아니면 확인사살할 대상이 없다

        Debug.Log($"[Die] 유예 확인사살 — 기능 정지: {name}", this);
        SetCause(IncapacitationCause.Die);
    }

    /// <summary>
    /// 구조 채널링 시작/종료를 알린다 — <see cref="PlayerReviver"/>가 호출한다. 서버(또는 오프라인) 전용. (#725)
    /// 시작하면 Die 타이머를 얼리고, 종료하면(완료·취소 무관) 얼어붙은 잔여로 되살려 다시 건다 —
    /// <b>시계는 멈출 뿐 되감기지 않는다.</b>
    /// </summary>
    /// <param name="active">채널링 시작이면 true, 종료면 false.</param>
    /// <param name="channelSeconds">시작 시에만 의미 있음 — 재부팅 진행 링 계산용 채널링 소요 시간.</param>
    public void ServerSetBeingRevived(bool active, float channelSeconds = 0f)
    {
        if (IsSpawned && !IsServer)
            return;
        if (!IsDowned)
            return; // 유예 중이 아니면 구조 대상이 아니다

        if (active)
        {
            // 얼기 전 값을 스냅샷 — SetReviveEnd 이후엔 RemainingUntilDie가 이 값을 읽게 된다
            m_downFrozenRemaining = RemainingUntilDie;
            SetDownFrozenRemaining(m_downFrozenRemaining);
            SetReviveEnd(CurrentTime + channelSeconds);
            m_causeEpisode++; // 돌던 ServerDieTimerAsync 무효화
        }
        else
        {
            SetDownDeadline(CurrentTime + m_downFrozenRemaining);
            SetReviveEnd(0d);
            ServerDieTimerAsync(m_downFrozenRemaining, ++m_causeEpisode).Forget();
        }
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

        // 내가 건 기절이 그대로일 때만 푼다 — 그 사이 다운·기능 정지·매달기가 들어왔으면 남의 상태다
        if (m_stunEpisode != episode || m_cause != IncapacitationCause.Stun)
            return;

        Recover();
    }

    // 다운 유예 → Die 전환 타이머. 기절 타이머와 같은 구조지만 방향이 반대다. seconds를 받는 이유는
    // 초회 진입(60초)과 구조 재개(얼어붙은 잔여)가 길이만 다르기 때문이다. (#364, #725)
    private async UniTaskVoid ServerDieTimerAsync(float seconds, int episode)
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
            return; // 파괴·퇴장 — 전환할 대상이 이미 없다
        }

        // 내가 본 다운이 그대로일 때만 떨어뜨린다 — 구조됐거나 다른 무력화로 바뀌었거나 구조
        // 채널링으로 회차가 올랐으면(m_causeEpisode 불일치) 남의 상태다
        if (m_causeEpisode != episode || m_cause != IncapacitationCause.Down)
            return;

        Debug.Log($"[Die] 유예 시간 경과 — 기능 정지: {name} (부활 키트만 남는다)", this);
        SetCause(IncapacitationCause.Die);
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

        // 사망 구간에는 <b>서버가 이 몸의 주인이다</b> — 시체를 NPC와 같은 조건으로 만든다. (#763 1단계)
        ApplyDeathOwnership(cause);

        // 기절에서 벗어나면 마감도 지운다 — 남겨 두면 다음 기절 전까지 옛 값이 읽힌다.
        // 기절로 '들어가는' 경우의 값은 ServerStun이 이 호출 직후에 채운다 (#477).
        if (cause != IncapacitationCause.Stun)
        {
            SetStunDeadline(0d);
        }

        // Die를 벗어나면(구조·부활·라운드 리셋) 몸 회수 불가 플래그도 지운다 — 남겨 두면 다음
        // 사망이 이전 회차의 맨홀 낙인을 그대로 물려받는다. (#775)
        if (cause != IncapacitationCause.Die)
        {
            SetBodyLost(false);
        }

        // 원인이 바뀌었으니 이전 회차의 Die 타이머는 무효다 — 구조 후 재다운도 새 회차로 다시 센다. (#364, #725)
        m_causeEpisode++;
        if (cause == IncapacitationCause.Down)
        {
            SetDownDeadline(CurrentTime + m_dieAfterDownSeconds);
            SetReviveEnd(0d);
            ServerDieTimerAsync(m_dieAfterDownSeconds, m_causeEpisode).Forget();
        }
        else
        {
            SetDownDeadline(0d);
            SetReviveEnd(0d);
        }

        // 쓰러지는 순간 밧줄에서 손을 뗀다 (#559). 무력화된 몸은 줄을 당길 수 없는데, 끌기는 <b>끄는
        // 쪽의 트랜스폼</b>만 따라가므로 그 몸을 옮기는 다른 시스템이 NPC까지 함께 옮겨 버린다 —
        // 납치 호송(#371)과 오검거 호송(#279)이 그렇다. 특히 납치 반출은 맵 밖으로 나가는데
        // 끌기 위치 보정은 NavMesh로 묶이지 않아, 딸려나간 NPC는 줄이 풀릴 때 NavMesh에 다시
        // 붙지 못하고 맵 밖에 굳는다(잡아 둔 진범이 그대로 사라진다).
        //
        // 호송 쪽에서 각각 놓게 하지 않고 여기 두는 이유는, 트랜스폼을 옮기는 시스템이 늘 때마다
        // 같은 것을 기억해야 하기 때문이다. '무력화되면 손을 놓는다'는 원인을 가리지 않는다.
        //
        // <b>줄은 그대로 남는다</b> — ReleaseDrag는 끌기만 멈추고 묶임은 유지한다(E 놓기와 같다).
        // 그래서 스스로 풀리는 무력화(기절·매달기)에서 깨어나면 걸어가 E로 다시 끌 수 있고,
        // NPC는 놓인 자리에서 Captured로 멈춘다 — 그 자리는 아직 맵 안이다.
        if (!was && IsIncapacitated)
            Escorter?.ReleaseAllDrags();

        RefreshAimHitbox();
        if (was != IsIncapacitated)
            OnIncapacitatedChanged?.Invoke(IsIncapacitated);
        OnAnyIncapacitatedChanged?.Invoke(); // 전역 훅 — RoundManager가 전원 행동불능(전멸) 여부를 재검사
    }

    /// <summary>
    /// 사망 중 이 <see cref="NetworkObject"/>의 주인을 <b>서버로 옮기고</b>, 풀리면 돌려준다. (#763 1단계)
    ///
    /// <b>왜 소유권인가.</b> 시체의 자세를 흘리는 <c>RagdollPoseStreamer</c>도, 루트
    /// <c>NetworkTransform</c>도 권위를 <b>오너</b>로 두고 있다(루트 NT의 <c>AuthorityMode</c>가
    /// Owner인 것은 살아있는 이동이 요구하는 값이다). 그래서 둘 중 하나만 서버로 바꾸면 몸과 루트가
    /// 서로 다른 피어에서 계산돼 시체가 이름표를 두고 떠난다. <b>오너 자체를 서버로 만들면 둘 다
    /// 서버를 가리키고 고칠 배선이 없다.</b>
    ///
    /// 얻는 것은 밧줄 견인의 왕복이다. 지금은 끄는 사람(클라 B) → 서버 → 시체 오너(클라 A)가 장력을
    /// 계산 → 서버 → B가 자세 수신으로 <b>4홉</b>이라, B의 화면에서 시체가 무겁고 줄이 끊긴다.
    /// 서버가 주인이면 NPC와 같은 <b>2홉</b>이 된다. (계획서 docs/763-player-ragdoll-npc-parity.md §1)
    ///
    /// ⚠ <b>호스트 자신의 몸은 옮길 것이 없다</b> — 이미 서버가 주인이다. 그때는 깃발도 세우지 않아
    /// 복귀에서도 아무 일이 일어나지 않는다.
    /// </summary>
    private void ApplyDeathOwnership(IncapacitationCause cause)
    {
        if (!IsSpawned || !IsServer || NetworkObject == null || NetworkManager == null)
            return;

        bool wantsServerOwner = cause == IncapacitationCause.Die;
        if (wantsServerOwner == m_ownershipMovedToServer)
            return;

        if (wantsServerOwner)
        {
            m_ownerBeforeDeath = OwnerClientId;
            if (m_ownerBeforeDeath == NetworkManager.ServerClientId)
                return; // 호스트의 몸 — 이미 서버 소유다

            m_ownershipMovedToServer = true;
            NetworkObject.ChangeOwnership(NetworkManager.ServerClientId);
            // LogOwnership("사망", m_ownerBeforeDeath, NetworkManager.ServerClientId);
            return;
        }

        m_ownershipMovedToServer = false;

        // ⚠ 나간 클라에게 돌려주지 않는다 — 없는 클라를 오너로 지정하면 NGO가 예외를 던진다.
        // 그 몸은 서버 소유로 남고, 정리는 접속 종료 경로가 한다(#287).
        if (!NetworkManager.ConnectedClients.ContainsKey(m_ownerBeforeDeath))
            return;

        NetworkObject.ChangeOwnership(m_ownerBeforeDeath);
        // LogOwnership("복귀", NetworkManager.ServerClientId, m_ownerBeforeDeath);
    }

    // ⚠ #759/#763 계측 — A-1 이관이 실측으로 확인돼 주석 처리했다(2026-08-20).
    //    호출부 둘도 같이 막혀 있다. 근거: docs/759-ragdoll-slowmotion-handoff.md §3-1-3
    /*
    // ⚠ 임시 계측 (#763 A-1) — 이관이 실제로 걸렸는지와, <b>플레이어 오브젝트 연결이 유지되는지</b>를
    // 함께 찍는다. 후자는 이 프로젝트에서 플레이어 오브젝트의 소유권을 옮기는 것이 처음이라 확인이
    // 필요한 항목이다(계획서 §4 함정 2). 확정되면 지운다.
    private void LogOwnership(string reason, ulong from, ulong to)
    {
        bool keptPlayerObject =
            NetworkManager.ConnectedClients.TryGetValue(m_ownerBeforeDeath, out NetworkClient client)
            && client.PlayerObject == NetworkObject;

        Debug.Log(
            $"[소유권] 시체#{NetworkObject.NetworkObjectId} 원인={reason} 오너 {from}→{to} "
                + $"플레이어오브젝트유지={keptPlayerObject}",
            this
        );
    }
    */

    // 기절 해제 예정 시각 갱신 — 실참조와 동기화값을 함께 쓴다(m_cause/m_causeSynced와 동일 관례).
    // 연출(#477)만 읽는다.
    private void SetStunDeadline(double deadline)
    {
        m_stunDeadline = deadline;
        if (IsSpawned && IsServer)
            m_stunDeadlineSynced.Value = deadline;
    }

    // 몸 회수 불가 플래그 갱신 — 실참조와 동기화값을 함께 쓴다(m_cause/m_causeSynced와 동일 관례). (#775)
    private void SetBodyLost(bool value)
    {
        m_bodyLost = value;
        if (IsSpawned && IsServer)
            m_bodyLostSynced.Value = value;
        RefreshAimHitbox(); // 서버·오프라인은 OnValueChanged가 오지 않으므로 여기서 직접 맞춘다
    }

    // Die 전환 예정 시각 갱신 — 실참조와 동기화값을 함께 쓴다(m_cause/m_causeSynced와 동일 관례). (#725)
    private void SetDownDeadline(double deadline)
    {
        m_downDeadline = deadline;
        if (IsSpawned && IsServer)
            m_downDeadlineSynced.Value = deadline;
    }

    // 구조 채널링 중 얼어붙은 잔여 시간 갱신 — 같은 이중 구조. (#725)
    private void SetDownFrozenRemaining(float remaining)
    {
        m_downFrozenRemaining = remaining;
        if (IsSpawned && IsServer)
            m_downFrozenRemainingSynced.Value = remaining;
    }

    // 구조 완료 예정 시각 갱신 — 같은 이중 구조. 0이면 구조 중이 아니다. (#725)
    private void SetReviveEnd(double end)
    {
        m_reviveEnd = end;
        if (IsSpawned && IsServer)
            m_reviveEndSynced.Value = end;
    }
}
