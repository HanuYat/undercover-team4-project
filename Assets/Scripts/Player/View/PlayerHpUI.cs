using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 로컬 플레이어의 HP를 좌하단 기름통 게이지로 표시한다. (#691, 이전에는 텍스트 한 줄이었다)
/// <see cref="PlayerReviveHud"/>의 관례를 따라 오너 전용으로 동작한다.
///
/// <b>여기는 판단, 그림은 <see cref="HpOilGaugeView"/>.</b> <see cref="PlayerHitView"/>가
/// <see cref="DamageVignetteUI"/>를 모는 것과 같은 분리다 — "내가 오너인가"·"지금 맞았는가"는
/// 플레이어 오브젝트의 사정이고, 뷰는 비율과 피해량만 받아 그린다.
///
/// 오프라인(비네트워크) Play 테스트에서도 동작한다 — 구독을 Awake에서 걸고, 스폰 전에는
/// <see cref="IsLocalOwner"/>가 자기를 오너로 취급한다 (PlayerHitView와 같은 방침).
/// </summary>
[RequireComponent(typeof(PlayerHealth))]
public class PlayerHpUI : NetworkBehaviour
{
    [Header("UI 컴포넌트 연결")]
    [Tooltip("좌하단 HpPanel에 붙은 기름통 게이지 뷰를 드래그하여 연결하세요.")]
    [SerializeField]
    private HpOilGaugeView m_gauge;

    private PlayerHealth m_health;

    // 스폰 전(오프라인)에는 IsOwner가 늘 false다 — 그때는 자기 화면이 곧 내 화면이므로 오너로 본다.
    // (PlayerHitView.IsLocalOwner와 같은 형태)
    private bool IsLocalOwner => !IsSpawned || IsOwner;

    private void Awake()
    {
        m_health = GetComponent<PlayerHealth>();

        // OnNetworkSpawn이 아니라 Awake에서 건다 — 스폰하지 않는 오프라인 테스트에서도 이벤트를 받아야 한다.
        m_health.OnDamaged += HandleDamaged;
    }

    public override void OnDestroy()
    {
        if (m_health != null)
            m_health.OnDamaged -= HandleDamaged;

        base.OnDestroy();
    }

    public override void OnNetworkSpawn()
    {
        // 남의 플레이어 게이지가 내 화면에 그려지지 않게 오너의 것만 남긴다.
        // 예전에는 컴포넌트를 끄기만 했는데, 그러면 남의 프리팹에 딸려 온 통이 화면에 그대로 남는다.
        // 통을 끄고 컴포넌트도 함께 끈다 — InventoryBarView가 "PlayerHpUI 관례"로 인용하는 처리이고,
        // 정작 이쪽이 지키지 않고 있었다. 꺼 두면 비오너에서 폴링(Update)도 돌지 않는다.
        if (IsOwner)
            return;

        if (m_gauge != null)
            m_gauge.gameObject.SetActive(false);

        enabled = false;
    }

    public override void OnNetworkDespawn()
    {
        // 씬 전환·리스폰으로 뷰가 사라질 때 연출이 눌어붙지 않게 (PlayerHitView.OnNetworkDespawn과 같은 이유)
        if (IsLocalOwner)
            m_gauge?.ClearAll();

        base.OnNetworkDespawn();
    }

    /// <remarks>
    /// <b>무력화 가드를 두지 않는다.</b> <see cref="PlayerHitView.HandleDamaged"/>는 다운 중 피격
    /// 연출을 걸러내지만(쓰러진 상태의 표현과 화면에서 싸운다), 누유는 반대로 <b>기능 정지시키는
    /// 그 일격이 가장 크게 터져야</b> 통이 터진 그림이 완성된다. HP 0 이후의 추가 피해는
    /// 어차피 깎인 양이 0이라 여기까지 오지 않고(<see cref="PlayerHealth.TakeDamage"/>),
    /// 빈 통은 <see cref="HpOilGaugeView"/>가 알아서 방울을 멈춘다.
    /// </remarks>
    private void HandleDamaged(DamageHit hit)
    {
        if (!IsLocalOwner)
            return;

        m_gauge?.PlayLeakBurst(hit.Amount);
    }

    private void Update()
    {
        if (!IsLocalOwner || m_gauge == null || m_health == null)
            return;

        // 체력은 이미 동기화 값이라 이벤트에 실을 이유가 없어 폴링한다 — PlayerHitView가 저체력
        // 글리치를 폴링으로 모는 것과 같은 방침.
        m_gauge.SetHealth(m_health.CurrentHp, m_health.MaxHp);
    }
}
