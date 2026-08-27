using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// HP 바 — 틀 안의 채움과 숫자를 그린다. (#691 → #894)
/// 좌하단 <c>HpPanel</c>에 붙으며, 값을 넣어 주는 쪽은 <see cref="PlayerHpUI"/>다.
/// 팀 상황 카드(<see cref="TeamStatusRowView"/>)도 같은 뷰를 작은 크기로 쓴다.
///
/// <b>순수 표현 컴포넌트로 유지할 것.</b> <see cref="DamageVignetteUI"/>와 같은 방침이다 —
/// "누가 언제 맞았나"·"내가 오너인가"는 전부 값을 넣어 주는 쪽이 알고, 여기는 비율만 그린다.
///
/// 로봇이라 피 대신 <b>시스템 손상</b> 언어를 쓴다 (GDD 7-5 — HP 0은 사망이 아니라 기능 정지,
/// 10-1은 HP를 '내구도'로 정의한다).
///
/// #894에서 기름통 은유(누유 줄기·바닥 고임)를 걷어냈다 — 새 아트가 기계식 게이지라 흘러내리는
/// 물질의 문법이 맞지 않는다. 피격 연출은 화면 비네트(<see cref="DamageVignetteUI"/>)가 이미
/// 맡고 있어 바에는 따로 두지 않는다.
/// </summary>
public class HpBarView : MonoBehaviour
{
    [Tooltip(
        "채움 — Image Type은 Filled, Fill Method는 Horizontal, Fill Origin은 Left. "
            + "[주의] Sprite가 비어 있으면 Unity가 Filled 타입을 무시하고 단순 사각형을 그려 "
            + "채움이 전혀 동작하지 않는다 (NpcHealthBarView와 같은 함정)"
    )]
    [SerializeField]
    private Image m_fill;

    [Tooltip("틀 안에 겹쳐 놓는 현재/최대 수치. 게이지로 바꿔도 숫자는 남긴다 — 정확한 값이 필요한 판단(진압봉 몇 대를 더 버티나)이 있다")]
    [SerializeField]
    private TextMeshProUGUI m_hpText;

    /// <summary>채움과 숫자를 값에 맞춘다.</summary>
    public void SetHealth(int current, int max)
    {
        if (m_fill != null)
            m_fill.fillAmount = max > 0 ? Mathf.Clamp01((float)current / max) : 0f;

        if (m_hpText != null)
            m_hpText.text = current + " / " + max;
    }
}
