using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.UI;

/// <summary>
/// 스캔 결과 프레젠터. (#39 → #233)
/// 스캐너를 든 채 NPC를 조준하면 그 NPC에 달린 월드공간 카드(ScanInfoView)를 켜서 정보를 표시한다:
///   - 이 플레이어가 이미 스캔한 NPC → 실제 프로필(이름/타입/세력).
///   - 아직 스캔하지 않은 NPC → 모든 필드를 ??로 마스킹.
/// 카드는 NPC의 자식이라 위치는 Transform이 알아서 따라간다 — 프레젠터는 조준이 바뀌는 순간에만
/// (PlayerInteractor.OnTargetChanged) 이전 카드를 끄고 새 카드를 켠다. 매 프레임 폴링 없음.
///
/// 스캔 완료 여부는 "이 플레이어" 기준으로만 관리한다(개인별, 팀 공유 아님). 스캔 결과 자체가
/// 오너 로컬 전용(GDD 5-4)이라 네트워크 동기화 없이 로컬 HashSet으로 충분하다 —
/// 스캐너의 OnScanCompleted(오너에게만 회신되는 ScanResultRpc에서 발행)를 받아 NPC id를 기록한다.
/// 카드 텍스트도 각 클라가 자기 로컬 NPC 인스턴스에만 찍으므로 동기화되지 않는다(개인별 표시 성립).
///
/// 오너 로컬 전용 — 남의 플레이어 HUD 캔버스가 내 화면에 겹치지 않게 오너가 아니면 통째로 끈다.
/// 스캐너는 PlayerLoadout이 런타임에 지급·장착하므로(#47) PlayerItemUser의 장착 변경을 따라가
/// 스캐너가 장착됐을 때만 그 인스턴스에 바인딩한다.
/// </summary>
public class ScanResultPresenter : NetworkBehaviour
{
    [Tooltip(
        "HUD 루트(캔버스). 남의 플레이어 것이 내 화면에 겹쳐 그려지지 않게 오너가 아니면 끈다"
    )]
    [SerializeField]
    private GameObject m_uiRoot;

    [Header("스캐너 피드백 (#309) — m_uiRoot 하위 오너 전용 UI")]
    [Tooltip("배터리 게이지 패널 루트(배경 포함). 스캐너를 들었을 때만 켜진다 — 표시/숨김 토글 대상")]
    [SerializeField]
    private GameObject m_batteryPanel;

    [Tooltip("배터리 라벨 (m_batteryPanel 하위). 칸이 최대치를 다 담으면 라벨만, 아니면 숫자까지 찍는다")]
    [SerializeField]
    private TextMeshProUGUI m_batteryText;

    [Tooltip("배터리 칸 — 왼쪽부터 잔량만큼 켜진다. 비워 두면 숫자 표기만 남는다 (#894)")]
    [SerializeField]
    private Image[] m_batteryCells = new Image[0];

    [SerializeField]
    private Color m_cellFilledTone = new Color(0.98f, 0.62f, 0.16f, 1f);

    [SerializeField]
    private Color m_cellEmptyTone = new Color(0.13f, 0.15f, 0.19f, 1f);

    [Tooltip("토스트 패널 루트(배경 포함). 실패 사유 표시 중에만 켜진다 — 표시/숨김 토글 대상")]
    [SerializeField]
    private GameObject m_toastPanel;

    [Tooltip("스캔 실패·취소 사유 토스트 텍스트 (m_toastPanel 하위)")]
    [SerializeField]
    private TextMeshProUGUI m_toastText;

    [Tooltip("토스트 표시 유지 시간(초)")]
    [SerializeField]
    private float m_toastSeconds = 2f;

    // 배터리·토스트 문구는 조회 시점이 코드 안이라 인스펙터에서 고를 것이 없다. (#497)
    private const string k_hudTable = "HudTable";
    private const string k_batteryKey = "Hud.Scan.Battery";
    private const string k_batteryLabelKey = "Hud.Scan.BatteryLabel";
    private const string k_chargedKey = "Hud.Scan.Charged";
    private const string k_lowBatteryKey = "Hud.Scan.LowBattery";

    // 아이템 토스트 사유는 아이템 쪽 테이블이 주인이다 — 규약 키 Item.Feedback.<enum 이름> (#525)
    private const string k_itemTable = "ItemTable";
    private const string k_feedbackPrefix = "Item.Feedback.";

