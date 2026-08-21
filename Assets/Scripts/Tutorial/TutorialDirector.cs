using System;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 튜토리얼 진행 (#663) — 로컬 호스트를 띄우고, 안내 문구를 단계별로 갈아 끼운다.
/// 튜토리얼 씬에만 있고 그 씬에 이것이 있다는 사실이 곧 "지금이 튜토리얼인가"의 판별 기준이다
/// (씬 자체는 <see cref="EScene.Game"/>으로 분류된다 — 이유는 AppHelper.FromSceneName 주석).
///
/// <b>매니저가 아니다</b> — 이걸 참조하는 곳이 없으므로 App에 올리지 않는다 (R3).
/// ConnectionLostToastView와 같은 결의 씬 부착 컴포넌트다.
///
/// <b>단계 판정은 폴링이다.</b> 열 가지 조건을 위해 시스템 열 곳에 이벤트를 새로 뚫는 대신,
/// 이미 있는 이벤트만 구독해 깃발을 세우고 나머지는 매 프레임 상태를 읽는다. 도는 것은
/// 한 번에 조건 하나뿐이라 비용이 사실상 없다.
/// </summary>
public class TutorialDirector : MonoBehaviour
{
    private const string k_table = "HudTable";
    private const string k_keyPrefix = "Hud.Tutorial.";

    // 코드가 이름으로 집어 쓰는 단계 키 — 아래 s_steps가 이 상수를 그대로 쓴다.
    // 리터럴을 양쪽에 따로 적으면 한쪽만 고쳤을 때 비교가 조용히 안 맞는다.
    private const string k_slotsKey = "Slots"; // 부팅 장착 이벤트를 거르는 기준 (HandleEquippedItemChanged)
    private const string k_fieldStartKey = "Scan"; // 여기부터 현장 — 들어설 때 본부 대문을 연다

    /// <summary>단계 하나 — 문구 키와 "끝났는가" 판정.</summary>
    private readonly struct Step
    {
        public readonly string Key;
        public readonly Func<TutorialDirector, bool> IsDone;

        public Step(string key, Func<TutorialDirector, bool> isDone)
        {
            Key = key;
            IsDone = isDone;
        }
    }

    // 순서가 곧 진행 순서다. 조작 → 본부에서 보는 법 → 현장 한 사이클(대조·무력화·연행·인계) 순으로,
    // 앞 단계에서 배운 것만으로 다음 단계를 할 수 있게 늘어놓았다.
    private static readonly Step[] s_steps =
    {
        new Step("Move", d => d.m_movedDistance >= d.m_moveDistance),
        new Step(k_slotsKey, d => d.m_slotChanged),
        new Step("Aim", d => d.m_interactor != null && d.m_interactor.CurrentInteractable != null),
        new Step("TeamTab", d => d.m_teamPanelSeen),
        new Step("Cctv", d => d.m_cctvSwitched),
        new Step(k_fieldStartKey, d => d.m_scanned),
        new Step("Subdue", d => d.AnyNpcStunned),
        new Step("Rope", d => d.m_escorter != null && d.m_escorter.IsDraggingAny),
        // 인계와 판정은 <b>수감 버튼 한 번</b>에 함께 일어난다(JailIntake가 판정을 먼저 돌린다) —
        // 그래서 조건이 같고, 문구만 둘로 나눠 읽힌다(최소 표시 시간이 순서를 지켜 준다).
        //
        // ⚠ <b>감옥 수용(JailZone) 이벤트로 재지 않는다.</b> 그쪽은 <b>계상되는 판정</b>에서만 울리므로
        // (ArrestVerdictRules.IsCredited) 생포 조건 불충족·오검거는 아무것도 쏘지 않아, 그렇게 인계한
        // 플레이어가 반응 없는 화면 앞에 영영 갇혔다. 판정은 어느 결과로든 나므로 그것을 기준으로 삼는다.
        new Step("Jail", d => d.m_judged),
        // 오검거는 체험시키지 않는다 — 지금 페널티가 꺼져 있어(#612) 가르칠 "대가"가 정산 코믹 스탯뿐이다.
        // 대신 판정을 한 번 받아 보게 하고, 진범이 아니면 돈이 들어오지 않는다는 것만 문구로 알린다.
        new Step("Verdict", d => d.m_judged),
    };

    [Header("배선")]
    [Tooltip("본부 CCTV 콘솔의 모니터 — 채널을 한 번 돌렸는지 본다. HQ/Interior/CCTVConsole/CCTVMonitor")]
    [SerializeField]
    private CCTVSwitcher m_cctv;

    [Tooltip("본부 대문 전부 — 본부 학습이 끝나면 자동으로 연다. HQ/Doors/* (남·북 두 짝)")]
    [SerializeField]
    private DoubleDoor[] m_hqGates;

    [Header("단계 기준값")]
    [Tooltip("이동 단계를 통과시킬 누적 이동 거리(m)")]
    [Min(0f)]
    [SerializeField]
    private float m_moveDistance = 6f;

    [Tooltip("문구가 최소한 이만큼은 떠 있는다 — 조건이 곧바로 충족돼도 읽을 시간을 준다(초)")]
    [Min(0f)]
    [SerializeField]
    private float m_minPromptSeconds = 3f;

    [Tooltip("시체 경고 토스트가 떠 있는 시간(초)")]
    [Min(0f)]
    [SerializeField]
    private float m_corpseToastSeconds = 6f;

    [Tooltip("마지막 문구를 읽을 시간 — 이 시간이 지나면 타이틀로 돌아간다(초)")]
    [Min(0f)]
    [SerializeField]
    private float m_finishDelaySeconds = 8f;

    // 진행 상태
    private int m_stepIndex = -1;
    private LocalizedString m_shownPrompt;
    private float m_movedDistance;
    private Vector3 m_lastPlayerPosition;
    private bool m_finished;

    // 지금 단계 문구를 띄운 시각 — 최소 표시 시간의 기준. 라운드 종료 freeze로 timeScale이
    // 건드려져도 흐르도록 실시간 기준을 쓴다(RoundManager의 시작 지연과 같은 방침).
    private float m_stepShownTime;

    // 이벤트로 세우는 깃발 — 폴링으로는 잡을 수 없는 "그 순간 한 번" 짜리들
    private bool m_slotChanged;
    private bool m_cctvSwitched;
    private bool m_scanned;
    private bool m_judged;
    private bool m_teamPanelSeen;

    // 시체 경고는 튜토리얼 통틀어 한 번뿐이다
    private bool m_corpseWarned;

    // 로컬 플레이어 부품 — 스폰이 씬 시작보다 늦어 매 프레임 다시 찾다가 잡히면 그때 건다
    private Transform m_playerTransform;
    private PlayerInteractor m_interactor;
    private PlayerEscorter m_escorter;
    private PlayerItemUser m_itemUser;
    private Scanner m_boundScanner;

    // 매니저 구독 — 라운드 준비가 끝나야 서는 것들이라 늦게 붙는다
    private ArrestJudge m_boundJudge;
    private bool m_cctvBound;

    // 스폰된 NPC 중 조건에 맞는 것이 하나라도 있는가. 조건마다 같은 순회를 복사하지 않으려고 나눠 뒀다.
    // 아래 두 곳이 넘기는 람다는 캡처가 없어 델리게이트가 한 번만 만들어진다 — 매 프레임 도는 자리라
    // 그게 중요하다(AnyNpcDead는 시체가 생길 때까지 계속 돈다).
    private static bool AnyNpc(Func<NpcController, bool> test)
    {
        NpcSpawner spawner = App.Game.NpcSpawner;
        if (spawner == null)
            return false;

        foreach (NpcController npc in spawner.SpawnedNpcs)
            if (npc != null && test(npc))
                return true;

        return false;
    }

    // NpcStun.IsStunned로 본다 — 무력화 경로가 둘이라 상태 비교로는 절반을 놓친다.
    // 진압봉·테이저는 FSM을 바꾸지 않는 오버레이 경로고(NPC는 Idle/Run인 채 기절한다),
    // NpcState.Stunned 전이는 넉백 착지 KO 전용이다. 그 둘을 함께 답하는 것이 IsStunned다.
    private bool AnyNpcStunned => AnyNpc(n => n.Stun != null && n.Stun.IsStunned);

    private bool AnyNpcDead => AnyNpc(n => n.Death != null && n.Death.IsDead);

    /// <summary>
    /// 지금이 튜토리얼인가 — 이 컴포넌트가 씬에 있다는 것이 곧 판별 기준이다(AppHelper.FromSceneName 주석).
    /// 튜토리얼 씬은 <see cref="EScene.Game"/>으로 분류되므로 App.CurrentScene으로는 가릴 수 없다.
    /// 읽는 곳: <see cref="PlayerHealth"/>(무적 바닥값).
    /// </summary>
    public static bool IsActive { get; private set; }

    private void Awake() => IsActive = true;

    private void Start()
    {
        TutorialFlow.MarkOffered(); // 직접 Play로 들어온 경우까지 포함해 여기서 한 번 기록한다
        HostAsync().Forget();
    }

    private void OnDestroy()
    {
        IsActive = false;
        HidePrompt();
        UnbindAll();
    }

    /// <summary>
    /// 로컬 호스트 + 라운드 준비. DevAutoHost와 같은 일을 하지만 <b>빌드에도 들어간다</b> —
    /// 그쪽은 <c>#if UNITY_EDITOR</c>라 빌드된 게임에서는 아예 컴파일되지 않는다.
    /// 그래서 튜토리얼 씬에는 DevAutoHost를 두지 않는다(둘 다 있으면 같은 프레임에 StartHost가 겹친다).
    ///
    /// Relay·Lobby를 타지 않는 순수 로컬 호스트다 — 세션 코드도, 로그인도 필요 없다.
    /// </summary>
    private async UniTaskVoid HostAsync()
    {
        NetworkManager net = NetworkManager.Singleton;
        if (net == null)
        {
            Debug.LogWarning("[튜토리얼] NetworkManager가 없어 시작할 수 없다", this);
            return;
        }

        if (net.IsListening)
            return; // 이미 세션 중이면 관여하지 않는다

        // 모든 Start()가 끝난 다음 프레임에 띄운다 — PlayerSpawnManager가 Start에서 접속 승인 콜백을
        // 거는데, 그 전에 StartHost하면 호스트 플레이어가 스폰 지점을 못 받고 원점에 생긴다 (#247).
        await UniTask.NextFrame(this.GetCancellationTokenOnDestroy());

        // 세션 SDK를 거치지 않으므로 연결 승인 게이트를 여기서 직접 건다 (#628 · #663).
        // 이걸 빼면 승인 콜백이 없어 NGO가 <b>플레이어를 원점에</b> 만든다 — PlayerSpawnManager의
        // 스폰 지점(본부)은 그 콜백을 타야 적용된다. SDK로 방을 만들 때는 SessionManager가 대신 걸어 준다.
        // DevAutoHost가 같은 이유로 같은 짝을 부른다.
        if (App.Net.Session != null)
        {
            ConnectionApprovalGate.StampLocalPayload(net);
            App.Net.Session.Approval.Install(net);
        }

        net.StartHost();

        await UniTask.NextFrame(this.GetCancellationTokenOnDestroy()); // NGO 서버 준비 보장

        // 씬 준비 게이트에 자기 보고 — 혼자라 기다릴 상대가 없지만, 보고가 없으면 타임아웃까지 대기한다 (#215)
        App.Game.ReadyGate?.ReportSelfReady();

        // 라운드 준비 진입점 — 여기서 NPC가 스폰되고 진범이 배정된다 (#403)
        App.Game.Round?.BeginRoundPreparation();

        Advance(); // 첫 안내를 띄운다
    }

    private void Update()
    {
        BindLate();
        TrackMovement();
        TrackTeamPanel();
        TrackCorpse();

        if (m_finished || m_stepIndex < 0 || m_stepIndex >= s_steps.Length)
            return;

        // 문구를 읽을 시간을 준다 — 조건이 이미 충족된 채로 단계에 들어서면 한 프레임 깜빡이고
        // 지나가 버린다. 이동(6m)처럼 원래 오래 걸리는 단계에는 아무 영향이 없다.
        if (Time.unscaledTime - m_stepShownTime < m_minPromptSeconds)
            return;

        if (s_steps[m_stepIndex].IsDone(this))
            Advance();
    }

    // 다음 단계로. 마지막을 넘어서면 잠시 뒤 타이틀로 돌아간다.
    private void Advance()
    {
        m_stepIndex++;
        m_stepShownTime = Time.unscaledTime;

        if (m_stepIndex >= s_steps.Length)
        {
            m_finished = true;
            ShowPrompt("Done");
            FinishAsync().Forget();
            return;
        }

        // 본부에서 배울 것(이동·슬롯·조준·상황판·CCTV)이 끝나면 현장으로 내보낸다.
        // 잠금은 라운드 시작 때 이미 풀렸으므로(DoubleDoor.HandleRoundStarted) 여는 것만 남는다.
        // 두 짝을 다 여는 이유는 어느 쪽으로 나갈지 모르기 때문이다 — 닫힌 쪽으로 간 사람이 갇힌다.
        if (s_steps[m_stepIndex].Key == k_fieldStartKey && m_hqGates != null)
            foreach (DoubleDoor gate in m_hqGates)
                if (gate != null)
                    gate.ServerSetOpen(true);

        ShowPrompt(s_steps[m_stepIndex].Key);
    }

    /// <summary>지금 떠 있는 단계의 키 — 아직 시작 전이거나 다 끝났으면 null.</summary>
    private string CurrentStepKey =>
        m_stepIndex >= 0 && m_stepIndex < s_steps.Length ? s_steps[m_stepIndex].Key : null;

    private async UniTaskVoid FinishAsync()
    {
        await UniTask.Delay(
            TimeSpan.FromSeconds(m_finishDelaySeconds),
            cancellationToken: this.GetCancellationTokenOnDestroy()
        );

        TutorialFlow.Exit();
    }

    private void ShowPrompt(string key)
    {
        HidePrompt();

        // 안내 HUD는 오너 스폰과 함께 생긴다(InteractionFeedback.EnsureHud). 그 스폰이 StartHost
        // 안에서 동기로 끝나고 첫 Advance는 그 다음 프레임이라 정상 흐름에서는 항상 서 있다 —
        // HUD가 없는 구성(데디케이티드 서버 등)에서 조용히 넘어가기 위한 가드다.
        if (App.UI.Prompt == null)
            return;

        m_shownPrompt = new LocalizedString(k_table, k_keyPrefix + key);
        App.UI.Prompt.Show(m_shownPrompt);
    }

    private void HidePrompt()
    {
        if (m_shownPrompt == null)
            return;

        App.UI.Prompt?.Hide(m_shownPrompt);
        m_shownPrompt = null;
    }

    // 늦게 서는 것들을 잡히는 대로 건다 — 플레이어는 스폰 뒤, 감옥·판정은 라운드 준비 뒤에 선다.
    private void BindLate()
    {
        if (m_playerTransform == null)
        {
            NetworkObject player = NetworkManager.Singleton?.LocalClient?.PlayerObject;
            if (player != null)
            {
                m_playerTransform = player.transform;
                m_lastPlayerPosition = m_playerTransform.position;
                m_interactor = player.GetComponent<PlayerInteractor>();
                m_escorter = player.GetComponent<PlayerEscorter>();

                m_itemUser = player.GetComponent<PlayerItemUser>();
                if (m_itemUser != null)
                    m_itemUser.OnEquippedItemChanged += HandleEquippedItemChanged;
            }
        }

        // CCTV는 씬 직렬화 참조라 Start에서도 잡히지만 <b>여기서 걸어야 한다</b> —
        // CCTVSwitcher.OnNetworkSpawn의 Apply()가 StartHost 도중 OnDisplayChanged를 한 번 쏘고,
        // 이 구독은 그 뒤(같은 프레임 Update)라 그것을 놓친다. 일찍 걸면 Cctv 단계가 저절로 통과한다.
        if (!m_cctvBound && m_cctv != null)
        {
            m_cctv.OnDisplayChanged += HandleCctvChanged;
            m_cctvBound = true;
        }

        // 판정은 산 사람과 시체가 서로 다른 이벤트로 갈린다 (#766) — 튜토리얼은 둘 다 통과로 친다.
        // 시체 인계도 정상 경로다(생사 불문 대상은 감액, 생포 필수 대상은 0원). 산 쪽만 들으면
        // 죽여서 끌고 온 플레이어가 아무 반응 없는 화면 앞에 갇힌다. "값이 다르다"는 토스트가 가르친다.
        if (m_boundJudge == null && App.Game.ArrestJudge != null)
        {
            m_boundJudge = App.Game.ArrestJudge;
            m_boundJudge.OnArrestJudged += HandleArrestJudged;
            m_boundJudge.OnCorpseJudged += HandleArrestJudged;
        }
    }

    private void UnbindAll()
    {
        if (m_itemUser != null)
            m_itemUser.OnEquippedItemChanged -= HandleEquippedItemChanged;
        if (m_boundScanner != null)
            m_boundScanner.OnScanCompleted -= HandleScanCompleted;
        if (m_cctvBound && m_cctv != null)
            m_cctv.OnDisplayChanged -= HandleCctvChanged;
        if (m_boundJudge != null)
        {
            m_boundJudge.OnArrestJudged -= HandleArrestJudged;
            m_boundJudge.OnCorpseJudged -= HandleArrestJudged;
        }
    }

    private void TrackMovement()
    {
        if (m_playerTransform == null)
            return;

        Vector3 now = m_playerTransform.position;
        m_movedDistance += Vector3.Distance(now, m_lastPlayerPosition);
        m_lastPlayerPosition = now;
    }

    // NPC가 죽으면 한 번 알려 준다 — 시체 인계도 통과 경로지만 값이 다르다는 것을 가르친다 (#766).
    // 단계 문구(PromptView)를 덮지 않게 토스트로 띄운다. "하나라도 죽었나"만 알면 되므로
    // NpcDeath.OnDied 구독 열 개 대신 폴링한다.
    private void TrackCorpse()
    {
        if (m_corpseWarned || !AnyNpcDead)
            return;

        m_corpseWarned = true;
        App.UI.Toast?.Show(new LocalizedString(k_table, k_keyPrefix + "Corpse"), m_corpseToastSeconds);
    }

    // Tab 상황판은 홀드로 잠깐 뜨고 만다 — 열린 순간을 놓치지 않게 매 프레임 본다.
    // 이 창이 이번 라운드 수배 몽타주를 함께 띄우므로(#720), 본부 수배 리스트 학습을 이것으로 갈음한다.
    private void TrackTeamPanel()
    {
        if (m_teamPanelSeen || App.UI.Game == null)
            return;

        if (App.UI.Game.TryGetPanel(out TeamStatusPanel panel) && panel.IsOpened)
            m_teamPanelSeen = true;
    }

    // 슬롯을 바꾼 것 자체가 한 단계이고, 스캐너를 들었다면 그때 스캔 완료도 함께 구독한다 —
    // 스캐너는 매니저가 아니라 손에 든 아이템이라 이 시점 말고는 잡을 자리가 없다.
    private void HandleEquippedItemChanged(ItemBase item)
    {
        // 부팅 중 기본 장비 지급(PlayerItemSupply)이 이 이벤트를 한 번 저절로 쏜다. 지급이 한 프레임
        // 미뤄져(#370) 이 구독보다 뒤에 오므로 구독 시점으로는 못 거른다 — 단계가 떠 있을 때만 센다.
        //
        // 이 가드는 여기에만 둔다. 나머지 깃발은 사람 입력이 있어야 서고, 인계(Jail)와 판정(Verdict)은
        // 유치장 버튼 한 번이 둘 다 쏘므로(JailIntake가 판정을 먼저 돌린다) 단계별로 끊으면
        // 판정 단계가 이미 끝난 판정을 영원히 기다린다.
        if (CurrentStepKey == k_slotsKey)
            m_slotChanged = true;

        if (m_boundScanner != null)
        {
            m_boundScanner.OnScanCompleted -= HandleScanCompleted;
            m_boundScanner = null;
        }

        m_boundScanner = item as Scanner;
        if (m_boundScanner != null)
            m_boundScanner.OnScanCompleted += HandleScanCompleted;
    }

    private void HandleScanCompleted(CitizenProfile profile, ulong npcId) => m_scanned = true;

    private void HandleCctvChanged() => m_cctvSwitched = true;

    private void HandleArrestJudged(ArrestResult result) => m_judged = true;
}
