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
/// 이미 있는 이벤트 넷만 구독해 깃발을 세우고 나머지는 매 프레임 상태를 읽는다. 도는 것은
/// 한 번에 조건 하나뿐이라 비용이 사실상 없다.
/// </summary>
public class TutorialDirector : MonoBehaviour
{
    private const string k_table = "HudTable";
    private const string k_keyPrefix = "Hud.Tutorial.";

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
        new Step("Slots", d => d.m_slotChanged),
        new Step("Aim", d => d.m_interactor != null && d.m_interactor.CurrentInteractable != null),
        new Step("TeamTab", d => d.m_teamPanelSeen),
        new Step("Cctv", d => d.m_cctvSwitched),
        new Step("Scan", d => d.m_scanned),
        new Step("Subdue", d => d.AnyNpcStunned),
        new Step("Rope", d => d.m_escorter != null && d.m_escorter.IsDraggingAny),
        new Step("Jail", d => d.m_admitted),
        // 오검거는 체험시키지 않는다 — 지금 페널티가 꺼져 있어(#612) 가르칠 "대가"가 정산 코믹 스탯뿐이다.
        // 대신 판정을 한 번 받아 보게 하고, 진범이 아니면 돈이 들어오지 않는다는 것만 문구로 알린다.
        new Step("Verdict", d => d.m_judged),
    };

    [Header("배선")]
    [Tooltip("본부 CCTV 콘솔의 모니터 — 채널을 한 번 돌렸는지 본다. HQ/Interior/CCTVConsole/CCTVMonitor")]
    [SerializeField]
    private CCTVSwitcher m_cctv;

    [Header("단계 기준값")]
    [Tooltip("이동 단계를 통과시킬 누적 이동 거리(m)")]
    [Min(0f)]
    [SerializeField]
    private float m_moveDistance = 6f;

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

    // 이벤트로 세우는 깃발 — 폴링으로는 잡을 수 없는 "그 순간 한 번" 짜리들
    private bool m_slotChanged;
    private bool m_cctvSwitched;
    private bool m_scanned;
    private bool m_admitted;
    private bool m_judged;
    private bool m_teamPanelSeen;

    // 로컬 플레이어 부품 — 스폰이 씬 시작보다 늦어 매 프레임 다시 찾다가 잡히면 그때 건다
    private Transform m_playerTransform;
    private PlayerInteractor m_interactor;
    private PlayerEscorter m_escorter;
    private PlayerItemUser m_itemUser;
    private Scanner m_boundScanner;

    // 매니저 구독 — 라운드 준비가 끝나야 서는 것들이라 늦게 붙는다
    private JailZone m_boundJail;
    private ArrestJudge m_boundJudge;
    private bool m_cctvBound;

    private bool AnyNpcStunned
    {
        get
        {
            NpcSpawner spawner = App.Game.NpcSpawner;
            if (spawner == null)
                return false;

            foreach (NpcController npc in spawner.SpawnedNpcs)
                if (npc != null && npc.CurrentState == NpcState.Stunned)
                    return true;

            return false;
        }
    }

    private void Start()
    {
        TutorialFlow.MarkOffered(); // 직접 Play로 들어온 경우까지 포함해 여기서 한 번 기록한다
        HostAsync().Forget();
    }

    private void OnDestroy()
    {
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
        App.Net.Session?.Approval.Install(net);

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

        if (m_finished || m_stepIndex < 0 || m_stepIndex >= s_steps.Length)
            return;

        if (s_steps[m_stepIndex].IsDone(this))
            Advance();
    }

    // 다음 단계로. 마지막을 넘어서면 잠시 뒤 타이틀로 돌아간다.
    private void Advance()
    {
        m_stepIndex++;

        if (m_stepIndex >= s_steps.Length)
        {
            m_finished = true;
            ShowPrompt("Done");
            FinishAsync().Forget();
            return;
        }

        ShowPrompt(s_steps[m_stepIndex].Key);
    }

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

        // 안내 HUD는 오너 스폰과 함께 생기므로 아직 없을 수 있다 — 그때는 다음 단계에서 다시 뜬다
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

        if (!m_cctvBound && m_cctv != null)
        {
            m_cctv.OnDisplayChanged += HandleCctvChanged;
            m_cctvBound = true;
        }

        if (m_boundJail == null && App.Game.Jail != null)
        {
            m_boundJail = App.Game.Jail;
            m_boundJail.OnInmateAdmitted += HandleInmateAdmitted;
        }

        if (m_boundJudge == null && App.Game.ArrestJudge != null)
        {
            m_boundJudge = App.Game.ArrestJudge;
            m_boundJudge.OnArrestJudged += HandleArrestJudged;
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
        if (m_boundJail != null)
            m_boundJail.OnInmateAdmitted -= HandleInmateAdmitted;
        if (m_boundJudge != null)
            m_boundJudge.OnArrestJudged -= HandleArrestJudged;
    }

    private void TrackMovement()
    {
        if (m_playerTransform == null)
            return;

        Vector3 now = m_playerTransform.position;
        m_movedDistance += Vector3.Distance(now, m_lastPlayerPosition);
        m_lastPlayerPosition = now;
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

    private void HandleInmateAdmitted(NpcController npc) => m_admitted = true;

    private void HandleArrestJudged(ArrestResult result) => m_judged = true;
}
