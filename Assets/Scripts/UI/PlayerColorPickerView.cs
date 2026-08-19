using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 로비의 로봇 색 팔레트 (#432) — 팔레트 에셋의 색만큼 칸을 찍어 내고, 고르면
/// <see cref="GameSettings.PlayerColorIndex"/>에 쓴다.
///
/// <b>여기서 네트워크를 건드리지 않는다.</b> 값의 출처는 로컬 설정 하나이고, 그것을 남에게 나르는 일은
/// 로비에서는 <see cref="SessionRoster"/>가, 게임 씬에서는 <c>PlayerCosmetics</c>가 각자 맡는다.
/// 이 화면은 "무엇을 골랐는가"만 정한다.
///
/// 팔레트가 늘어도 여기 코드는 그대로다 — 칸 수는 에셋이 정한다.
/// </summary>
public class PlayerColorPickerView : MonoBehaviour
{
    [Tooltip("색 팔레트 — 로비 아바타·Player 프리팹과 같은 에셋을 물릴 것")]
    [SerializeField] private PlayerColorPalette m_palette;

    [Tooltip("칸을 넣을 부모 (Grid/Horizontal Layout Group)")]
    [SerializeField] private RectTransform m_container;

    [SerializeField] private PlayerColorSwatchView m_swatchPrefab;

    private readonly List<PlayerColorSwatchView> m_swatches = new List<PlayerColorSwatchView>();

    private void Awake()
    {
        if (m_palette == null || m_container == null || m_swatchPrefab == null)
        {
            Debug.LogWarning($"[{nameof(PlayerColorPickerView)}] 배선이 빠졌습니다 — 팔레트를 그리지 않습니다. (#432)", this);
            enabled = false;
            return;
        }

        Build();
    }

    // 설정이 다른 경로로 바뀌어도 표시가 따라간다 — 지금은 이 화면뿐이지만, 표시와 값의 출처를
    // 갈라 두면 나중에 고르는 자리가 늘어도 여기를 고칠 일이 없다.
    private void OnEnable()
    {
        GameSettings.OnPlayerColorChanged += HandleColorChanged;
        RefreshSelection();
    }

    private void OnDisable() => GameSettings.OnPlayerColorChanged -= HandleColorChanged;

    private void Build()
    {
        for (int i = 0; i < m_palette.Count; i++)
        {
            int index = i; // 클로저가 루프 변수를 잡지 않게 사본을 넘긴다
            PlayerColorSwatchView swatch = Instantiate(m_swatchPrefab, m_container);
            swatch.name = $"Swatch {index}";
            swatch.Bind(m_palette.Get(index), () => Pick(index));
            m_swatches.Add(swatch);
        }

        RefreshSelection();
    }

    private void Pick(int index)
    {
        // 같은 색을 다시 눌러도 대입한다 — 값이 같으면 GameSettings가 이벤트만 한 번 더 낼 뿐이다
        GameSettings.PlayerColorIndex = index;
    }

    private void HandleColorChanged(int _) => RefreshSelection();

    private void RefreshSelection()
    {
        int selected = GameSettings.PlayerColorIndex;

        for (int i = 0; i < m_swatches.Count; i++)
            m_swatches[i].SetSelected(i == selected);
    }
}
