using Unity.Netcode;
using UnityEngine;

/// <summary>
/// NPC 한 명의 신원 — 런타임에 배정되는 시민 프로필과 범인 여부를 들고 있다. (이슈 #38)
/// 배정은 서버의 CriminalAssigner가 담당하고, 공개 가능한 신원(이름·타입·세력)은
/// NetworkVariable로 전 클라이언트에 동기화된다 (#52) — 뒤늦게 접속한 클라이언트도
/// 스폰 시점 초기 동기화로 현재 값을 받는다. 스캐너(#39)·진범 판정(#41) 등이 읽는다.
/// IsCriminal(정답)·Reaction(검거 반응)·Appearance(몽타주 기준)는 서버 전용 — 동기화하지 않는다.
/// NetworkBehaviour이므로 런타임 AddComponent 불가 — NPC 프리팹에 미리 부착돼 있어야 한다.
/// </summary>
public class CitizenIdentity : NetworkBehaviour
{
    [Header("공식 기록 (세력 심볼 조회용)")]
    [Tooltip("클라이언트가 동기화 수신값으로 프로필을 재조립할 때 쓴다 — NPC 프리팹에서 할당")]
    [SerializeField]
    private OfficialRecords m_officialRecords;

    // 서버 권위 신원 동기화 — 서버만 쓰고 전 피어가 읽는다.
    // 프로필 인스턴스(런타임 ScriptableObject)는 복제되지 않으므로 원시 데이터만 실어 나른다 (#52)
    private readonly NetworkVariable<CitizenData> m_syncedData = new NetworkVariable<CitizenData>();

    /// <summary>
    /// 이 NPC의 시민 프로필. 서버(또는 오프라인)는 배정 시, 클라이언트는 동기화 수신 시 채워진다.
    /// 배정 전에는 null — 스캐너는 이 경우를 "프로필 미배정"으로 안내한다.
    /// </summary>
    public CitizenProfile Profile { get; private set; }

    /// <summary>
    /// 실제 범인 여부 — 진범 판정(#41)의 정답 기준. 서버 전용 (클라이언트에서는 항상 false).
    /// 곧 공개 플래그이기도 하다 (#102): 라운드 시작에 확정된 예비 용의자는 false로 대기하다가
    /// 제보 전화 승격 시 켜진다. 대기 중에 잡으면 오검거로 판정된다 (GDD 7-3과 일치).
    /// </summary>
    public bool IsCriminal { get; private set; }

    /// <summary>
    /// 위조범 여부 — 스캔 표시 정보(m_nameView 등)가 인명부 정본과 어긋나는 NPC. 위조 검거 판정(#320)의 기준.
    /// IsCriminal과 동일하게 서버 전용이며 동기화하지 않는다 (클라이언트에서는 항상 false).
    /// 진범과 독립 배정되어 겹칠 수 있고, 판정 시 진범(IsCriminal)이 우선한다.
    /// </summary>
    public bool IsForger { get; private set; }

    /// <summary>외형 특징 조합(#74) — 몽타주 부합 판정의 기준. AppearanceAssigner가 채워준다. 서버 전용.</summary>
    public AppearanceProfile Appearance { get; private set; } = AppearanceProfile.Unassigned;

    /// <summary>검거 반응 유형(#76) — 수갑 채널링 성공 순간의 반응. CriminalAssigner가 배정한다. 서버 전용.</summary>
    public ReactionType Reaction { get; private set; } = ReactionType.Compliant;

    // ---- 동기화 수신 (클라이언트) ----

    public override void OnNetworkSpawn()
    {
        // 서버 본인은 배정 시 Profile을 직접 세팅하므로 재조립이 필요 없다 — 클라이언트만 수신한다
        if (IsServer)
            return;

        m_syncedData.OnValueChanged += HandleSyncedDataChanged;

        // 뒤늦게 접속한 클라이언트는 스폰 시점에 이미 배정된 값이 실려 온다 — 즉시 반영.
        // (스폰이 배정보다 먼저인 첫 접속 클라는 비어 있다가 OnValueChanged로 받는다)
        if (m_syncedData.Value.IsAssigned)
            RebuildProfile(m_syncedData.Value);
    }

    public override void OnNetworkDespawn()
    {
        m_syncedData.OnValueChanged -= HandleSyncedDataChanged;
    }

    private void HandleSyncedDataChanged(CitizenData previous, CitizenData current)
    {
        if (current.IsAssigned)
            RebuildProfile(current);
    }

    // 동기화 수신값으로 로컬 프로필 인스턴스를 재조립한다 — 클라이언트 전용.
    // 서버의 원본과 같은 공개 데이터를 갖는 별도 인스턴스이며, 스캐너/UI는 차이를 모른다.
    private void RebuildProfile(CitizenData data)
    {
        CitizenProfile profile = ScriptableObject.CreateInstance<CitizenProfile>();
        profile.Initialize(data.Name.ToString(), data.Type, data.Faction, data.SymbolIndex, m_officialRecords);
        profile.m_nameView = data.NameView.ToString(); // 위조된 표시 이름 반영 — 정상 시민은 정본과 동일 (#223)
        Profile = profile;
    }

    // ---- 배정 (서버 · 오프라인 전용) ----

    /// <summary>
    /// 프로필과 범인 여부를 배정한다. 서버(또는 오프라인)의 CriminalAssigner 전용.
    /// 공개 가능한 부분은 동기화 변수에 함께 기록되어 전 클라이언트에 전파된다 —
    /// 오프라인(미스폰)에서는 로컬 프로퍼티만으로 동작한다 (NpcController.m_networkState와 동일 이중 구조).
    /// </summary>
    public void AssignProfile(CitizenProfile profile, bool isCriminal)
    {
        Profile = profile;
        IsCriminal = isCriminal;

        if (IsSpawned && IsServer)
            m_syncedData.Value = CitizenData.FromProfile(profile);
    }

    /// <summary>
    /// 범인 여부만 바꾼다 — 프로필은 그대로 둔다. 제보 전화 승격(#102) 전용.
    /// 프로필까지 다시 배정하면 CitizenData 동기화 스냅샷이 재전송되어, 본부가 보고 있던
    /// 스캔 표시값이 이유 없이 깜빡인다. IsCriminal은 서버 전용이라 동기화할 것이 없다.
    /// </summary>
    public void SetCriminal(bool isCriminal)
    {
        IsCriminal = isCriminal;
    }

    /// <summary>외형 특징 조합을 배정한다. AppearanceAssigner 전용. (서버 전용 — 동기화 없음)</summary>
    public void AssignAppearance(AppearanceProfile appearance)
    {
        Appearance = appearance;
    }

    /// <summary>위조범 여부를 배정한다. CriminalAssigner 전용. (서버 전용 — 동기화 없음, #320)</summary>
    public void AssignForgery(bool isForger)
    {
        IsForger = isForger;
    }

    /// <summary>
    /// 검거 반응 유형을 배정한다. (서버 전용 — 동기화 없음)
    /// CriminalAssigner가 라운드 시작에 배정하고, 순응형이 피격으로 도주·저항을 뽑았을 때
    /// NpcController가 그 결과를 여기에 1회 확정한다 (#400).
    /// </summary>
    public void AssignReaction(ReactionType reaction)
    {
        Reaction = reaction;
    }
}
