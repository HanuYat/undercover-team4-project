using UnityEngine;

// using Unity.Netcode; // TODO: 네트워크 테스트 시 주석 해제

/// <summary>
/// 모든 아이템의 공통 기반 클래스.
/// 이름·아이콘·설명 등 공통 데이터와 사용 진입점(Use)을 정의한다.
/// 스캐너·수갑 등 하위 아이템은 이 클래스를 상속해 Use()를 구현한다.
/// </summary>
// TODO: 네트워크 테스트 시 NetworkBehaviour로 승격 검토 (아이템 액션을 서버 권위로 동기화)
public abstract class ItemBase : MonoBehaviour
{
    [Header("아이템 정보")]
    [SerializeField]
    private string m_itemName;

    [SerializeField]
    private Sprite m_itemIcon;

    [SerializeField]
    [TextArea]
    private string m_itemDescription;

    [Header("1인칭 표시")]
    [Tooltip("장착 시 1인칭 손에 표시할 모델 프리팹. 비우면 손만 표시된다 (#45)")]
    [SerializeField]
    private GameObject m_heldModelPrefab;

    /// <summary>인벤토리·UI에 표시되는 아이템 이름.</summary>
    public string ItemName => m_itemName;

    /// <summary>인벤토리·UI에 표시되는 아이템 아이콘.</summary>
    public Sprite ItemIcon => m_itemIcon;

    /// <summary>인벤토리·UI에 표시되는 아이템 설명.</summary>
    public string ItemDescription => m_itemDescription;

    /// <summary>장착 시 1인칭 손에 들리는 모델 프리팹. 없으면 null — PlayerHandView가 표시를 생략한다. (#45)</summary>
    public GameObject HeldModelPrefab => m_heldModelPrefab;

    /// <summary>
    /// 현재 아이템을 사용할 수 있는지 여부.
    /// 기본값은 true이며, 하위 클래스가 사용 조건을 재정의한다.
    /// (예: 스캐너는 배터리 잔량이 있을 때만 true)
    /// 호출부는 Use() 전에 이 값을 확인한다.
    /// </summary>
    public virtual bool CanUse() => true;

    /// <summary>
    /// 아이템 사용 진입점. 하위 클래스가 구체 동작을 구현한다.
    /// (예: 스캐너 3초 채널링 후 스캔 정보 로그)
    /// </summary>
    // TODO: 네트워크 테스트 시 서버 권위로 실행되게 (오너 입력 → ServerRpc 요청 → 서버가 실제 효과 실행/검증 후 동기화)
    public abstract void Use();
}
