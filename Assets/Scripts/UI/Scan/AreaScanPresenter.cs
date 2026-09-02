using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 구역 스캔 오너 토스트. (#490)
/// 문구는 <c>App.UI.Toast</c>(#493)를 그대로 쓴다 — <see cref="ArrestNoticeBroadcaster"/>의
/// <c>m_noticeMessage</c>와 같은 관례로, 인스펙터에 배선한 <see cref="LocalizedString"/>에
/// <c>Arguments</c>를 채워 넘긴다. 아이템(<see cref="AreaScanner"/>)은 이벤트만 발행하고 문구는
/// UI가 소유한다는 방향(#525, <see cref="Scanner.OnDepletedUseAttempt"/>)과 일치한다.
///
/// <b>오너 로컬 전용</b> — Player 프리팹에 붙어 전 클라이언트에 복제되므로 오너가 아니면 통째로
/// 끈다(<see cref="ScanResultPresenter"/>와 같은 방침). 실제로는 구독 대상 이벤트 자체가 로컬
/// C# 델리게이트라(RPC가 아니다) 오너가 입력을 넣은 클라에서만 발행되지만, 쓰지 않을 구독을
/// 남의 플레이어 인스턴스에 만들어 두지 않는 편이 명확하다.
/// </summary>
public class AreaScanPresenter : NetworkBehaviour
{
    [Tooltip("쿨다운 중 사용 시도 토스트 — {0}=남은 초(정수)")]
    [SerializeField]
    private LocalizedString m_cooldownMessage;

    [Tooltip("먹통 중 사용 시도 토스트")]
    [SerializeField]
    private LocalizedString m_blackoutMessage;

    [Tooltip("반경 안에 진범이 있을 때의 판독 결과 토스트 — {0}=반경(m)")]
    [SerializeField]
    private LocalizedString m_hitMessage;

    [Tooltip("반경 안에 진범이 없을 때의 판독 결과 토스트 — {0}=반경(m)")]
    [SerializeField]
    private LocalizedString m_missMessage;

    [Tooltip("토스트가 화면에 머무는 시간(초)")]
    [Min(0f)]
    [SerializeField]
    private float m_toastSeconds = 2f;

    private PlayerItemUser m_itemUser;
    private AreaScanner m_scanner; // 현재 장착된 인스턴스에 바인딩. 미장착이면 null.

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            enabled = false;
            return;
        }

        // 아이템유저는 플레이어 루트에 있다 — 부모까지 탐색 (ScanResultPresenter와 동일 관례).
        m_itemUser = GetComponentInParent<PlayerItemUser>();
        if (m_itemUser == null)
        {
            Debug.LogWarning("AreaScanPresenter: PlayerItemUser를 찾지 못함 — 구역 스캔 토스트 표시 불가", this);
            return;
        }

        m_itemUser.OnEquippedItemChanged += HandleEquippedItemChanged;
        HandleEquippedItemChanged(m_itemUser.EquippedItem);
    }

    public override void OnNetworkDespawn()
    {
        if (m_itemUser != null)
            m_itemUser.OnEquippedItemChanged -= HandleEquippedItemChanged;

        BindScanner(null);
    }

    // 장착 아이템이 구역 스캐너면 바인딩, 아니면 해제한다.
    private void HandleEquippedItemChanged(ItemBase item) => BindScanner(item as AreaScanner);

    // 구독 대상 인스턴스를 교체한다 — 이전 것은 구독 해제하고 새 것(있으면)을 구독한다.
    private void BindScanner(AreaScanner scanner)
    {
        if (m_scanner == scanner)
            return;

        if (m_scanner != null)
        {
            m_scanner.OnCooldownUseAttempt -= HandleCooldownUseAttempt;
            m_scanner.OnBlackoutUseAttempt -= HandleBlackoutUseAttempt;
            m_scanner.OnScanResult -= HandleScanResult;
        }

        m_scanner = scanner;

        if (m_scanner != null)
        {
            m_scanner.OnCooldownUseAttempt += HandleCooldownUseAttempt;
            m_scanner.OnBlackoutUseAttempt += HandleBlackoutUseAttempt;
            m_scanner.OnScanResult += HandleScanResult;
        }
    }

    private void HandleCooldownUseAttempt(float remaining)
    {
        if (m_cooldownMessage == null || m_cooldownMessage.IsEmpty)
            return;

        m_cooldownMessage.Arguments = new object[] { Mathf.CeilToInt(remaining) }; // Show보다 먼저
        App.UI.Toast?.Show(m_cooldownMessage, m_toastSeconds); // HUD 없으면 무동작
    }

    // 링이 발밑에서 퍼지고 색으로만 갈려 안 읽혔다 — 같은 결과를 글자로 한 번 더 말한다 (#915)
    private void HandleScanResult(bool found, float radius)
    {
        LocalizedString message = found ? m_hitMessage : m_missMessage;
        if (message == null || message.IsEmpty)
            return;

        message.Arguments = new object[] { Mathf.RoundToInt(radius) }; // Show보다 먼저
        App.UI.Toast?.Show(message, m_toastSeconds);
    }

    private void HandleBlackoutUseAttempt()
    {
        if (m_blackoutMessage == null || m_blackoutMessage.IsEmpty)
            return;

        App.UI.Toast?.Show(m_blackoutMessage, m_toastSeconds);
    }
}
