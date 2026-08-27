using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 상점 판매 후보 명부 (#814). ShopLineup이 매 라운드 이 중 일부를 뽑아 주문창 칸에 배정한다.
/// 인덱스가 네트워크 계약이다 — ShopLineup의 칸이 이 인덱스를 그대로 싣는다.
/// 세이브에는 남지 않으므로(진열은 라운드마다 다시 뽑힘) 빌드 간 순서 제약은 없다.
/// </summary>
[CreateAssetMenu(fileName = "ShopCatalog", menuName = "Scriptable Objects/Shop Catalog")]
public class ShopCatalog : ScriptableObject
{
    private const string k_itemTable = "ItemTable";
    private const string k_nameKeyPrefix = "Item.Name.";

    [Serializable]
    public class Entry
    {
        [Tooltip("소지형 판매 품목. 가격·이름·설명은 여기서 읽는다. (비우면 설치형)")]
        [SerializeField]
        private ItemBase m_itemPrefab;

        [Tooltip("설치형 판매 품목. (None이면 소지형)")]
        [SerializeField]
        private EInstallable m_installable = EInstallable.None;

        [Tooltip("설치형 아이템의 가격. (소지형은 ItemBase.ShopPrice 사용)")]
        [Min(0)]
        [SerializeField]
        private int m_installablePrice;

        [Tooltip("고정 등장 그룹. ShopLineup의 고정 칸이 이 표시가 붙은 항목에서만 뽑힌다.")]
        [SerializeField]
        private bool m_staple;

        // 진열 고정 그룹(m_staple)과 갈라 둔다 — 홈런 진압봉처럼 고정 등장이면서 소모품이 아닌 항목이
        // 생겼다. 이 표시는 "쓰면 없어지는가"만 뜻한다. (#840)
        [Tooltip("소모품(쓰면 없어진다). 본부 재고 게시판이 이 표시가 붙은 항목의 남은 개수를 센다.")]
        [SerializeField]
        private bool m_consumable;

        [Tooltip("설치형 아이콘을 구울 때 쓸 모델 (ItemIconBaker). 소지형은 ItemBase.HeldModelPrefab로 대신한다")]
        [SerializeField]
        private GameObject m_displayModel;

        [Tooltip("아이콘을 구울 때 모델에 씌울 배율 — 눌러 쓰는 설치형은 이 비례가 곧 실물이다")]
        [SerializeField]
        private Vector3 m_displayScale = Vector3.one;

        [Tooltip("주문창 목록에 쓸 아이콘. 비우면 소지형은 ItemBase.ItemIcon으로 대신한다. (설치형은 필수)")]
        [SerializeField]
        private Sprite m_displayIcon;

        public ItemBase ItemPrefab => m_itemPrefab;
        public EInstallable Installable => m_installable;
        public int InstallablePrice => m_installablePrice;
        public bool IsStaple => m_staple;
        public bool IsConsumable => m_consumable;
        public Vector3 DisplayScale => m_displayScale;

        public bool IsInstallable => m_installable != EInstallable.None;

        /// <summary>판매가. 소지형은 프리팹의 ItemBase.ShopPrice, 설치형은 이 항목의 값.</summary>
        public int Price => IsInstallable ? m_installablePrice : (m_itemPrefab != null ? m_itemPrefab.ShopPrice : 0);

        /// <summary>주문창 아이콘 (#843). 지정값이 없으면 소지형은 아이템 아이콘으로 대신한다.</summary>
        public Sprite Icon =>
            m_displayIcon != null ? m_displayIcon
            : !IsInstallable && m_itemPrefab != null ? m_itemPrefab.ItemIcon
            : null;

        /// <summary>
        /// 지금 언어로 읽은 표시 이름 (#840) — 설치형은 규약 키, 소지형은 아이템의 LocalizedString.
        /// 배선이 없으면 빈 문자열이다(폴백 문구는 보는 쪽이 정한다). 언어 변경 갱신은 호출부가
        /// 통째로 다시 그리는 것으로 처리한다 (#497).
        /// </summary>
        public string DisplayName =>
            IsInstallable ? LocalizedStrings.Get(k_itemTable, k_nameKeyPrefix + m_installable)
            : m_itemPrefab != null && !m_itemPrefab.ItemName.IsEmpty
                ? m_itemPrefab.ItemName.GetLocalizedString()
            : string.Empty;

        /// <summary>아이콘을 구울 때 쓰는 모델 (#814). 지정값이 없으면 소지형은 손 모델로 대신한다.</summary>
        public GameObject DisplayModel =>
            m_displayModel != null ? m_displayModel
            : !IsInstallable && m_itemPrefab != null ? m_itemPrefab.HeldModelPrefab
            : null;

        /// <summary>추첨 후보로 쓸 수 있는가 — 소지형은 프리팹 배선, 설치형은 가격 배선을 본다.</summary>
        public bool IsValid => IsInstallable ? m_installablePrice > 0 : m_itemPrefab != null;
    }

    [Tooltip("인덱스가 네트워크 계약 — ShopLineup의 칸이 그대로 복제한다")]
    [SerializeField]
    private Entry[] m_entries;

    [Tooltip("고정 등장(Staple) 항목이 채우는 칸 수")]
    [Min(0)]
    [SerializeField]
    private int m_stapleSlots = 5;

    [Tooltip("그 외 항목에서 랜덤으로 채우는 칸 수")]
    [Min(0)]
    [SerializeField]
    private int m_randomSlots = 2;

    public IReadOnlyList<Entry> Entries => m_entries;
    public int Count => m_entries?.Length ?? 0;
    public int StapleSlots => m_stapleSlots;
    public int RandomSlots => m_randomSlots;

    public bool IsValidIndex(int index) => index >= 0 && index < Count && m_entries[index] != null;

    /// <summary>인덱스로 항목을 얻는다 — 범위 밖이면 null.</summary>
    public Entry Get(int index) => IsValidIndex(index) ? m_entries[index] : null;

    /// <summary>이 소지형 프리팹이 몇 번 항목인가 — 없으면 -1. 구매 집계(#840)의 역인덱스다.</summary>
    public int IndexOf(ItemBase itemPrefab)
    {
        if (itemPrefab == null || m_entries == null)
            return -1;

        for (int i = 0; i < m_entries.Length; i++)
        {
            Entry entry = m_entries[i];
            if (entry != null && !entry.IsInstallable && entry.ItemPrefab == itemPrefab)
                return i;
        }

        return -1;
    }

    /// <summary>이 설치형이 몇 번 항목인가 — 없으면 -1. (#840)</summary>
    public int IndexOf(EInstallable installable)
    {
        if (installable == EInstallable.None || m_entries == null)
            return -1;

        for (int i = 0; i < m_entries.Length; i++)
        {
            Entry entry = m_entries[i];
            if (entry != null && entry.Installable == installable)
                return i;
        }

        return -1;
    }
}