    private PlayerInteractor m_interactor;
    private PlayerItemUser m_itemUser;
    private Scanner m_scanner; // 현재 장착된 스캐너 인스턴스에 바인딩. 스캐너 미장착이면 null.
    private IChargeable m_battery; // 그 스캐너의 배터리(ItemBattery) 잔량은 옆 컴포넌트가 들고 있다.

    // 이 플레이어가 스캔 완료한 NPC의 NetworkObjectId. 오너 로컬 전용(동기화 없음). (#233)
    private readonly HashSet<ulong> m_scannedNpcIds = new HashSet<ulong>();

    /// <summary>이 플레이어가 이미 스캔한 NPC인가 — 스캐너가 중복 사용을 막는 데 쓴다.
    /// 카드 표시와 같은 집합을 보므로 "실제값이 보이는데 또 스캔되는" 어긋남이 생기지 않는다.</summary>
    public bool HasScanned(ulong npcNetworkObjectId) => m_scannedNpcIds.Contains(npcNetworkObjectId);

    // 지금 조준해서 표시 중인 NPC의 카드/신원. 조준이 바뀌면 교체된다.
    private ScanInfoView m_currentView;
    private CitizenIdentity m_currentIdentity;
    private ulong m_currentNpcId;
    private bool m_currentHasId;

    // 생사 상태 아이콘용 — 생사는 조준 유지 중에도 바뀔 수 있어 폴링으로 따라간다 (아래 Update 참고).
    private NpcController m_currentController;
    private bool m_currentDeadState;

    // 토스트 자동 숨김 타이머 — 새 메시지가 오면 이전 타이머를 취소하고 이어받는다. (지속 토스트는 타이머 없음)
    private CancellationTokenSource m_toastCts;

    // 마지막으로 본 배터리 값 — 증가(충전) 감지용. 미장착이면 -1. (#309)
    private int m_lastBattery = -1;

