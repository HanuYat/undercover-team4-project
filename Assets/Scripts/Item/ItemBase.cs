using UnityEngine;

/// <summary>
/// 모든 아이템의 공통 기반 클래스.
/// 이름·아이콘·설명 등 공통 데이터와 사용 진입점(Use)을 정의한다.
/// 스캐너·수갑 등 하위 아이템은 이 클래스를 상속해 Use()를 구현한다.
/// </summary>
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

    /// <summary>인벤토리·UI에 표시되는 아이템 이름.</summary>
    public string ItemName => m_itemName;

    /// <summary>인벤토리·UI에 표시되는 아이템 아이콘.</summary>
    public Sprite ItemIcon => m_itemIcon;

    /// <summary>인벤토리·UI에 표시되는 아이템 설명.</summary>
    public string ItemDescription => m_itemDescription;

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
    public abstract void Use();
}
