using UnityEngine;

/// <summary>
/// 로봇 색 팔레트 (#432) — 고를 수 있는 색을 순서대로 담는다. 인덱스가 곧 동기화 값이므로
/// <b>중간에 끼워 넣지 말 것</b>: 뒤 항목이 밀려 예전에 고른 색이 바뀐다. 추가는 항상 끝에 한다.
/// 설계 정본: docs/design/player-color.md
/// </summary>
[CreateAssetMenu(fileName = "PlayerColors", menuName = "Scriptable Objects/PlayerColorPalette")]
public class PlayerColorPalette : ScriptableObject
{
    [Tooltip("고를 수 있는 색 — 인덱스가 곧 동기화 값이라 순서를 바꾸지 말 것")]
    [SerializeField] private Color[] m_colors;

    public int Count => m_colors != null ? m_colors.Length : 0;

    /// <summary>
    /// 인덱스의 색. 범위 밖이면 <b>자르고</b>(모듈로가 아니다) 비어 있으면 흰색이다 —
    /// 자르면 어느 피어에서든 같은 색이 나온다. 목록이 줄어든 뒤의 옛 저장값이 이 자리에 온다.
    /// </summary>
    public Color Get(int index)
    {
        if (Count == 0)
            return Color.white;

        return m_colors[Mathf.Clamp(index, 0, Count - 1)];
    }
}
