using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 한 슬롯의 치장을 고르는 칸들 (#818) — <see cref="PlayerColorPickerView"/>와 같은 구조다.
/// 고르면 <see cref="GameSettings"/>에 쓰고, 남에게 나르는 일은 명부와 <c>PlayerAccessories</c>가 맡는다.
///
/// 색 칸은 팔레트 색을 그대로 보여 주지만 치장 칸은 <b>이름</b>을 보여 준다 — 아이콘이 아직 없다.
/// <b>보유 개념이 없다</b> — 카탈로그의 전 항목을 고를 수 있다. 보유함 필터는 후속(#818 D)에서 얹는다.
/// </summary>
public class AccessoryPickerView : MonoBehaviour
{
    [Tooltip("이 줄이 고르는 슬롯")]
    [SerializeField] private EAccessorySlot m_slot;

    [Tooltip("치장 카탈로그 — Player 프리팹과 같은 에셋을 물릴 것")]
    [SerializeField] private AccessoryCatalog m_catalog;

    [Tooltip("칸을 넣을 부모 (Layout Group)")]
    [SerializeField] private RectTransform m_container;

    [Tooltip("칸 프리팹 — Button과 자식 TMP_Text 하나면 된다")]
    [SerializeField] private Button m_buttonPrefab;

    private readonly List<Button> m_buttons = new List<Button>();

    private void Awake()
    {
        if (m_catalog == null || m_container == null || m_buttonPrefab == null)
        {
            Debug.LogWarning($"[{nameof(AccessoryPickerView)}] 배선이 빠졌습니다 (#818)", this);
            enabled = false;
            return;
        }

        Build();
    }

    private void OnEnable()
    {
        GameSettings.OnAccessoryChanged += HandleAccessoryChanged;
        RefreshSelection();
    }

    private void OnDisable() => GameSettings.OnAccessoryChanged -= HandleAccessoryChanged;

    private void Build()
    {
        int count = m_catalog.CountOf(m_slot);
        for (int i = 0; i < count; i++)
        {
            int index = i; // 클로저가 루프 변수를 잡지 않게 사본을 넘긴다
            Button button = Instantiate(m_buttonPrefab, m_container);
            button.name = $"Accessory {index}";

            GameObject prefab = m_catalog.Get(m_slot, index);
            var label = button.GetComponentInChildren<TMP_Text>();
            if (label != null)
                label.text = prefab == null ? "없음" : Prettify(prefab.name);

            button.onClick.AddListener(() => GameSettings.SetAccessory(m_slot, index));
            m_buttons.Add(button);
        }

        RefreshSelection();
    }

    // 프리팹 이름을 그대로 쓰면 "Apo_Sheriff_Male_Hat_01"이 그대로 보인다 — 출처·성별 토막을
    // 걷어내 읽을 수 있게만 만든다. 제대로 된 이름·아이콘은 후속 슬라이스에서 붙인다.
    private static string Prettify(string name)
    {
        string[] tokens = name.Split('_');
        var kept = new List<string>(tokens.Length);
        foreach (string token in tokens)
            if (token != "Apo" && token != "Pol" && token != "Male" && token != "Female")
                kept.Add(token);

        return kept.Count == 0 ? name : string.Join(" ", kept);
    }

    private void HandleAccessoryChanged(EAccessorySlot slot)
    {
        if (slot == m_slot)
            RefreshSelection();
    }

    // 고른 칸만 상호작용을 끈다 — 색 칸의 SetSelected에 해당하는 최소 표시다
    private void RefreshSelection()
    {
        int selected = GameSettings.GetAccessory(m_slot);

        for (int i = 0; i < m_buttons.Count; i++)
            m_buttons[i].interactable = i != selected;
    }
}
