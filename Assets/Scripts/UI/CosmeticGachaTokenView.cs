using TMPro;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 자판기에 붙는 남은 토큰 표시 (#818 D) — <see cref="CosmeticGachaMachine"/>과 같은 오브젝트에 둔다.
///
/// 조준 안내(<see cref="InteractPrompts.Gacha"/>)에 숫자를 끼우지 않는 이유는 그 문구가 20종을
/// 한 벌만 캐시해 두고 참조 비교로 재구독을 판정하는 자리라서다 — 개체마다 다른 인자를 넣을 수 없다.
/// 그래서 기계 쪽에 따로 붙인다. 계정 값이라 조준하지 않아도 보이는 것이 낫기도 하다.
/// </summary>
public class CosmeticGachaTokenView : MonoBehaviour
{
    [Tooltip("남은 토큰을 적을 3D 라벨")]
    [SerializeField]
    private TMP_Text m_label;

    [Tooltip("표시 문구 — {0}에 남은 토큰 수가 들어간다")]
    [SerializeField]
    private LocalizedString m_format;

    private void OnEnable()
    {
        // 키가 안 붙었으면 구독하지 않는다 — 빈 LocalizedString을 구독하면 조회할 때마다
        // 에러가 쌓인다 (EmoteWheelSlotView가 같은 이유로 IsEmpty를 본다)
        if (m_format.IsEmpty)
        {
            Debug.LogWarning($"[{nameof(CosmeticGachaTokenView)}] 표시 문구 키가 연결되지 않았습니다 (#818 D)", this);
            return;
        }

        // 인자를 먼저 넣고 구독한다 — 순서를 어기면 첫 발화가 인자 없는 문장으로 나간다
        m_format.Arguments = new object[] { CosmeticInventory.Tokens };
        m_format.StringChanged += SetText;
        CosmeticInventory.OnTokensChanged += Refresh;
    }

    private void OnDisable()
    {
        if (m_format.IsEmpty)
            return;

        m_format.StringChanged -= SetText;
        CosmeticInventory.OnTokensChanged -= Refresh;
    }

    // 토큰이 오가면 인자를 갈고 다시 읽는다 — RefreshString이 구독자에게 새 문장을 보낸다
    private void Refresh()
    {
        m_format.Arguments = new object[] { CosmeticInventory.Tokens };
        m_format.RefreshString();
    }

    private void SetText(string value)
    {
        if (m_label != null)
            m_label.text = value;
    }
}
