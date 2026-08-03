using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 설치형 아이템의 공통 기반 (GDD 8-4, #108/#488) — 본부에 놓인 고정 설비.
/// 기본은 미설치, 상점에서 사면 배달(ShopDelivery)이 서버에서 <see cref="SetInstalled"/>를 호출해 켠다.
/// 미설치 상태에서는 모델도 콜라이더도 꺼져 있어 본부에 아예 없는 것처럼 보인다.
///
/// 서버 권위 — 설치 여부는 서버만 쓰고 모든 클라가 읽는다. 씬에 놓인 서버 소유 오브젝트라 어떤 플레이어도 오너가 아니므로,
/// 파생이 요청 RPC를 둘 때는 <c>InvokePermission = RpcInvokePermission.Everyone</c>을 명시할 것 (#55).
/// </summary>
public abstract class InstallableItem : NetworkBehaviour, IInteractable
{
    [Header("설치 (상점 #182 접합점)")]
    [Tooltip("상점 식별자 — ShopDelivery가 이 값으로 팀 구매 목록과 대조한다. None이면 사도 배달되지 않는다")]
    [SerializeField] private EInstallable m_id = EInstallable.None;

    [Tooltip("라운드 시작 시의 설치 상태. 정식 흐름에서는 꺼 둘 것 — 구매가 켜 준다")]
    [SerializeField] private bool m_installedOnStart;

    private readonly NetworkVariable<bool> m_installedSynced = new NetworkVariable<bool>();
    private bool m_installed; // 서버·오프라인의 진실값 (비네트워크 Play 테스트 폴백)

    // 미설치 설비를 숨기는 대상. GameObject 자체는 끄지 않는다.
    // 끄면 NetworkBehaviour가 멈춰 설치 동기화값을 못 받아 영영 안 켜진다.
    private Renderer[] m_renderers;
    private Collider[] m_colliders;

    // 마지막으로 파생에 알린 설치 상태 — NetworkVariable은 값을 쓴 피어에서도 OnValueChanged를 올리므로
    // 서버에서는 SetInstalled와 동기화 콜백으로 같은 적용이 두 번 들어온다.
    // 표시 갱신은 멱등해서 무해하지만 파생 훅(소리·연출)까지 두 번 불리면 안 되므로 여기서 거른다.
    private bool m_notifiedInstalled;

    /// <summary>상점 식별자 — 배달(ShopDelivery)이 팀 구매 목록과 대조할 때 쓴다.</summary>
    public EInstallable Id => m_id;

    /// <summary>설치(구매 완료) 여부. 서버·오프라인은 실참조, 원격 피어는 동기화값으로 판정.</summary>
    public bool IsInstalled => IsSpawned && !IsServer ? m_installedSynced.Value : m_installed;

    protected void Awake()
    {
        m_renderers = GetComponentsInChildren<Renderer>(true);
        m_colliders = GetComponentsInChildren<Collider>(true);

        ApplyVisibility(false); // 스폰 전 기본값은 미설치 — 씬 로드 직후 한 프레임 노출되는 것을 막는다

        OnInstallableAwake();
    }

    public sealed override void OnNetworkSpawn()
    {
        // 설치 상태 초기값은 서버가 채운다 — 쓰기 권한이 서버뿐이라 클라는 조작할 수 없다.
        if (IsServer)
        {
            SetInstalled(m_installedOnStart);

            if (m_id == EInstallable.None)
                Debug.LogWarning($"[설치형] {name}: 상점 식별자가 None — 구매해도 배달되지 않는다", this);
        }

        // 원격 피어는 스폰 페이로드로 도착한 값을, 이후 구매는 변경 콜백으로 반영한다.
        m_installedSynced.OnValueChanged += HandleInstalledChanged;
        ApplyInstalled();

        OnInstallableSpawn();
    }

    public sealed override void OnNetworkDespawn()
    {
        m_installedSynced.OnValueChanged -= HandleInstalledChanged;

        OnInstallableDespawn();
    }

    // ---- 파생 훅 ----

    /// <summary>Awake 시점 초기화 — 컴포넌트 캐시 등. 스폰 전에 불리므로 네트워크 상태를 읽지 말 것.</summary>
    protected virtual void OnInstallableAwake() { }

    /// <summary>스폰 시점 초기화 — 이벤트 구독 등. 설치 상태는 이미 확정돼 있다.</summary>
    protected virtual void OnInstallableSpawn() { }

    /// <summary>디스폰 정리 — <see cref="OnInstallableSpawn"/>에서 건 구독을 해제한다.</summary>
    protected virtual void OnInstallableDespawn() { }

    /// <summary>
    /// 설치 상태가 실제로 바뀐 순간 — 설치 연출을 붙일 곳. 표시 토글은 베이스가 이미 했다.
    /// <see cref="OnInstallableAwake"/> 이후에만 불리므로 여기서 쓰는 참조는 Awake 훅에서 캐시해 둘 것.
    ///
    /// <b>일회성 연출(구매 효과음 등)에는 쓰지 말 것</b> — 이미 설치된 설비에 늦게 접속한 클라는
    /// 스폰 시점에 false→true 전이를 한 번 받는다. 상태성 표시(발광·라벨)에만 쓴다.
    /// </summary>
    protected virtual void OnInstalledChanged(bool installed) { }

    /// <summary>E 상호작용 본체 — 설치된 상태에서만 불린다(미설치 게이트는 베이스가 걸었다).</summary>
    protected abstract void OnInteract(GameObject interactor);

    // ---- 설치 ----

    /// <summary>
    /// 설치 상태를 바꾼다 — <b>상점(#182)의 접합점</b>. 구매 처리에서 서버가 호출하면 설비가 켜진다.
    /// 서버(또는 오프라인)에서만 유효하다.
    /// </summary>
    public void SetInstalled(bool installed)
    {
        if (IsSpawned && !IsServer)
            return;

        m_installed = installed;
        if (IsSpawned && IsServer)
            m_installedSynced.Value = installed;

        // 서버·오프라인은 자기 화면도 여기서 맞춘다 — 오프라인(!IsSpawned)에는 변경 콜백이 아예 없다.
        ApplyInstalled();
    }

    private void HandleInstalledChanged(bool previous, bool current) => ApplyInstalled();

    private void ApplyInstalled()
    {
        bool installed = IsInstalled;

        ApplyVisibility(installed);

        if (m_notifiedInstalled == installed)
            return;

        m_notifiedInstalled = installed;
        OnInstalledChanged(installed);
    }

    // 미설치 설비는 보이지도, 부딪히지도 않는다 — 사지 않은 물건이 본부에 서 있으면 혼란스럽고,
    // 렌더러만 끄면 보이지 않는 벽이 남는다(콜라이더가 솔리드라 그대로 몸이 막힌다).
    private void ApplyVisibility(bool visible)
    {
        foreach (Renderer itemRenderer in m_renderers)
        {
            if (itemRenderer != null)
                itemRenderer.enabled = visible;
        }

        foreach (Collider itemCollider in m_colliders)
        {
            if (itemCollider != null)
                itemCollider.enabled = visible;
        }
    }

    // ---- 상호작용 (E) ----

    /// <summary>
    /// 미설치 설비에는 윤곽선이 뜨지 않는다 (#184). 파생은 조건을 <b>더 좁힐</b> 때만 재정의한다
    /// (예: 쿨다운 중 차단) — base 호출을 함께 쓸 것. 넓히면 미설치 게이트가 무너진다.
    /// </summary>
    public virtual bool CanInteract(GameObject interactor) => IsInstalled;

    /// <summary>
    /// E 상호작용 — 미설치 게이트를 거친 뒤 <see cref="OnInteract"/>로 넘긴다.
    /// PlayerInteractor는 오너 클라에서만 돌므로(오너 외 비활성) 이 호출도 상호작용한 본인의 클라이언트에서만 일어난다.
    /// </summary>
    public void Interact(GameObject interactor)
    {
        if (!IsInstalled)
        {
            Debug.Log($"[설치형] {name}: 아직 설치되지 않음 (상점에서 구매 필요)");
            return;
        }

        OnInteract(interactor);
    }
}
