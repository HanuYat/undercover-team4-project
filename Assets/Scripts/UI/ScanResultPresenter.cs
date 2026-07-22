using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

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

    private PlayerInteractor m_interactor;
    private PlayerItemUser m_itemUser;
    private Scanner m_scanner; // 현재 장착된 스캐너 인스턴스에 바인딩. 스캐너 미장착이면 null.

    // 이 플레이어가 스캔 완료한 NPC의 NetworkObjectId. 오너 로컬 전용(동기화 없음). (#233)
    private readonly HashSet<ulong> m_scannedNpcIds = new HashSet<ulong>();

    // 지금 조준해서 표시 중인 NPC의 카드/신원. 조준이 바뀌면 교체된다.
    private ScanInfoView m_currentView;
    private CitizenIdentity m_currentIdentity;
    private ulong m_currentNpcId;
    private bool m_currentHasId;

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

        BindScanner(null);
        HideCurrent();
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
            return;

        NetworkObject npcObject = identity.GetComponentInParent<NetworkObject>();

        m_currentIdentity = identity;
        m_currentView = view;
        m_currentHasId = npcObject != null;
        m_currentNpcId = npcObject != null ? npcObject.NetworkObjectId : 0;

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
            m_currentView.ShowReal(profile.CitizenName, profile.m_typeView, profile.m_factionView);
        }
        else
        {
            m_currentView.ShowMasked();
        }
    }

    private void HideCurrent()
    {
        if (m_currentView != null)
            m_currentView.Hide();

        m_currentView = null;
        m_currentIdentity = null;
        m_currentHasId = false;
        m_currentNpcId = 0;
    }

    // 구독 대상 스캐너를 교체한다 — 이전 스캐너는 구독 해제하고 새 스캐너(있으면)를 구독한다.
    private void BindScanner(Scanner scanner)
    {
        if (m_scanner == scanner)
            return;

        if (m_scanner != null)
            m_scanner.OnScanCompleted -= HandleScanCompleted;

        m_scanner = scanner;

        if (m_scanner != null)
            m_scanner.OnScanCompleted += HandleScanCompleted;
    }

    // 스캔 성공 — 이 NPC를 "스캔 완료" 집합에 기록한다. 지금 그 NPC를 보고 있으면 즉시 실제값으로 교체.
    private void HandleScanCompleted(CitizenProfile profile, ulong npcNetworkObjectId)
    {
        m_scannedNpcIds.Add(npcNetworkObjectId);

        if (m_currentHasId && m_currentNpcId == npcNetworkObjectId)
            UpdateCurrentContent();
    }
}
