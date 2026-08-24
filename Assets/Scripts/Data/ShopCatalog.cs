using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 상점 판매 후보 명부 (#814). ShopLineup이 매 라운드 이 중 일부를 뽑아 진열대에 배정한다.
/// 인덱스가 네트워크 계약이다 — ShopStand.m_entryIndex가 이 인덱스를 그대로 싣는다.
/// 세이브에는 남지 않으므로(진열은 라운드마다 다시 뽑힘) 빌드 간 순서 제약은 없다.
/// </summary>
[CreateAssetMenu(fileName = "ShopCatalog", menuName = "Scriptable Objects/Shop Catalog")]
public class ShopCatalog : ScriptableObject
{
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

        [Tooltip("고정 등장 그룹(소모형). ShopLineup의 소모형 칸이 이 표시가 붙은 항목에서만 뽑힌다.")]
        [SerializeField]
        private bool m_staple;

        [Tooltip("진열대에 올릴 모델. 비우면 소지형은 ItemBase.HeldModelPrefab로 대신한다. (설치형은 필수)")]
        [SerializeField]
        private GameObject m_displayModel;

        [SerializeField]
        private Vector3 m_displayScale = Vector3.one;

        [SerializeField]
        private Vector3 m_displayEuler;

        public ItemBase ItemPrefab => m_itemPrefab;
        public EInstallable Installable => m_installable;
        public int InstallablePrice => m_installablePrice;
        public bool IsStaple => m_staple;
        public Vector3 DisplayScale => m_displayScale;
        public Vector3 DisplayEuler => m_displayEuler;

        public bool IsInstallable => m_installable != EInstallable.None;

        /// <summary>판매가. 소지형은 프리팹의 ItemBase.ShopPrice, 설치형은 이 항목의 값.</summary>
        public int Price => IsInstallable ? m_installablePrice : (m_itemPrefab != null ? m_itemPrefab.ShopPrice : 0);

        /// <summary>진열 모델. 지정값이 없으면 소지형은 손 모델로 대신한다.</summary>
        public GameObject DisplayModel =>
            m_displayModel != null ? m_displayModel
            : !IsInstallable && m_itemPrefab != null ? m_itemPrefab.HeldModelPrefab
            : null;

        /// <summary>추첨 후보로 쓸 수 있는가 — 소지형은 프리팹 배선, 설치형은 가격 배선을 본다.</summary>
        public bool IsValid => IsInstallable ? m_installablePrice > 0 : m_itemPrefab != null;
    }

    [Tooltip("인덱스가 네트워크 계약 — ShopStand가 그대로 복제한다")]
    [SerializeField]
    private Entry[] m_entries;

    [Tooltip("소모형(Staple) 항목이 채우는 칸 수")]
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
}