    // 스캔 카드가 없다고 이미 알린 NPC — 같은 대상에 경고를 반복하지 않는다. (#505)
    private readonly HashSet<int> m_warnedMissingCard = new HashSet<int>();

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            if (m_uiRoot != null)
                m_uiRoot.SetActive(false);
            enabled = false;
            return;
        }

        if (m_uiRoot != null)
            m_uiRoot.SetActive(true);

        SetActive(m_batteryPanel, false); // 스캐너 장착 전엔 숨김
        SetActive(m_toastPanel, false);

        // 카드·배터리 문구가 테이블에서 오므로 언어가 바뀌면 다시 채운다 — 값은 그대로여도 표기가 바뀐다.
        // 문구마다 StringChanged를 거는 대신 로케일 변경 한 곳에 걸어 통째로 다시 그린다 (ShopStand와 같은 방식). (#497)
        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;

        // 인터랙터/아이템유저는 플레이어 루트에 있다 — 부모까지 탐색.
        m_interactor = GetComponentInParent<PlayerInteractor>();
        m_itemUser = GetComponentInParent<PlayerItemUser>();
        if (m_interactor == null || m_itemUser == null)
        {
            Debug.LogWarning(
                "ScanResultPresenter: PlayerInteractor/PlayerItemUser를 찾지 못함 — 스캔 표시 불가",
                this
            );
            return;
        }

        m_interactor.OnTargetChanged += HandleTargetChanged;
        m_itemUser.OnEquippedItemChanged += HandleEquippedItemChanged;
        HandleEquippedItemChanged(m_itemUser.EquippedItem);
    }

    public override void OnNetworkDespawn()
    {
        if (m_interactor != null)
            m_interactor.OnTargetChanged -= HandleTargetChanged;
        if (m_itemUser != null)
            m_itemUser.OnEquippedItemChanged -= HandleEquippedItemChanged;

        // 종료 중에는 설정 에셋을 되살리지 않는다 — HasSettings로 먼저 확인한다 (ShopStand 관례)
        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;

        BindScanner(null); // 게이지·토스트 정리 포함
        HideCurrent();
    }

    // 언어가 바뀌면 지금 떠 있는 카드와 배터리 표기를 다시 채운다.
    // 토스트는 다시 그리지 않는다 — 전부 몇 초짜리라 다음 발생분부터 새 언어로 뜬다.
    private void HandleLocaleChanged(Locale locale)
    {
        UpdateCurrentContent();

        if (m_battery != null)
            ApplyBatteryText(m_battery.CurrentBattery);
    }

    // 조준 대상이 바뀔 때만 호출된다 — 이전 카드를 끄고, 새 대상이 스캔 가능한 NPC면 그 카드를 켠다.
    private void HandleTargetChanged(GameObject target)
    {
        HideCurrent();

        // 스캐너 미장착이면 표시하지 않는다.
        if (m_scanner == null)
            return;

        // 콜라이더가 NPC 루트의 자식일 수 있어 부모까지 탐색한다.
        CitizenIdentity identity =
            target != null ? target.GetComponentInParent<CitizenIdentity>() : null;
        if (identity == null)
            return;

        // NPC 프리팹에 달린 카드(기본 비활성)를 찾는다.
        ScanInfoView view = identity.GetComponentInChildren<ScanInfoView>(true);
        if (view == null)
        {
            WarnMissingCardOnce(identity);
            return;
        }

        NetworkObject npcObject = identity.GetComponentInParent<NetworkObject>();

        m_currentIdentity = identity;
        m_currentView = view;
        m_currentHasId = npcObject != null;
        m_currentNpcId = npcObject != null ? npcObject.NetworkObjectId : 0;

        // 생사는 CitizenIdentity와 같은 오브젝트의 NpcController가 갖는다(#571) — 없으면(테스트 씬 등) 폴링을 건너뛴다.
        m_currentController = identity.GetComponent<NpcController>();
        m_currentDeadState = m_currentController != null && m_currentController.Death.IsDead;

        // 빌보드가 바라볼 카메라를 로컬 플레이어 카메라로 지정 (Camera.main 태그에 의존하지 않게)
        if (m_interactor.AimCamera != null)
            m_currentView.SetCamera(m_interactor.AimCamera.transform);

        UpdateCurrentContent();
    }

    // 장착 아이템이 스캐너면 바인딩, 아니면 해제하고 현재 조준 대상 기준으로 표시를 다시 평가한다.
    private void HandleEquippedItemChanged(ItemBase item)
    {
        BindScanner(item as Scanner);

        // 장착 변경은 조준 대상 변경 이벤트를 발생시키지 않으므로, 현재 조준 중인 대상으로 재평가한다.
        // (스캐너로 바꾸면서 이미 NPC를 보고 있던 경우 표시, 스캐너를 내려놓으면 숨김)
        HandleTargetChanged(m_interactor != null ? m_interactor.CurrentTarget : null);
    }

    private void UpdateCurrentContent()
    {
        if (m_currentView == null)
            return;

        // 이 플레이어가 스캔했고 프로필이 로컬에 동기화돼 있으면 실제값, 아니면 ?? 마스킹.
        bool scanned =
            m_currentHasId
            && m_scannedNpcIds.Contains(m_currentNpcId)
            && m_currentIdentity.Profile != null;

        if (scanned)
        {
            CitizenProfile profile = m_currentIdentity.Profile;
            // 스캔 표시는 정본이 아닌 표시 이름(m_nameView) — 위조범은 여기서 정본과 어긋난다 (#223)
            m_currentView.ShowReal(profile.m_nameView, profile.m_typeView, m_currentDeadState);
        }
        else
        {
            m_currentView.ShowMasked();
        }
    }

    // 생사 상태는 조준을 유지한 채로도 바뀔 수 있다(카드를 보는 동안 대상이 죽는 경우) — 조준이
    // 바뀔 때만 갱신하는 HandleTargetChanged로는 못 잡으므로, 지금 조준 중인 대상이 있을 때만
    // 가볍게 폴링한다. NpcDeath.OnDied는 서버(오프라인) 전용이라 클라 로컬인 이 클래스에서는
    // 못 쓴다 — IsDead(동기화 상태)를 직접 비교하는 이 방식만 전 클라에서 안전하다.
    private void Update()
    {
        if (m_currentController == null)
            return;

        bool dead = m_currentController.Death.IsDead;
        if (dead == m_currentDeadState)
            return;

        m_currentDeadState = dead;
        UpdateCurrentContent();
    }

    // 스캔 카드가 없는 NPC — 프로필이 배정돼 있어도 띄울 카드가 없어 아무 일도 일어나지 않는다.
    // 조용히 지나가면 증상이 "로그에는 스캔 결과가 찍히는데 화면에는 안 보인다"로만 나타나 원인을 찾기 어렵다
    // (#505에서 돌발 이벤트 난동꾼 프리팹 둘이 정확히 이 상태였다). 새 NPC 유형을 추가할 때 바로 드러나게 알린다.
    //
    // 조준이 바뀔 때마다 도는 경로라 대상당 한 번만 낸다 — 같은 NPC를 다시 보며 로그가 도배되지 않게.
    private void WarnMissingCardOnce(CitizenIdentity identity)
    {
        if (!m_warnedMissingCard.Add(identity.GetInstanceID()))
            return;

        Debug.LogWarning(
            $"ScanResultPresenter: {identity.name}에 스캔 카드(ScanInfoView)가 없어 스캔 정보를 표시할 수 없다 — "
                + "NPC 프리팹에 ScanInfoCard.prefab을 자식으로 넣을 것 (NPC_Citizen 참고)",
            identity
        );
    }

    private void HideCurrent()
    {
        if (m_currentView != null)
            m_currentView.Hide();

        m_currentView = null;
        m_currentIdentity = null;
        m_currentHasId = false;
        m_currentNpcId = 0;
        m_currentController = null;
    }

    // 구독 대상 스캐너를 교체한다 — 이전 스캐너는 구독 해제하고 새 스캐너(있으면)를 구독한다.
    private void BindScanner(Scanner scanner)
    {
        if (m_scanner == scanner)
            return;

        if (m_scanner != null)
        {
            m_scanner.OnScanCompleted -= HandleScanCompleted;
            if (m_battery != null)
                m_battery.OnCharged -= HandleBatteryChanged;
            m_scanner.OnScanFeedback -= HandleScanFeedback;
            m_scanner.OnDepletedUseAttempt -= HandleDepletedUseAttempt;
        }

        m_scanner = scanner;
        m_battery = scanner != null ? scanner.GetComponent<IChargeable>() : null;

        if (m_scanner != null)
        {
            m_scanner.OnScanCompleted += HandleScanCompleted;
            if (m_battery != null)
                m_battery.OnCharged += HandleBatteryChanged; // 스캔 소모·본부 충전 반영 (#309)
            m_scanner.OnScanFeedback += HandleScanFeedback;   // 범위 이탈 등 실패 토스트 (#309)
            m_scanner.OnDepletedUseAttempt += HandleDepletedUseAttempt; // 소진 상태 사용 시도 (#810)
            m_lastBattery = -1;                               // 장착 시점 값을 "충전"으로 오인하지 않게 리셋
            SetActive(m_batteryPanel, true);                  // 게이지 노출
            UpdateBattery(m_battery != null ? m_battery.CurrentBattery : 0); // 초기 잔량 표기
        }
        else
        {
            SetActive(m_batteryPanel, false);                 // 스캐너 내려놓으면 게이지 숨김
            HideToast();
            m_lastBattery = -1;
        }
    }

    // 스캔 성공 — 이 NPC를 "스캔 완료" 집합에 기록한다. 지금 그 NPC를 보고 있으면 즉시 실제값으로 교체.
    private void HandleScanCompleted(CitizenProfile profile, ulong npcNetworkObjectId)
    {
        m_scannedNpcIds.Add(npcNetworkObjectId);

        if (m_currentHasId && m_currentNpcId == npcNetworkObjectId)
            UpdateCurrentContent();
    }

    // ---- 스캐너 배터리 게이지 + 상태 토스트 (#309) ----
    // 배터리 값은 Scanner의 NetworkVariable이 이미 오너에 동기화되므로 여기선 표시만 한다(RPC 불필요).
    // 토스트는 전부 몇 초짜리다 — 잔량 자체는 게이지가 상시로 보여주므로 경고문을 띄워 둘 이유가 없다 (#810).
    // 소진 안내는 값 변화가 아니라 사용 시도(Scanner.OnDepletedUseAttempt)로 구동한다.

    private void HandleBatteryChanged(int current) => UpdateBattery(current);

    private void UpdateBattery(int current)
    {
        ApplyBatteryText(current);
        ApplyBatteryCells(current);

        bool charged = m_lastBattery >= 0 && current > m_lastBattery; // 잔량 증가 = 충전기 이용
        m_lastBattery = current;

        if (charged)
            ShowToast(LocalizedStrings.Get(k_hudTable, k_chargedKey), transient: true);
    }

    // 칸이 최대치를 다 담을 때는 라벨만 쓴다 — 숫자는 칸이 이미 말하고 있다. 칸이 모자라는
    // 구성(칸을 안 배선했거나 최대치가 칸 수보다 큰 스캐너)에서만 예전처럼 숫자를 찍는다.
    private void ApplyBatteryText(int current)
    {
        if (m_batteryText == null || m_battery == null)
            return;

        int max = m_battery.MaxBattery;
        bool cellsCover = max > 0 && m_batteryCells != null && m_batteryCells.Length >= max;

        m_batteryText.text = cellsCover
            ? LocalizedStrings.Get(k_hudTable, k_batteryLabelKey)
            : LocalizedStrings.Get(k_hudTable, k_batteryKey, current, max);
    }

    // 남는 칸은 꺼 둔다 — 최대치가 3인 스캐너에 빈 칸 둘이 붙어 있으면 잔량을 잘못 읽는다.
    private void ApplyBatteryCells(int current)
    {
        if (m_batteryCells == null || m_batteryCells.Length == 0)
            return;

        int used = m_battery != null ? Mathf.Min(m_battery.MaxBattery, m_batteryCells.Length) : 0;
        for (int i = 0; i < m_batteryCells.Length; i++)
        {
            Image cell = m_batteryCells[i];
            if (cell == null)
                continue;

            bool inUse = i < used;
            if (cell.gameObject.activeSelf != inUse)
                cell.gameObject.SetActive(inUse);

            if (inUse)
                cell.color = i < current ? m_cellFilledTone : m_cellEmptyTone;
        }
    }

    // 배터리 0인 채로 스캔을 시도했을 때만 부족 문구를 띄운다 — 들고 있는 동안이 아니다 (#810).
    private void HandleDepletedUseAttempt() =>
        ShowToast(LocalizedStrings.Get(k_hudTable, k_lowBatteryKey), transient: true);

    // 범위 이탈 등 스캔 실패 사유 — 잠깐 띄운다. 스캐너는 사유를 enum으로만 보내고
    // 문구는 여기서 자기 로케일로 조회한다 (#525) — 서버 언어가 새어 들어오지 않게.
    private void HandleScanFeedback(EItemFeedback feedback) =>
        ShowToast(LocalizedStrings.Get(k_itemTable, k_feedbackPrefix + feedback), transient: true);

    // transient=true면 m_toastSeconds 후 자동 숨김, false면 다음 토스트/숨김 전까지 지속.
    private void ShowToast(string message, bool transient)
    {
        if (m_toastPanel == null || string.IsNullOrEmpty(message))
            return;

        m_toastCts?.Cancel();
        m_toastCts?.Dispose();
        m_toastCts = null;

        if (m_toastText != null)
            m_toastText.text = message;
        SetActive(m_toastPanel, true);

        if (transient)
        {
            m_toastCts = new CancellationTokenSource();
            HideAfterAsync(m_toastCts.Token).Forget();
        }
    }

    private void HideToast()
    {
        m_toastCts?.Cancel();
        m_toastCts?.Dispose();
        m_toastCts = null;
        SetActive(m_toastPanel, false);
    }

    private async UniTaskVoid HideAfterAsync(CancellationToken token)
    {
        try
        {
            await UniTask.Delay(TimeSpan.FromSeconds(m_toastSeconds), cancellationToken: token);
        }
        catch (OperationCanceledException)
        {
            return; // 새 토스트가 이어받았거나 디스폰 — 패널은 그쪽이 관리
        }

        SetActive(m_toastPanel, false);
        m_toastCts?.Dispose(); // 정상 만료도 Cancel 경로와 동일하게 정리 (CTS 누적 방지)
        m_toastCts = null;
    }

    private static void SetActive(GameObject go, bool active)
    {
        if (go != null && go.activeSelf != active)
            go.SetActive(active);
    }
}
